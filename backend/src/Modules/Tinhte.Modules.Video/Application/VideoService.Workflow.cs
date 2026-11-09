using System.Security.Claims;
using Npgsql;
using Tinhte.Modules.Video.Domain;
using Tinhte.Modules.Video.Infrastructure;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Video.Application;

/// <summary>
/// Quy trình duyệt + xuất bản + gỡ video (WBS-040; FR-VID-008/010, FR-CMS-002/010). Theo quy ước duyệt bài WBS-024 (ADR-0005):
/// <code>
///   DRAFT ──submit──▶ IN_REVIEW ──approve──▶ APPROVED ──publish──▶ PUBLISHED ──unpublish (lý do)──▶ UNPUBLISHED
///                          ├──request-changes (lý do)──▶ CHANGES_REQUESTED ─┐
///                          └──reject (lý do)───────────▶ REJECTED ──────────┴─ PATCH tạo revision mới (DRAFT)
/// </code>
/// <list type="bullet">
///   <item>Trạng thái video đi theo working revision; bản công khai giữ nguyên tới khi publish revision mới (publish pointer).</item>
///   <item>Gửi duyệt cần media; duyệt chốt đúng thế hệ media (<c>approved_media_generation</c>).</item>
///   <item>Tách nhiệm vụ: không duyệt/xuất bản revision mình là tác giả hoặc đã sửa → 403 SOD_VIOLATION.</item>
///   <item>Chốt chặn publish: media READY + VIDEO (422 MEDIA_NOT_READY), quyền media và video CLEARED còn hạn (422 RIGHTS_INVALID),
///         media không đổi thế hệ từ lúc duyệt (409). SPONSORED: trigger 0307 (cờ, duyệt tài trợ, cửa sổ). Trigger 0334 kiểm lại ở DB.</item>
///   <item>Gỡ: xóa publish pointer ngay trong transaction (origin không phục vụ nữa), ghi người/lúc/lý do.
///         Thu hồi playback grant, purge CDN, gỡ khỏi feed/search: TODO(WBS-039/128, WBS-020 outbox).</item>
///   <item>Thứ tự lỗi theo catalog: 403 quyền → 404 → 403 SoD → 428/412 → 422 → 409.</item>
/// </list>
/// Quyết định của service, không phải của API: KHÔNG đặt quyền CLEARED (WBS-023), KHÔNG đổi visibility media (WBS-039).
/// </summary>
public sealed partial class VideoService
{
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 2000;

    /// <summary>postAdminVideosVideoidSubmit: gửi working revision DRAFT (có media) đi duyệt. Quyền = quyền sửa video.</summary>
    public async Task<AdminVideo> SubmitAsync(ClaimsPrincipal user, Guid videoId, Func<long> requireIfMatch, CancellationToken ct)
    {
        var actorId = user.GetUserId() ?? throw new DomainProblemException(ErrorCodes.Unauthenticated, "Authentication required", "Sign in to the CMS.");
        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var video = await LockAsync(connection, tx, videoId, ct) ?? throw NotFound();
            RequireEditPermission(user, video.OwnerId);
            var expected = requireIfMatch();
            ETags.EnsureMatches(video.RowVersion, expected);

            var working = video.WorkingRevisionId is { } id ? await LoadRevisionAsync(connection, tx, id, ct) : null;
            if (!VideoStatuses.Editable.Contains(video.Status) || working is null
                || working.Status != VideoRevisionStatuses.Draft || working.RevisionId == video.PublicRevisionId)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "Nothing to submit",
                    $"Only a DRAFT working revision that is not public can be submitted (video {video.Status}, revision {working?.Status ?? "none"}). Edit the video first.");
            }

            var media = working.MediaId is { } mediaId ? await LoadMediaAsync(connection, tx, mediaId, ct) : null;
            if (media is null || media.Unusable)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "Media required",
                    media is null ? "Attach an uploaded video before submitting for review."
                        : $"The attached media is {media.Status}{(media.RightsBlocked ? $" with rights {media.RightsStatus}" : "")}: attach another file before submitting.");
            }

            await ExecAsync(connection, tx, """
                UPDATE tt.video_revisions SET status = 'IN_REVIEW', submitted_by = @actor, submitted_at = now() WHERE id = @rev
                """, ct, ("rev", working.RevisionId), ("actor", actorId));
            await UpdateVideoStatusAsync(connection, tx, videoId, expected, VideoStatuses.InReview, assignee: null, ct);
            return (await LoadAsync(connection, tx, videoId, ct))!;
        }, ct);
    }

    /// <summary>postAdminVideosVideoidApprove: content.approve (MFA) + SoD; duyệt đúng revision đang chờ và chốt thế hệ media.</summary>
    public async Task<AdminVideo> ApproveAsync(Guid actorId, Guid videoId, Guid revisionId, string? note, Func<long> requireIfMatch, CancellationToken ct) =>
        await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var video = await LockAsync(connection, tx, videoId, ct) ?? throw NotFound();
            var revision = await LoadRevisionAsync(connection, tx, revisionId, ct) is { } r && r.VideoId == videoId
                ? r : throw Invalid("revisionId", "The revision does not belong to this video.");
            await RequireSeparationOfDutiesAsync(connection, tx, revisionId, actorId, "approve", ct);
            var expected = requireIfMatch();
            ETags.EnsureMatches(video.RowVersion, expected);

            var media = revision.MediaId is { } mediaId ? await LoadMediaAsync(connection, tx, mediaId, ct) : null;
            if (media is not null && (media.Unusable || media.RightsBlocked))
            {
                throw Invalid("revisionId", $"The media of this revision is {media.Status} with rights {media.RightsStatus}: it cannot be approved.");
            }

            RequirePendingReview(video, revision);
            if (media is null) throw new DomainProblemException(ErrorCodes.StateConflict, "Media required", "The revision has no media to approve.");

            await using (var command = VideoSql.Command(connection, tx, """
                UPDATE tt.video_revisions
                   SET status = 'APPROVED', reviewed_by = @actor, reviewed_at = now(), review_note = @note, approved_media_generation = @generation
                 WHERE id = @rev
                """).WithUuid("rev", revisionId).WithUuid("actor", actorId).WithText("note", string.IsNullOrWhiteSpace(note) ? null : note.Trim()))
            {
                command.Parameters.AddWithValue("generation", media.Generation);
                await command.ExecuteNonQueryAsync(ct);
            }

            await UpdateVideoStatusAsync(connection, tx, videoId, expected, VideoStatuses.Approved, assignee: null, ct);
            return (await LoadAsync(connection, tx, videoId, ct))!;
        }, ct);

    /// <summary>
    /// postAdminVideosVideoidRequestChanges (content.review) / postAdminVideosVideoidReject (content.approve): trả revision đang chờ
    /// kèm lý do; người gửi duyệt thành người phụ trách tiếp theo.
    /// </summary>
    public async Task<AdminVideo> ReturnAsync(Guid actorId, Guid videoId, string? reason, bool reject, Func<long> requireIfMatch, CancellationToken ct) =>
        await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var video = await LockAsync(connection, tx, videoId, ct) ?? throw NotFound();
            var expected = requireIfMatch();
            ETags.EnsureMatches(video.RowVersion, expected);
            var text = CheckReason(reason);
            var revision = video.WorkingRevisionId is { } id ? await LoadRevisionAsync(connection, tx, id, ct) : null;
            RequirePendingReview(video, revision);

            var status = reject ? VideoRevisionStatuses.Rejected : VideoRevisionStatuses.ChangesRequested;
            await ExecAsync(connection, tx, """
                UPDATE tt.video_revisions SET status = @status, reviewed_by = @actor, reviewed_at = now(), review_note = @note WHERE id = @rev
                """, ct, ("rev", revision!.RevisionId), ("actor", actorId), ("note", text), ("status", status));
            await UpdateVideoStatusAsync(connection, tx, videoId, expected, status, assignee: revision.SubmittedBy ?? revision.AuthorId, ct);
            return (await LoadAsync(connection, tx, videoId, ct))!;
        }, ct);

    /// <summary>
    /// postAdminVideosVideoidPublish: content.publish (MFA) + SoD; đặt publish pointer sang revision APPROVED khi qua mọi chốt chặn.
    /// Một transaction: đổi con trỏ (trigger 0307 + 0334 kiểm lại) → revision cũ SUPERSEDED, revision mới PUBLISHED.
    /// </summary>
    public async Task<AdminVideo> PublishAsync(Guid actorId, Guid videoId, Guid revisionId, Func<long> requireIfMatch, CancellationToken ct) =>
        await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var video = await LockAsync(connection, tx, videoId, ct) ?? throw NotFound();
            var revision = await LoadRevisionAsync(connection, tx, revisionId, ct) is { } r && r.VideoId == videoId
                ? r : throw Invalid("revisionId", "The revision does not belong to this video.");
            await RequireSeparationOfDutiesAsync(connection, tx, revisionId, actorId, "publish", ct);
            var expected = requireIfMatch();
            ETags.EnsureMatches(video.RowVersion, expected);

            // 422: media phát được, quyền media + video còn hiệu lực.
            var media = revision.MediaId is { } mediaId ? await LoadMediaAsync(connection, tx, mediaId, ct) : null;
            if (media is null || media.Status != MediaStatuses.Ready || media.MediaType != MediaTypes.Video)
            {
                throw new DomainProblemException(ErrorCodes.MediaNotReady, "Media not ready",
                    media is null ? "The revision has no media." : $"The media is {media.Status}: only a READY video can be published.");
            }

            if (!media.RightsValid)
            {
                throw new DomainProblemException(ErrorCodes.RightsInvalid, "Rights invalid",
                    $"Media rights are {media.RightsStatus}{(media.RightsExpiresAt is { } me ? $", expiring {me:O}" : "")}: publishing requires CLEARED, unexpired rights.");
            }

            if (!video.RightsValid)
            {
                throw new DomainProblemException(ErrorCodes.RightsInvalid, "Rights invalid",
                    $"Video rights are {video.RightsStatus}{(video.RightsExpiresAt is { } ve ? $", expiring {ve:O}" : "")}: publishing requires CLEARED, unexpired rights.");
            }

            // 409: chỉ revision APPROVED (do bước duyệt + SoD đặt); media không được xử lý lại sau khi duyệt.
            if (video.Status == VideoStatuses.Retracted)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict", "A retracted video cannot be published again.");
            }

            if (revision.Status != VideoRevisionStatuses.Approved)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict",
                    $"Only an APPROVED revision can be published; revision {revision.Number} is {revision.Status}.");
            }

            if (revision.ApprovedMediaGeneration != media.Generation)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "Media changed since approval",
                    $"The media was reprocessed after approval (generation {revision.ApprovedMediaGeneration} → {media.Generation}): review it again.");
            }

            await using (var pointer = VideoSql.Command(connection, tx, """
                UPDATE tt.videos
                   SET public_revision_id = @rev, status = 'PUBLISHED', assignee_id = NULL, scheduled_at = NULL
                 WHERE id = @id AND row_version = @expected
                """).WithUuid("rev", revisionId).WithUuid("id", videoId))
            {
                pointer.Parameters.AddWithValue("expected", expected);
                if (await pointer.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            }

            await using (var revisions = VideoSql.Command(connection, tx, """
                UPDATE tt.video_revisions SET status = 'SUPERSEDED' WHERE id = @old AND id <> @rev AND status = 'PUBLISHED';
                UPDATE tt.video_revisions SET status = 'PUBLISHED', published_at = now(), published_by = @actor WHERE id = @rev;
                """).WithUuid("old", video.PublicRevisionId).WithUuid("rev", revisionId).WithUuid("actor", actorId))
            {
                await revisions.ExecuteNonQueryAsync(ct);
            }

            // TODO(WBS-020): phát sự kiện ContentPublished qua outbox trong cùng transaction.
            return (await LoadAsync(connection, tx, videoId, ct))!;
        }, ct);

    /// <summary>
    /// postAdminVideosVideoidUnpublish (content.takedown, MFA): gỡ khẩn cấp, If-Match tùy chọn. Xóa publish pointer → origin không
    /// phục vụ bản công khai nữa; public revision → SUPERSEDED; ghi người/lúc/lý do gỡ.
    /// </summary>
    public async Task<AdminVideo> UnpublishAsync(Guid actorId, Guid videoId, string? reason, long? ifMatch, CancellationToken ct) =>
        await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var video = await LockAsync(connection, tx, videoId, ct) ?? throw NotFound();
            if (ifMatch is { } expected) ETags.EnsureMatches(video.RowVersion, expected);
            var text = CheckReason(reason);
            if (video.PublicRevisionId is not { } publicRevisionId)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "Not published", $"The video is {video.Status} and has no public revision to take down.");
            }

            await using (var update = VideoSql.Command(connection, tx, """
                UPDATE tt.videos
                   SET public_revision_id = NULL, status = 'UNPUBLISHED', scheduled_at = NULL,
                       unpublished_at = now(), unpublished_by = @actor, unpublish_reason = @reason
                 WHERE id = @id AND row_version = @current
                """).WithUuid("actor", actorId).WithText("reason", text).WithUuid("id", videoId))
            {
                update.Parameters.AddWithValue("current", video.RowVersion);
                if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            }

            await ExecAsync(connection, tx, "UPDATE tt.video_revisions SET status = 'SUPERSEDED' WHERE id = @rev AND status = 'PUBLISHED'",
                ct, ("rev", publicRevisionId));

            // TODO(WBS-039/128, WBS-020): thu hồi playback grant, purge CDN, gỡ khỏi feed/search/sitemap qua outbox (đo SLA lan truyền).
            return (await LoadAsync(connection, tx, videoId, ct))!;
        }, ct);

    /// <summary>ActionReason.reason: bắt buộc, 5–2000 ký tự.</summary>
    public static string CheckReason(string? reason)
    {
        var text = reason?.Trim();
        if (text is null || text.Length < ReasonMinLength)
        {
            throw new DomainProblemException(ErrorCodes.ReasonRequired, "Reason required",
                $"Give a reason of at least {ReasonMinLength} characters.",
                new Dictionary<string, string[]> { ["reason"] = [$"At least {ReasonMinLength} characters."] });
        }

        if (text.Length > ReasonMaxLength) throw Invalid("reason", $"At most {ReasonMaxLength} characters.");
        return text;
    }

    // ---------------------------------------------------------------- nội bộ

    private sealed record MediaGate(string Status, string? MediaType, int Generation, string RightsStatus, DateTimeOffset? RightsExpiresAt, bool RightsValid)
    {
        public bool Unusable => MediaStatuses.Unusable.Contains(Status);

        public bool RightsBlocked => MediaRights.Blocked.Contains(RightsStatus) || (RightsExpiresAt is { } e && e <= DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Revision đang chờ quyết định: là working revision và IN_REVIEW. Video có thể đang UNPUBLISHED (bị gỡ khi bản mới đang chờ) —
    /// sau khi gỡ không còn publish pointer nên duyệt tiếp không làm lộ bản cũ.
    /// </summary>
    private static void RequirePendingReview(LockedVideo video, CurrentRevision? revision)
    {
        if (video.Status == VideoStatuses.Retracted || revision is null || revision.Status != VideoRevisionStatuses.InReview
            || revision.RevisionId != video.WorkingRevisionId)
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "Not in review",
                $"The video is {video.Status} and revision {(revision is null ? "-" : $"{revision.Number} is {revision.Status}")}: only the working revision IN_REVIEW can be reviewed.");
        }
    }

    /// <summary>SoD (SOD_VIOLATION): người tạo hoặc người sửa revision không được tự duyệt/xuất bản nó (giống WBS-024).</summary>
    private static async Task RequireSeparationOfDutiesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid revisionId, Guid actorId, string action, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx,
            "SELECT @actor IN (author_id, updated_by) IS TRUE FROM tt.video_revisions WHERE id = @rev")
            .WithUuid("rev", revisionId).WithUuid("actor", actorId);
        if (await command.ExecuteScalarAsync(ct) is true)
        {
            throw new DomainProblemException(ErrorCodes.SodViolation, "Separation of duties",
                $"You wrote or edited this revision, so you cannot {action} it. Ask another approver.");
        }
    }

    /// <summary>Trạng thái + quyền của media, khóa FOR SHARE: pipeline/quyền không đổi giữa lúc kiểm và lúc ghi.</summary>
    private static async Task<MediaGate?> LoadMediaAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid mediaId, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx, """
            SELECT status, media_type, generation, rights_status, rights_expires_at,
                   rights_status = 'CLEARED' AND (rights_expires_at IS NULL OR rights_expires_at > now())
              FROM tt.media_assets WHERE id = @id FOR SHARE
            """).WithUuid("id", mediaId);
        await using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new MediaGate(r.GetString(0), VideoSql.NullableText(r, 1), r.GetInt32(2), r.GetString(3), VideoSql.NullableTime(r, 4), r.GetBoolean(5))
            : null;
    }

    private static async Task UpdateVideoStatusAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid videoId, long expected, string status, Guid? assignee, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx,
            "UPDATE tt.videos SET status = @status, assignee_id = @assignee WHERE id = @id AND row_version = @expected")
            .WithText("status", status).WithUuid("assignee", assignee).WithUuid("id", videoId);
        command.Parameters.AddWithValue("expected", expected);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
    }

    private static async Task ExecAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = VideoSql.Command(connection, tx, sql);
        foreach (var (name, value) in args)
        {
            switch (value)
            {
                case Guid g: command.WithUuid(name, g); break;
                default: command.WithText(name, (string?)value); break;
            }
        }

        await command.ExecuteNonQueryAsync(ct);
    }
}

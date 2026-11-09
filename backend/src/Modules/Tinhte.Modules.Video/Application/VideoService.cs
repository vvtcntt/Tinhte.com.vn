using System.Security.Claims;
using System.Text;
using Npgsql;
using Tinhte.Modules.Video.Domain;
using Tinhte.Modules.Video.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Video.Application;

/// <summary>
/// Video trong CMS (WBS-046; FR-CMS-001/006, FR-VID-008): tạo bản nháp + working revision, sửa working revision, gắn media.
/// Cùng mô hình revision với bài viết (ADR-0005, migration 0331): revision DRAFT chưa public sửa tại chỗ, ngược lại PATCH tạo
/// revision mới. Media là nội dung của revision (migration 0334). Gửi duyệt/duyệt/xuất bản/gỡ: VideoService.Workflow.cs (WBS-040).
/// </summary>
public sealed partial class VideoService(ITinhteDatabase database)
{
    // ---------------------------------------------------------------- đọc

    public async Task<AdminVideo> GetAsync(Guid videoId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        return await LoadAsync(connection, null, videoId, ct) ?? throw NotFound();
    }

    public async Task<IReadOnlyList<(AdminVideo Video, DateTimeOffset CreatedAt)>> ListAsync(
        AdminVideoFilter filter, AdminVideoPosition? after, int limitPlusOne, CancellationToken ct)
    {
        var sql = new StringBuilder(VideoSql.AdminSelect).Append(" WHERE TRUE");
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = VideoSql.Command(connection, null, "");
        if (filter.Statuses.Count > 0)
        {
            sql.Append(" AND v.status = ANY(@statuses)");
            command.Parameters.AddWithValue("statuses", filter.Statuses.ToArray());
        }

        if (filter.OwnerId is { } owner) { sql.Append(" AND v.owner_id = @owner"); command.WithUuid("owner", owner); }
        if (filter.AssigneeId is { } assignee) { sql.Append(" AND v.assignee_id = @assignee"); command.WithUuid("assignee", assignee); }
        if (filter.UpdatedFrom is { } from) { sql.Append(" AND v.updated_at >= @from"); command.WithTime("from", from); }
        if (filter.UpdatedTo is { } to) { sql.Append(" AND v.updated_at <= @to"); command.WithTime("to", to); }
        if (filter.Q is { } q)
        {
            sql.Append(@" AND (w.title ILIKE @q ESCAPE '\' OR v.slug ILIKE @q ESCAPE '\')");
            command.WithText("q", VideoSql.LikePattern(q));
        }

        // Keyset theo (khóa sắp xếp, id): ổn định khi có video mới chèn vào (ADR-0003 §4).
        var (order, compare, key) = filter.Sort switch
        {
            "updatedAt_asc" => ("v.updated_at ASC, v.id ASC", ">", "v.updated_at"),
            "createdAt_desc" => ("v.created_at DESC, v.id DESC", "<", "v.created_at"),
            "title_asc" => ("COALESCE(w.title, '') ASC, v.id ASC", ">", "COALESCE(w.title, '')"),
            _ => ("v.updated_at DESC, v.id DESC", "<", "v.updated_at"),
        };
        if (after is not null)
        {
            var byTitle = filter.Sort == "title_asc";
            if (byTitle ? after.Title is null : after.At is null)
            {
                throw new DomainProblemException(ErrorCodes.InvalidCursor, "Invalid cursor", "The cursor is not valid for this list. Restart from the first page.");
            }

            sql.Append($" AND ({key}, v.id) {compare} (@after_key, @after_id)");
            if (byTitle) command.WithText("after_key", after.Title);
            else command.WithTime("after_key", after.At);
            command.WithUuid("after_id", after.Id);
        }

        sql.Append(" ORDER BY ").Append(order).Append(" LIMIT @limit");
        command.Parameters.AddWithValue("limit", limitPlusOne);
        command.CommandText = sql.ToString();

        var rows = new List<(AdminVideo, DateTimeOffset)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = VideoSql.ReadAdmin(reader);
            rows.Add((row.Video, row.CreatedAt));
        }

        return rows;
    }

    // ---------------------------------------------------------------- tạo (postAdminVideos)

    public async Task<AdminVideo> CreateAsync(Guid actorId, CreateVideoRequest request, CancellationToken ct)
    {
        if (request.RightsSource is not null) throw Invalid("rightsSource", "Not supported yet: source/license taxonomy is delivered by WBS-023.");
        var kind = request.ContentKind ?? VideoContentKinds.Editorial;
        var sponsorship = CheckSponsorship(kind, request.Sponsorship);

        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var videoId = Guid.CreateVersion7();
            var revisionId = Guid.CreateVersion7();
            if (request.MediaId is { } media) await EnsureBindableMediaAsync(connection, tx, media, videoId, ct);

            // tt.videos.media_id = media của public revision (trigger 0334): media của bản nháp nằm trên revision.
            await using (var insert = VideoSql.Command(connection, tx, """
                INSERT INTO tt.videos (id, slug, status, owner_id) VALUES (@id, @slug, 'DRAFT', @actor)
                """).WithUuid("id", videoId).WithText("slug", VideoSlug.For(request.Title, videoId)).WithUuid("actor", actorId))
            {
                await insert.ExecuteNonQueryAsync(ct);
            }

            await InsertRevisionAsync(connection, tx,
                new RevisionContent(videoId, revisionId, 1, request.Title, request.Summary, request.MediaId, kind, sponsorship, null), actorId, ct);

            await using (var link = VideoSql.Command(connection, tx, "UPDATE tt.videos SET working_revision_id = @rev WHERE id = @id")
                .WithUuid("rev", revisionId).WithUuid("id", videoId))
            {
                await link.ExecuteNonQueryAsync(ct);
            }

            return (await LoadAsync(connection, tx, videoId, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- sửa (patchAdminVideosVideoid)

    /// <summary>Quyền sửa phụ thuộc dữ liệu: video của mình cần content.write_own, của người khác cần content.write_any.</summary>
    public static Guid RequireEditPermission(ClaimsPrincipal user, Guid? ownerId) =>
        user.RequirePermission(ownerId is { } owner && owner == user.GetUserId() ? Permissions.ContentWriteOwn : Permissions.ContentWriteAny);

    public async Task<AdminVideo> PatchAsync(ClaimsPrincipal user, Guid videoId, long expectedRowVersion, VideoPatch patch, CancellationToken ct)
    {
        var actorId = user.GetUserId() ?? throw new DomainProblemException(ErrorCodes.Unauthenticated, "Authentication required", "Sign in to the CMS.");
        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var video = await LockAsync(connection, tx, videoId, ct) ?? throw NotFound();
            RequireEditPermission(user, video.OwnerId);
            ETags.EnsureMatches(video.RowVersion, expectedRowVersion);
            if (!VideoStatuses.Editable.Contains(video.Status))
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict",
                    $"A video in status {video.Status} cannot be edited; finish or withdraw the editorial workflow first.");
            }

            var baseRevisionId = video.WorkingRevisionId ?? video.PublicRevisionId ?? await LatestRevisionAsync(connection, tx, videoId, ct)
                ?? throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict", "The video has no revision to edit.");
            var current = (await LoadRevisionAsync(connection, tx, baseRevisionId, ct))!;

            // Revision đang chờ quyết định duyệt/xuất bản (cả khi video đã bị gỡ — UNPUBLISHED vẫn sửa được): quyết định trước, sửa sau.
            if (VideoRevisionStatuses.Pending.Contains(current.Status) && current.RevisionId != video.PublicRevisionId)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict",
                    $"Revision {current.Number} is {current.Status}: wait for the review/publish decision before editing.");
            }

            // Media là nội dung của revision (0334): đổi media của video đã public = revision mới, bản công khai giữ media cũ tới khi publish.
            var next = Apply(current, patch);
            if (next.MediaId != current.MediaId && next.MediaId is { } media) await EnsureBindableMediaAsync(connection, tx, media, videoId, ct);

            var workingRevisionId = video.WorkingRevisionId;
            var status = video.Status;
            var editInPlace = current.Status == VideoRevisionStatuses.Draft && current.RevisionId == video.WorkingRevisionId
                && current.RevisionId != video.PublicRevisionId;
            if (editInPlace)
            {
                await UpdateRevisionAsync(connection, tx, next, sponsorshipChanged: !SameSponsorship(current.Sponsorship, next.Sponsorship), actorId, ct);
            }
            else
            {
                // Revision đã đóng băng (bị trả/từ chối/đang public/đã thay): tạo revision mới, bản công khai giữ nguyên.
                var number = await NextRevisionNumberAsync(connection, tx, videoId, ct);
                next = next with { RevisionId = Guid.CreateVersion7(), Number = number, ChangeReason = patch.ChangeReason.IsSet ? patch.ChangeReason.Value : null };
                await InsertRevisionAsync(connection, tx, next, actorId, ct);
                workingRevisionId = next.RevisionId;
                if (status is VideoStatuses.ChangesRequested or VideoStatuses.Rejected) status = VideoStatuses.Draft;
            }

            await using var update = VideoSql.Command(connection, tx, """
                UPDATE tt.videos SET working_revision_id = @working, status = @status WHERE id = @id AND row_version = @expected
                """)
                .WithUuid("working", workingRevisionId)
                .WithText("status", status)
                .WithUuid("id", videoId);
            update.Parameters.AddWithValue("expected", expectedRowVersion);
            if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();

            return (await LoadAsync(connection, tx, videoId, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- nội bộ

    private sealed record LockedVideo(
        string Status, long RowVersion, Guid? OwnerId, Guid? WorkingRevisionId, Guid? PublicRevisionId, string RightsStatus, DateTimeOffset? RightsExpiresAt,
        bool RightsValid);

    private sealed record RevisionContent(
        Guid VideoId, Guid RevisionId, int Number, string Title, string? Summary, Guid? MediaId, string ContentKind, VideoSponsorshipInput? Sponsorship,
        string? ChangeReason);

    private sealed record CurrentRevision(Guid RevisionId, Guid VideoId, int Number, string Status, string Title, string? Summary, Guid? MediaId,
        string ContentKind, VideoSponsorshipInput? Sponsorship, string? ChangeReason, Guid? AuthorId, Guid? SubmittedBy, int? ApprovedMediaGeneration);

    private static DomainProblemException NotFound() => new(ErrorCodes.NotFound, "Video not found", "No video with this id.");

    private static DomainProblemException Invalid(string field, string message) =>
        new(ErrorCodes.ValidationFailed, "Validation failed", message, new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>SponsorshipRequiredRule: SPONSORED cần sponsorship đầy đủ (422 SPONSORSHIP_LABEL_REQUIRED); EDITORIAL không mang sponsorship.</summary>
    private static VideoSponsorshipInput? CheckSponsorship(string kind, VideoSponsorshipInput? sponsorship)
    {
        if (kind == VideoContentKinds.Sponsored && sponsorship is null)
        {
            throw new DomainProblemException(ErrorCodes.SponsorshipLabelRequired, "Sponsorship label required",
                "contentKind SPONSORED requires a complete sponsorship (sponsorName, sponsorReference, evidenceRef, validFrom, validTo).");
        }

        if (kind == VideoContentKinds.Editorial && sponsorship is not null) throw Invalid("sponsorship", "Only SPONSORED content carries sponsorship.");
        if (sponsorship is { ValidFrom: { } from, ValidTo: { } to } && to <= from) throw Invalid("sponsorship.validTo", "validTo must be after validFrom.");
        return sponsorship;
    }

    private static bool SameSponsorship(VideoSponsorshipInput? a, VideoSponsorshipInput? b) => a == b;

    private static RevisionContent Apply(CurrentRevision current, VideoPatch patch)
    {
        var kind = patch.ContentKind.IsSet ? patch.ContentKind.Value : current.ContentKind;
        var sponsorship = patch.Sponsorship.IsSet ? patch.Sponsorship.Value
            : kind == VideoContentKinds.Editorial ? null   // chuyển sang EDITORIAL: bỏ sponsorship cũ
            : current.Sponsorship;
        CheckSponsorship(kind, sponsorship);
        return new RevisionContent(current.VideoId, current.RevisionId, current.Number,
            patch.Title.IsSet ? patch.Title.Value : current.Title,
            patch.Summary.IsSet ? patch.Summary.Value : current.Summary,
            patch.MediaId.IsSet ? patch.MediaId.Value : current.MediaId,
            kind, sponsorship,
            patch.ChangeReason.IsSet ? patch.ChangeReason.Value : current.ChangeReason);
    }

    /// <summary>
    /// Media gắn được vào video (FR-CMS-009): tải lên qua intent, loại VIDEO, chưa hỏng/bị từ chối, quyền chưa bị hạn chế/thu hồi/hết hạn,
    /// chưa gắn với video khác (ở bất kỳ revision nào). UNVERIFIED vẫn gắn được vào bản nháp — publish mới đòi CLEARED (WBS-040).
    /// Khóa FOR UPDATE dòng media để hai video không cùng lúc giành một media.
    /// </summary>
    private static async Task EnsureBindableMediaAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid mediaId, Guid videoId, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx, """
            SELECT m.media_type, m.status, m.rights_status, m.rights_expires_at IS NOT NULL AND m.rights_expires_at <= now(),
                   EXISTS (SELECT 1 FROM tt.video_revisions r WHERE r.media_id = m.id AND r.video_id <> @video)
              FROM tt.media_assets m WHERE m.id = @id AND m.storage_key IS NOT NULL FOR UPDATE OF m
            """).WithUuid("id", mediaId).WithUuid("video", videoId);
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw Invalid("mediaId", "Unknown media.");
        var (type, status, rights, rightsExpired, boundElsewhere) = (r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4));
        if (type != MediaTypes.Video) throw Invalid("mediaId", "Only a VIDEO media asset can be attached to a video.");
        if (MediaStatuses.Unusable.Contains(status)) throw Invalid("mediaId", $"The media is {status} and cannot be attached; upload the file again.");
        if (MediaRights.Blocked.Contains(rights) || rightsExpired) throw Invalid("mediaId", $"The media rights are {(rightsExpired ? "EXPIRED" : rights)}; it cannot be attached.");
        if (boundElsewhere)
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "Media in use", "This media is already attached to another video.");
        }
    }

    private static async Task InsertRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, RevisionContent c, Guid actorId, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx, """
            INSERT INTO tt.video_revisions (id, video_id, number, title, summary, media_id, author_id, updated_by, status,
                content_kind, sponsor_name, sponsor_reference, sponsorship_evidence_ref, sponsorship_valid_from, sponsorship_valid_to, change_reason)
            VALUES (@id, @video, @number, @title, @summary, @media, @actor, @actor, 'DRAFT',
                @kind, @sponsor_name, @sponsor_reference, @evidence, @valid_from, @valid_to, @reason)
            """);
        command.Parameters.AddWithValue("number", c.Number);
        await BindContent(command, c).WithUuid("id", c.RevisionId).WithUuid("video", c.VideoId).WithUuid("actor", actorId).ExecuteNonQueryAsync(ct);
    }

    /// <summary>Sửa tại chỗ revision DRAFT. Đổi sponsorship → xóa duyệt tài trợ cũ (phải duyệt lại).</summary>
    private static async Task UpdateRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, RevisionContent c, bool sponsorshipChanged, Guid actorId, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx, """
            UPDATE tt.video_revisions
               SET title = @title, summary = @summary, media_id = @media, content_kind = @kind,
                   sponsor_name = @sponsor_name, sponsor_reference = @sponsor_reference, sponsorship_evidence_ref = @evidence,
                   sponsorship_valid_from = @valid_from, sponsorship_valid_to = @valid_to, change_reason = @reason, updated_by = @actor,
                   sponsorship_approved_by = CASE WHEN @reset_approval THEN NULL ELSE sponsorship_approved_by END,
                   sponsorship_approved_at = CASE WHEN @reset_approval THEN NULL ELSE sponsorship_approved_at END
             WHERE id = @id
            """);
        command.Parameters.AddWithValue("reset_approval", sponsorshipChanged);
        await BindContent(command, c).WithUuid("id", c.RevisionId).WithUuid("actor", actorId).ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand BindContent(NpgsqlCommand command, RevisionContent c) => command
        .WithText("title", c.Title)
        .WithText("summary", c.Summary)
        .WithUuid("media", c.MediaId)
        .WithText("kind", c.ContentKind)
        .WithText("sponsor_name", c.Sponsorship?.SponsorName)
        .WithText("sponsor_reference", c.Sponsorship?.SponsorReference)
        .WithText("evidence", c.Sponsorship?.EvidenceRef)
        .WithTime("valid_from", c.Sponsorship?.ValidFrom)
        .WithTime("valid_to", c.Sponsorship?.ValidTo)
        .WithText("reason", c.ChangeReason);

    private static async Task<AdminVideo?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid videoId, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx, VideoSql.AdminSelect + " WHERE v.id = @id").WithUuid("id", videoId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? VideoSql.ReadAdmin(reader).Video : null;
    }

    private static async Task<CurrentRevision?> LoadRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid revisionId, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx, """
            SELECT id, video_id, number, status, title, summary, content_kind,
                   sponsor_name, sponsor_reference, sponsorship_evidence_ref, sponsorship_valid_from, sponsorship_valid_to, change_reason,
                   media_id, author_id, submitted_by, approved_media_generation
              FROM tt.video_revisions WHERE id = @id
            """).WithUuid("id", revisionId);
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var sponsorship = r.IsDBNull(7) || r.IsDBNull(8) || r.IsDBNull(9) || r.IsDBNull(10) || r.IsDBNull(11)
            ? null
            : new VideoSponsorshipInput(r.GetString(7), r.GetString(8), r.GetString(9), VideoSql.Time(r, 10), VideoSql.Time(r, 11));
        return new CurrentRevision(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), VideoSql.NullableText(r, 5),
            VideoSql.NullableGuid(r, 13), r.GetString(6), sponsorship, VideoSql.NullableText(r, 12), VideoSql.NullableGuid(r, 14), VideoSql.NullableGuid(r, 15),
            r.IsDBNull(16) ? null : r.GetInt32(16));
    }

    private static async Task<LockedVideo?> LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid videoId, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx, """
            SELECT status, row_version, owner_id, working_revision_id, public_revision_id, rights_status, rights_expires_at,
                   rights_status = 'CLEARED' AND (rights_expires_at IS NULL OR rights_expires_at > now())
              FROM tt.videos WHERE id = @id FOR UPDATE
            """).WithUuid("id", videoId);
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new LockedVideo(r.GetString(0), r.GetInt64(1), VideoSql.NullableGuid(r, 2), VideoSql.NullableGuid(r, 3), VideoSql.NullableGuid(r, 4),
            r.GetString(5), VideoSql.NullableTime(r, 6), r.GetBoolean(7));
    }

    private static async Task<Guid?> LatestRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid videoId, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx, "SELECT id FROM tt.video_revisions WHERE video_id = @id ORDER BY number DESC LIMIT 1")
            .WithUuid("id", videoId);
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task<int> NextRevisionNumberAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid videoId, CancellationToken ct)
    {
        // Dòng video đã bị khóa FOR UPDATE → không có hai transaction cùng cấp số.
        await using var command = VideoSql.Command(connection, tx, "SELECT COALESCE(max(number), 0) + 1 FROM tt.video_revisions WHERE video_id = @id")
            .WithUuid("id", videoId);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }
}

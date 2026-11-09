using System.Security.Claims;
using Npgsql;
using Tinhte.Modules.News.Domain;
using Tinhte.Modules.News.Infrastructure;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.News.Application;

/// <summary>
/// Quy trình duyệt bài (WBS-024; FR-NEWS-004, FR-CMS-002, FR-CMS-010):
/// <code>
///   DRAFT ──submit──▶ IN_REVIEW ──approve──▶ APPROVED ──publish──▶ PUBLISHED
///                          ├──request-changes (lý do)──▶ CHANGES_REQUESTED ─┐
///                          └──reject (lý do)───────────▶ REJECTED ──────────┴─ PATCH tạo revision mới (DRAFT)
/// </code>
/// <list type="bullet">
///   <item>Trạng thái bài đi theo working revision; bài đang public vẫn hiển thị bản public (publish pointer — ADR-0005).</item>
///   <item>Tách nhiệm vụ: không được duyệt hay xuất bản revision mà mình là tác giả hoặc đã sửa → 403 SOD_VIOLATION.</item>
///   <item>Yêu cầu sửa / từ chối bắt buộc lý do (≥ 5 ký tự) → 422 REASON_REQUIRED; người phụ trách tiếp theo
///         (assigneeId) là người đã gửi duyệt.</item>
///   <item>Thứ tự lỗi theo catalog: 403 quyền → 404 → 403 SoD → 428/412 → 422 → 409.</item>
///   <item>Trigger 0322 chặn ở DB mọi bước chuyển sai, thiếu lý do và sửa lại hồ sơ duyệt.</item>
/// </list>
/// </summary>
public sealed partial class ArticleService
{
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 2000;

    /// <summary>submitArticleForReview: tác giả (hoặc người có quyền sửa bài) gửi working revision DRAFT đi duyệt.</summary>
    public async Task<AdminArticle> SubmitAsync(ClaimsPrincipal user, Guid articleId, Func<long> requireIfMatch, CancellationToken ct)
    {
        var actorId = user.GetUserId() ?? throw new DomainProblemException(ErrorCodes.Unauthenticated, "Authentication required", "Sign in to the CMS.");
        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var article = await LockAsync(connection, tx, articleId, ct) ?? throw NotFound();
            RequireEditPermission(user, article.OwnerId);
            var expected = requireIfMatch();
            ETags.EnsureMatches(article.RowVersion, expected);

            var working = article.WorkingRevisionId is { } id ? await LoadRevisionAsync(connection, tx, articleId, id, ct) : null;
            if (!ArticleStatuses.Editable.Contains(article.Status) || working is null
                || working.Status != RevisionStatuses.Draft || working.RevisionId == article.PublicRevisionId)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "Nothing to submit",
                    $"Only a DRAFT working revision that is not public can be submitted (article {article.Status}, revision {working?.Status ?? "none"}). Edit the article first.");
            }

            await ExecAsync(connection, tx, """
                UPDATE tt.article_revisions SET status = 'IN_REVIEW', submitted_by = @actor, submitted_at = now() WHERE id = @rev
                """, ct, ("rev", working.RevisionId), ("actor", actorId));
            await UpdateArticleStatusAsync(connection, tx, articleId, expected, ArticleStatuses.InReview, assignee: null, ct);
            return (await LoadAsync(connection, tx, articleId, ct))!;
        }, ct);
    }

    /// <summary>approveArticleRevision: content.approve (MFA) + SoD; chỉ duyệt đúng revision đang chờ.</summary>
    public async Task<AdminArticle> ApproveAsync(Guid actorId, Guid articleId, Guid revisionId, string? note, Func<long> requireIfMatch, CancellationToken ct) =>
        await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var article = await LockAsync(connection, tx, articleId, ct) ?? throw NotFound();
            var revision = await LoadRevisionAsync(connection, tx, articleId, revisionId, ct)
                ?? throw Invalid("revisionId", "The revision does not belong to this article.");
            await RequireSeparationOfDutiesAsync(connection, tx, revisionId, actorId, "approve", ct);
            var expected = requireIfMatch();
            ETags.EnsureMatches(article.RowVersion, expected);
            RequirePendingReview(article, revision);

            await ExecAsync(connection, tx, """
                UPDATE tt.article_revisions SET status = 'APPROVED', reviewed_by = @actor, reviewed_at = now(), review_note = @note WHERE id = @rev
                """, ct, ("rev", revisionId), ("actor", actorId), ("note", string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
            await UpdateArticleStatusAsync(connection, tx, articleId, expected, ArticleStatuses.Approved, assignee: null, ct);
            return (await LoadAsync(connection, tx, articleId, ct))!;
        }, ct);

    /// <summary>
    /// postAdminArticlesArticleidRequestChanges (content.review) / postAdminArticlesArticleidReject (content.approve):
    /// trả bài kèm lý do; người gửi duyệt thành người phụ trách tiếp theo.
    /// </summary>
    public async Task<AdminArticle> ReturnAsync(Guid actorId, Guid articleId, string? reason, bool reject, Func<long> requireIfMatch, CancellationToken ct) =>
        await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var article = await LockAsync(connection, tx, articleId, ct) ?? throw NotFound();
            var expected = requireIfMatch();
            ETags.EnsureMatches(article.RowVersion, expected);
            var text = CheckReason(reason);
            var revision = article.WorkingRevisionId is { } id ? await LoadRevisionAsync(connection, tx, articleId, id, ct) : null;
            RequirePendingReview(article, revision);

            var status = reject ? RevisionStatuses.Rejected : RevisionStatuses.ChangesRequested;
            await ExecAsync(connection, tx, """
                UPDATE tt.article_revisions SET status = @status, reviewed_by = @actor, reviewed_at = now(), review_note = @note WHERE id = @rev
                """, ct, ("rev", revision!.RevisionId), ("actor", actorId), ("note", text), ("status", status));
            await UpdateArticleStatusAsync(connection, tx, articleId, expected, status, assignee: revision.SubmittedBy ?? revision.AuthorId, ct);
            return (await LoadAsync(connection, tx, articleId, ct))!;
        }, ct);

    /// <summary>ActionReason.reason: bắt buộc, 5–2000 ký tự.</summary>
    public static string CheckReason(string? reason)
    {
        var text = reason?.Trim();
        if (text is null || text.Length < ReasonMinLength)
        {
            throw new DomainProblemException(ErrorCodes.ReasonRequired, "Reason required",
                $"Give a reason of at least {ReasonMinLength} characters so the author knows what to change.",
                new Dictionary<string, string[]> { ["reason"] = [$"At least {ReasonMinLength} characters."] });
        }

        if (text.Length > ReasonMaxLength) throw Invalid("reason", $"At most {ReasonMaxLength} characters.");
        return text;
    }

    private static void RequirePendingReview(LockedArticle article, ArticleRevision? revision)
    {
        if (article.Status != ArticleStatuses.InReview || revision is null || revision.Status != RevisionStatuses.InReview
            || revision.RevisionId != article.WorkingRevisionId)
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "Not in review",
                $"The article is {article.Status} and revision {(revision is null ? "-" : $"{revision.Number} is {revision.Status}")}: only the working revision IN_REVIEW can be reviewed.");
        }
    }

    /// <summary>SoD (SOD_VIOLATION): người tạo hoặc người sửa revision không được tự duyệt/xuất bản nó.</summary>
    private static async Task RequireSeparationOfDutiesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid revisionId, Guid actorId, string action, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx,
            "SELECT @actor IN (author_id, updated_by) IS TRUE FROM tt.article_revisions WHERE id = @rev")
            .WithUuid("rev", revisionId).WithUuid("actor", actorId);
        if (await command.ExecuteScalarAsync(ct) is true)
        {
            throw new DomainProblemException(ErrorCodes.SodViolation, "Separation of duties",
                $"You wrote or edited this revision, so you cannot {action} it. Ask another approver.");
        }
    }

    private static async Task UpdateArticleStatusAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid articleId, long expected, string status, Guid? assignee, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx,
            "UPDATE tt.articles SET status = @status, assignee_id = @assignee WHERE id = @id AND row_version = @expected")
            .WithText("status", status).WithUuid("assignee", assignee).WithUuid("id", articleId);
        command.Parameters.AddWithValue("expected", expected);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
    }

    private static async Task ExecAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var command = ArticleSql.Command(connection, tx, sql);
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

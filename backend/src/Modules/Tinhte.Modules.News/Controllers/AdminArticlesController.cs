using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.News.Application;
using Tinhte.Modules.News.Domain;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.News.Controllers;

/// <summary>
/// Bài viết trong CMS (tag CmsNews, WBS-022). Bề mặt admin: chỉ phục vụ trên site cms (ADR-0001 D5).
/// Thứ tự lỗi: 401 → 403 CSRF → 403 quyền → 404 → 428/412 → 409/422.
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/articles")]
public sealed class AdminArticlesController(ArticleService articles, ICursorCodec cursors) : TinhteControllerBase
{
    /// <summary>getAdminArticles: danh sách CMS phân trang cursor, lọc theo trạng thái/người/thời gian/từ khóa.</summary>
    [HttpGet(Name = "getAdminArticles")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<CursorPage<AdminArticle>>> List(
        [FromQuery] PageRequest page,
        [FromQuery(Name = "status")] string[]? status,
        [FromQuery(Name = "ownerId")] Guid? ownerId,
        [FromQuery(Name = "assigneeId")] Guid? assigneeId,
        [FromQuery(Name = "updatedFrom")] DateTimeOffset? updatedFrom,
        [FromQuery(Name = "updatedTo")] DateTimeOffset? updatedTo,
        [FromQuery(Name = "q"), StringLength(200)] string? q,
        [FromQuery(Name = "sort"), RegularExpression(AdminArticleFilter.SortPattern)] string? sort,
        CancellationToken cancellationToken)
    {
        // ContentStatusFilter: style form, explode false → "DRAFT,PUBLISHED" (nhận cả dạng lặp tham số).
        var statuses = (status ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (statuses.FirstOrDefault(s => !ArticleStatuses.All.Contains(s)) is { } unknown)
        {
            throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid status filter", $"Unknown status '{unknown}'.");
        }

        var filter = new AdminArticleFilter(statuses, ownerId, assigneeId, updatedFrom, updatedTo, string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            sort ?? AdminArticleFilter.DefaultSort);
        var scope = CursorScope("admin-articles", filter.ScopeKey());
        var after = page.Cursor is { } cursor ? cursors.Decode<AdminArticlePosition>(scope, cursor) : null;
        var rows = await articles.ListAsync(filter, after, page.Limit + 1, cancellationToken);
        var byTitle = filter.Sort == "title_asc";
        var byCreated = filter.Sort == "createdAt_desc";
        var result = rows.ToPage(page.Limit, cursors, scope, x => new AdminArticlePosition(
            byTitle ? null : byCreated ? x.CreatedAt : x.Article.UpdatedAt, byTitle ? x.Article.Title : null, x.Article.Id));
        return Ok(new CursorPage<AdminArticle>(result.Items.Select(x => x.Article).ToList(), result.NextCursor));
    }

    /// <summary>createArticleDraft: bài DRAFT + working revision số 1. Slug sinh từ tiêu đề.</summary>
    [HttpPost(Name = "createArticleDraft")]
    [RequirePermission(Permissions.ContentWriteOwn)]
    [Idempotent]
    public async Task<ActionResult<AdminArticle>> Create(CreateArticleRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentWriteOwn);
        var article = await articles.CreateAsync(actor, request, cancellationToken);
        SetETag(article.RowVersion);
        return CreatedAtRoute("getAdminArticlesArticleid", new { articleId = article.Id }, article);
    }

    /// <summary>getAdminArticlesArticleid: chi tiết + ETag W/"rowVersion".</summary>
    [HttpGet("{articleId}", Name = "getAdminArticlesArticleid")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<AdminArticle>> Get(Guid articleId, CancellationToken cancellationToken)
    {
        var article = await articles.GetAsync(articleId, cancellationToken);
        SetETag(article.RowVersion);
        return Ok(article);
    }

    /// <summary>
    /// patchAdminArticlesArticleid: sửa working revision (If-Match). Không bao giờ đổi publicRevisionId.
    /// Quyền theo dữ liệu: bài của mình → content.write_own, bài người khác → content.write_any.
    /// </summary>
    [HttpPatch("{articleId}", Name = "patchAdminArticlesArticleid")]
    [RequirePermission(Permissions.ContentWriteOwn)]
    public async Task<ActionResult<AdminArticle>> Patch(Guid articleId, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var current = await articles.GetAsync(articleId, cancellationToken);
        ArticleService.RequireEditPermission(User, current.OwnerId);
        var expected = RequireIfMatch();
        var patch = ArticlePatch.Parse(body);
        var article = await articles.PatchAsync(User, articleId, expected, patch, cancellationToken);
        SetETag(article.RowVersion);
        return Ok(article);
    }

    /// <summary>getAdminArticlesArticleidRevisions: lịch sử revision bất biến (mới nhất trước, không kèm bodyBlocks).</summary>
    [HttpGet("{articleId}/revisions", Name = "getAdminArticlesArticleidRevisions")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<ArticleRevisionList>> Revisions(Guid articleId, CancellationToken cancellationToken) =>
        Ok(await articles.ListRevisionsAsync(articleId, cancellationToken));

    /// <summary>getArticleRevision: một revision (kèm bodyBlocks), tùy chọn diff với compareTo.</summary>
    [HttpGet("{articleId}/revisions/{revisionId}", Name = "getArticleRevision")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<ArticleRevision>> Revision(Guid articleId, Guid revisionId, [FromQuery(Name = "compareTo")] Guid? compareTo,
        CancellationToken cancellationToken) =>
        Ok(await articles.GetRevisionAsync(articleId, revisionId, compareTo, cancellationToken));

    /// <summary>submitArticleForReview: gửi working revision DRAFT đi duyệt (quyền sửa bài theo dữ liệu, như PATCH).</summary>
    [HttpPost("{articleId}/submit", Name = "submitArticleForReview")]
    [RequirePermission(Permissions.ContentWriteOwn)]
    [Idempotent]
    public async Task<ActionResult<AdminArticle>> Submit(Guid articleId, CancellationToken cancellationToken) =>
        WithETag(await articles.SubmitAsync(User, articleId, RequireIfMatch, cancellationToken));

    /// <summary>approveArticleRevision: content.approve (MFA) + tách nhiệm vụ (không tự duyệt bài mình viết/sửa).</summary>
    [HttpPost("{articleId}/approve", Name = "approveArticleRevision")]
    [RequirePermission(Permissions.ContentApprove)]
    [Idempotent]
    public async Task<ActionResult<AdminArticle>> Approve(Guid articleId, ApproveRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentApprove);
        return WithETag(await articles.ApproveAsync(actor, articleId, request.RevisionId!.Value, request.Note, RequireIfMatch, cancellationToken));
    }

    /// <summary>postAdminArticlesArticleidRequestChanges: content.review; lý do bắt buộc (REASON_REQUIRED). Hợp đồng trả 201.</summary>
    [HttpPost("{articleId}/request-changes", Name = "postAdminArticlesArticleidRequestChanges")]
    [RequirePermission(Permissions.ContentReview)]
    [Idempotent]
    public async Task<ActionResult<AdminArticle>> RequestChanges(Guid articleId, ActionReason request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentReview);
        var article = await articles.ReturnAsync(actor, articleId, request.Reason, reject: false, RequireIfMatch, cancellationToken);
        SetETag(article.RowVersion);
        return StatusCode(StatusCodes.Status201Created, article);
    }

    /// <summary>postAdminArticlesArticleidReject: content.approve (MFA); lý do bắt buộc.</summary>
    [HttpPost("{articleId}/reject", Name = "postAdminArticlesArticleidReject")]
    [RequirePermission(Permissions.ContentApprove)]
    [Idempotent]
    public async Task<ActionResult<AdminArticle>> Reject(Guid articleId, ActionReason request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentApprove);
        return WithETag(await articles.ReturnAsync(actor, articleId, request.Reason, reject: true, RequireIfMatch, cancellationToken));
    }

    private OkObjectResult WithETag(AdminArticle article)
    {
        SetETag(article.RowVersion);
        return Ok(article);
    }

    /// <summary>
    /// publishArticle: đặt publish pointer sang revision APPROVED (content.publish, cần MFA). Trigger 0307 chặn SPONSORED
    /// khi cờ ads.sponsored_publication OFF (403 FEATURE_DISABLED) / chưa duyệt tài trợ / hết hạn.
    /// </summary>
    [HttpPost("{articleId}/publish", Name = "publishArticle")]
    [RequirePermission(Permissions.ContentPublish)]
    [Idempotent]
    public async Task<ActionResult<AdminArticle>> Publish(Guid articleId, PublishRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentPublish);
        await articles.GetAsync(articleId, cancellationToken);
        var expected = RequireIfMatch();
        var article = await articles.PublishAsync(actor, articleId, expected, request.RevisionId!.Value, cancellationToken);
        SetETag(article.RowVersion);
        return Ok(article);
    }
}

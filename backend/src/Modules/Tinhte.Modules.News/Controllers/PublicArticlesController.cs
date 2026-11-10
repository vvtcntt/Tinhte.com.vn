using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.News.Application;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.News.Controllers;

/// <summary>Bài công khai (tag PublicNews). Bề mặt public: chỉ trên site public-web; chỉ bài thuộc site của Host (ADR-0001).</summary>
[Route(ApiRoutes.Prefix + "/articles")]
public sealed class PublicArticlesController(PublicArticleService articles, PublicArticleListService list, ICursorCodec cursors) : TinhteControllerBase
{
    /// <summary>
    /// listPublicArticles (FR-DISC-004, FR-ID-001): bài đang công khai của site, mới nhất trước, phân trang cursor.
    /// <c>category</c> lọc theo chuyên mục đang bật; chuyên mục không có → danh sách rỗng (hợp đồng không có 404 ở đây).
    /// </summary>
    [HttpGet(Name = "listPublicArticles")]
    [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<CursorPage<PublicArticleSummary>>> List(
        [FromQuery] PageRequest page,
        [FromQuery(Name = "category"), RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$"), StringLength(120)] string? category,
        CancellationToken cancellationToken)
    {
        var scope = CursorScope("public-articles", category);
        var after = page.Cursor is { } cursor ? cursors.Decode<PublicArticlePosition>(scope, cursor) : null;
        var rows = await list.ListAsync(Site.Key, category, after, page.Limit + 1, cancellationToken);
        return Ok(rows.ToPage(page.Limit, cursors, scope, a => new PublicArticlePosition(a.PublishedAt, a.Id)));
    }

    /// <summary>getPublicArticle: chỉ public revision; slug cũ → 308; đã gỡ → 410 CONTENT_WITHDRAWN.</summary>
    [HttpGet("{slug}", Name = "getPublicArticle")]
    public async Task<IActionResult> Get([StringLength(250, MinimumLength = 1)] string slug, CancellationToken cancellationToken)
    {
        var result = await articles.GetAsync(Site.Key, slug, cancellationToken);
        if (result is PublicArticleResult.Moved moved)
        {
            // Location tương đối, cùng origin của site (không viết cứng host — ADR-0001).
            return RedirectPermanentPreserveMethod($"/{ApiRoutes.Prefix}/articles/{Uri.EscapeDataString(moved.Slug)}");
        }

        var article = ((PublicArticleResult.Found)result).Article;
        // ETag theo public revision: sửa bản nháp không làm đổi ETag công khai.
        Response.Headers.ETag = $"W/\"{article.RevisionId:N}\"";
        return Ok(article);
    }
}

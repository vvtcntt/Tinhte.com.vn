using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.News.Application;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.News.Controllers;

/// <summary>Bài công khai (tag PublicNews). Bề mặt public: chỉ trên site public-web; chỉ bài thuộc site của Host (ADR-0001).</summary>
[Route(ApiRoutes.Prefix + "/articles")]
public sealed class PublicArticlesController(PublicArticleService articles) : TinhteControllerBase
{
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

using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.News.Application;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.News.Controllers;

/// <summary>
/// Biên tập tin nổi bật trang chủ (tag CmsHome; FR-DISC-003). Bề mặt admin: chỉ phục vụ trên site CMS;
/// site được biên tập chọn qua <c>siteKey</c> (Site Registry, ADR-0001).
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/home/{siteKey}/featured-articles")]
[Tags("CmsHome")]
public sealed class AdminHomeController(HomeCurationService curation) : TinhteControllerBase
{
    /// <summary>Schema HomeFeaturedArticlesUpdate.</summary>
    public sealed record HomeFeaturedArticlesUpdate(IReadOnlyList<Guid>? ArticleIds);

    [HttpGet(Name = "getAdminHomeFeaturedArticles")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<HomeFeaturedArticles>> Get(
        [RegularExpression("^[a-z][a-z0-9-]{0,30}$")] string siteKey, CancellationToken cancellationToken)
    {
        var featured = await curation.GetAsync(siteKey, cancellationToken);
        SetETag(featured.RowVersion);
        return Ok(featured);
    }

    [HttpPut(Name = "putAdminHomeFeaturedArticles")]
    [RequirePermission(Permissions.ContentPublish)]
    public async Task<ActionResult<HomeFeaturedArticles>> Put(
        [RegularExpression("^[a-z][a-z0-9-]{0,30}$")] string siteKey, HomeFeaturedArticlesUpdate request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentPublish);
        curation.RequireSite(siteKey);          // 404 trước 428/412
        var expected = RequireIfMatch();
        if (request.ArticleIds is null)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "articleIds is required.",
                new Dictionary<string, string[]> { ["articleIds"] = ["articleIds is required (use [] to clear the featured list)."] });
        }

        var featured = await curation.ReplaceAsync(actor, siteKey, request.ArticleIds, expected, cancellationToken);
        SetETag(featured.RowVersion);
        return Ok(featured);
    }
}

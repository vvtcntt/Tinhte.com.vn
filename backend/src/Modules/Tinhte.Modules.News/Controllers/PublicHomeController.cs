using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.News.Application;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.News.Controllers;

/// <summary>
/// getHome (tag PublicHome; FR-DISC-003): chỉ nội dung hiển thị công khai của site trong Host (ADR-0001 D8).
/// Cache ngắn: bài bị gỡ phải biến khỏi trang chủ trong SLA (FR-DISC-004) — purge CDN là việc của WBS-112.
/// </summary>
[Route(ApiRoutes.Prefix + "/home")]
[Tags("PublicHome")]
public sealed class PublicHomeController(HomeService home) : TinhteControllerBase
{
    [HttpGet(Name = "getHome")]
    [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<HomePage>> Get(CancellationToken cancellationToken) =>
        Ok(await home.GetAsync(Site.Key, cancellationToken));
}

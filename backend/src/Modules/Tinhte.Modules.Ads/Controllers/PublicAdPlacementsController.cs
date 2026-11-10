using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Ads.Application;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.Ads.Controllers;

/// <summary>
/// getAdsPlacements (tag Advertising; FR-ADS-001/002, WBS-133): slot quảng cáo tĩnh của site trong Host, không nhắm theo người dùng.
/// Cờ <c>ads.display_slots</c> OFF (mặc định production) → 200 với danh sách rỗng (không để khung trống). Cache ngắn như /home:
/// đặt chỗ hết giờ hoặc bị tạm dừng phải biến khỏi trang trong vài chục giây; purge CDN là việc của WBS-112.
/// </summary>
[Route(ApiRoutes.Prefix + "/ads/placements")]
[Tags("Advertising")]
public sealed class PublicAdPlacementsController(PublicAdSlotService slots) : TinhteControllerBase
{
    [HttpGet(Name = "getAdsPlacements")]
    [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<PublicAdSlotList>> Get([FromQuery(Name = "placementCodes")] string[]? placementCodes, CancellationToken cancellationToken)
    {
        var codes = PublicAdSlotService.ParseCodes(placementCodes);
        if (codes is null)
        {
            return Problem(ErrorCodes.BadRequest, "Invalid placementCodes",
                $"placementCodes must list 1–{PublicAdSlotService.MaxCodes} comma-separated codes matching ^[a-z0-9_-]{{2,80}}$.");
        }

        return Ok(await slots.GetAsync(Site.Key, codes, cancellationToken));
    }
}

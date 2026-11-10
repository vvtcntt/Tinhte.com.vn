using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Ads.Application;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Ads.Controllers;

/// <summary>
/// Vị trí quảng cáo + đặt chỗ của vị trí trong CMS (tag Advertising; FR-ADS-002, P0-ADS — DRAFT v0.3 mục 16/19). Bề mặt admin: chỉ phục vụ
/// trên site cms (ADR-0001 D5); vị trí thuộc site public-web chọn qua <c>siteKey</c>. Quyền <c>ads.manage</c>/<c>ads.approve</c> là ĐỀ XUẤT
/// trong config/rbac/rbac.json, chờ BA/PO duyệt. Thứ tự lỗi: 401 → 403 CSRF → 403 quyền → 404 → 428/412 → 409/422.
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/ad-placements")]
[Tags("Advertising")]
public sealed class AdminAdPlacementsController(AdAdminService ads) : TinhteControllerBase
{
    /// <summary>getAdminAdPlacements: mọi vị trí (lọc theo site), sắp theo site rồi mã.</summary>
    [HttpGet(Name = "getAdminAdPlacements")]
    [RequirePermission(Permissions.AdsManage)]
    public async Task<ActionResult<AdPlacementList>> List([FromQuery(Name = "siteKey")] string? siteKey, CancellationToken cancellationToken) =>
        Ok(await ads.ListPlacementsAsync(ads.ParseSiteFilter(siteKey), cancellationToken));

    /// <summary>createAdPlacement: vị trí DRAFT trên một site public-web; (siteKey, code) unique.</summary>
    [HttpPost(Name = "createAdPlacement")]
    [RequirePermission(Permissions.AdsManage)]
    [Idempotent]
    public async Task<ActionResult<AdPlacement>> Create(AdPlacementCreate request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AdsManage);
        var placement = await ads.CreatePlacementAsync(actor, request, cancellationToken);
        SetETag(placement.RowVersion);
        return CreatedAtRoute("getAdPlacement", new { placementId = placement.Id }, placement);
    }

    /// <summary>getAdPlacement: chi tiết + ETag W/"rowVersion".</summary>
    [HttpGet("{placementId}", Name = "getAdPlacement")]
    [RequirePermission(Permissions.AdsManage)]
    public async Task<ActionResult<AdPlacement>> Get(Guid placementId, CancellationToken cancellationToken)
    {
        var placement = await ads.GetPlacementAsync(placementId, cancellationToken);
        SetETag(placement.RowVersion);
        return Ok(placement);
    }

    /// <summary>
    /// putAdminAdPlacementsPlacementid: thay mã, nhãn, trạng thái, kích thước (If-Match). Vào APPROVED/ACTIVE cần <c>ads.approve</c> (MFA) —
    /// kiểm theo trạng thái hiện tại, sau 404 và trước 428/412.
    /// </summary>
    [HttpPut("{placementId}", Name = "putAdminAdPlacementsPlacementid")]
    [RequirePermission(Permissions.AdsManage)]
    public async Task<ActionResult<AdPlacement>> Put(Guid placementId, AdPlacementUpdate request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AdsManage);
        var current = await ads.GetPlacementAsync(placementId, cancellationToken);
        AdAdminService.RequireTransitionPermission(User, current.Status, request.Status!);
        var expected = RequireIfMatch();
        var placement = await ads.UpdatePlacementAsync(actor, placementId, expected, request, cancellationToken);
        SetETag(placement.RowVersion);
        return Ok(placement);
    }

    /// <summary>listAdminAdBookings: đặt chỗ của vị trí, mới nhất trước, mọi trạng thái.</summary>
    [HttpGet("{placementId}/bookings", Name = "listAdminAdBookings")]
    [RequirePermission(Permissions.AdsManage)]
    public async Task<ActionResult<AdBookingList>> ListBookings(Guid placementId, CancellationToken cancellationToken) =>
        Ok(await ads.ListBookingsAsync(placementId, cancellationToken));

    /// <summary>createAdBooking: đặt chỗ DRAFT, tối đa một banner mỗi breakpoint, kích thước thuộc vị trí; link https (UNSAFE_URL).</summary>
    [HttpPost("{placementId}/bookings", Name = "createAdBooking")]
    [RequirePermission(Permissions.AdsManage)]
    [Idempotent]
    public async Task<ActionResult<AdBooking>> CreateBooking(Guid placementId, AdBookingCreate request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AdsManage);
        var booking = await ads.CreateBookingAsync(actor, placementId, request, cancellationToken);
        SetETag(booking.RowVersion);
        return CreatedAtRoute("getAdBooking", new { bookingId = booking.Id }, booking);
    }
}

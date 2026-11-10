using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Ads.Application;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Ads.Controllers;

/// <summary>
/// Đặt chỗ quảng cáo trong CMS (tag Advertising; FR-ADS-002, P0-ADS — DRAFT v0.3 mục 16/19). Bề mặt admin: chỉ trên site cms.
/// Banner, link, nhà quảng cáo, lịch chỉ đổi khi DRAFT; ưu tiên đổi mọi lúc. Vào APPROVED/ACTIVE cần <c>ads.approve</c> (MFA, ĐỀ XUẤT).
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/ad-bookings")]
[Tags("Advertising")]
public sealed class AdminAdBookingsController(AdAdminService ads) : TinhteControllerBase
{
    /// <summary>getAdBooking: chi tiết + ETag W/"rowVersion".</summary>
    [HttpGet("{bookingId}", Name = "getAdBooking")]
    [RequirePermission(Permissions.AdsManage)]
    public async Task<ActionResult<AdBooking>> Get(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await ads.GetBookingAsync(bookingId, cancellationToken);
        SetETag(booking.RowVersion);
        return Ok(booking);
    }

    /// <summary>putAdminAdBooking: thay toàn bộ (If-Match); quyền duyệt kiểm theo trạng thái hiện tại, sau 404 và trước 428/412.</summary>
    [HttpPut("{bookingId}", Name = "putAdminAdBooking")]
    [RequirePermission(Permissions.AdsManage)]
    public async Task<ActionResult<AdBooking>> Put(Guid bookingId, AdBookingUpdate request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AdsManage);
        var current = await ads.GetBookingAsync(bookingId, cancellationToken);
        AdAdminService.RequireTransitionPermission(User, current.Status, request.Status!);
        var expected = RequireIfMatch();
        var booking = await ads.UpdateBookingAsync(actor, bookingId, expected, request, cancellationToken);
        SetETag(booking.RowVersion);
        return Ok(booking);
    }
}

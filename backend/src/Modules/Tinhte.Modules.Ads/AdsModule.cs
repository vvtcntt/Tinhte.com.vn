using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Modules.Ads.Application;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Ads;

/// <summary>
/// Module ads: Sponsored-lite, ad placements — P0-ADS (FR-ADS). Đã có: slot công khai theo site, nhiều kích thước theo breakpoint
/// (GET /ads/placements, migration 0350 — DRAFT đợt 3 trang chủ); API quản trị vị trí/đặt chỗ (/admin/ad-placements*, /admin/ad-bookings/*)
/// với quyền ĐỀ XUẤT ads.manage/ads.approve + vai trò AD_OPS (config/rbac/rbac.json, migration 0351 — chờ BA/PO duyệt).
/// </summary>
public sealed class AdsModule : IModule
{
    public string Name => "ads";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<PublicAdSlotService>();
        services.AddSingleton<AdAdminService>();
    }
}

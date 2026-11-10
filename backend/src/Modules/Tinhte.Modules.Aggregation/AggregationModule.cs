using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Platform.Modules;

namespace Tinhte.Modules.Aggregation;

/// <summary>
/// Module Aggregation — Radar tin GĐ1 (ADR-0008, PROPOSED; BA-01; FR-AGG-001…005). Lát hiện tại: quản lý nguồn thu thập và feed
/// (FR-AGG-001/002, tag CmsAggregation). Chưa có: worker thu thập (Tinhte.Worker, A-1), thử feed, luật chuyên mục, mục tin, cụm/Radar,
/// thống kê — lát sau. Không gọi AI, không xuất bản gì. Chỉ tham chiếu Tinhte.Platform; đọc bảng module khác bằng SQL chỉ đọc.
/// </summary>
public sealed class AggregationModule : IModule
{
    public string Name => "aggregation";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IFeedHostResolver, DnsFeedHostResolver>();
        services.AddSingleton<FeedUrlPolicy>();
        services.AddSingleton<AggregationSourceService>();
    }
}

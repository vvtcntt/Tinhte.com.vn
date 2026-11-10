using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Modules.Aggregation.Infrastructure.Collection;
using Tinhte.Platform;
using Tinhte.Platform.Modules;
using Tinhte.Platform.Sites;

namespace Tinhte.Modules.Aggregation;

/// <summary>
/// Module Aggregation — Radar tin GĐ1 (ADR-0008, PROPOSED; BA-01; FR-AGG-001…005). Đã có: quản lý nguồn/feed (lát 1); bộ thu thập an toàn,
/// thử feed, luật chuyên mục, danh sách mục tin (lát 2); gom cụm MinHash trong worker, Radar cụm + thao tác cụm, thống kê (lát 3). Worker thu thập là tiến trình riêng <c>Tinhte.Worker</c> (A-1) dùng
/// <see cref="AddCollectorWorker"/>. Chưa có: màn CMS. Không gọi AI, không xuất bản gì.
/// Chỉ tham chiếu Tinhte.Platform; đọc bảng module khác bằng SQL chỉ đọc.
/// </summary>
public sealed class AggregationModule : IModule
{
    public string Name => "aggregation";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        AddCollector(services, configuration);
        services.AddSingleton<AggregationSourceService>();
        services.AddSingleton<FeedTestRateLimiter>();
        services.AddSingleton<FeedTestService>();
        services.AddSingleton<AggregationRadarService>();
        services.AddSingleton<AggregationClusterService>();
    }

    /// <summary>Bộ thu thập dùng chung API (thử feed) và worker: chống SSRF, robots, User-Agent theo Site Registry, "hôm nay".</summary>
    public static void AddCollector(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFeedHostResolver, DnsFeedHostResolver>();
        services.TryAddSingleton<FeedUrlPolicy>();
        services.TryAddSingleton(sp => AggregationOptions.From(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(sp => new NewsroomClock(sp.GetRequiredService<AggregationOptions>().NewsroomTimeZone));
        services.TryAddSingleton(sp => BotUserAgent.FromRegistry(sp.GetRequiredService<SiteRegistry>(), sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(sp => new FeedFetcher(SafeFeedHttp.CreateHandler(sp.GetRequiredService<IFeedHostResolver>()),
            sp.GetRequiredService<IFeedHostResolver>(), sp.GetRequiredService<BotUserAgent>(), sp.GetRequiredService<TimeProvider>()));
    }

    /// <summary>Phần chỉ worker cần: danh sách chủ đề nhạy cảm (config/aggregation/sensitive-topics.json) + dịch vụ thu thập.</summary>
    public static void AddCollectorWorker(IServiceCollection services, IConfiguration configuration)
    {
        AddCollector(services, configuration);
        services.TryAddSingleton(sp => SensitiveTopics.Load(ContentPaths.Resolve(sp.GetRequiredService<AggregationOptions>().SensitiveTopicsPath,
            sp.GetRequiredService<IHostEnvironment>())));
        services.TryAddSingleton<FeedCollectionService>();
    }
}

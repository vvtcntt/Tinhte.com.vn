using Microsoft.Extensions.Configuration;

namespace Tinhte.Platform.Flags;

/// <summary>Khóa cờ theo registry x-feature-flags của hợp đồng v0.3 (AIR-001). Test giữ khớp với spec.</summary>
public static class FeatureFlagKeys
{
    public const string CommunityVideoComments = "community.video_comments";
    public const string CommunityArticleComments = "community.article_comments";
    public const string CommunityMember = "community.member";
    public const string CommunityModeration = "community.moderation";
    public const string CommunityUgc = "community.ugc";
    public const string CommunityAppeals = "community.appeals";
    public const string MemberBookmarks = "member.bookmarks";
    public const string AdsSponsoredPublication = "ads.sponsored_publication";
    public const string AdsDisplaySlots = "ads.display_slots";
    public const string AdsShortsInstream = "ads.shorts_instream";
    public const string AdsCampaignAdvanced = "ads.campaign_advanced";
    public const string DiscoverySeries = "discovery.series";
    public const string DiscoverySearchSuggestions = "discovery.search_suggestions";
    public const string CmsRelatedSuggestions = "cms.related_suggestions";
    public const string AnalyticsOperations = "analytics.operations";
    public const string AiAssistance = "ai.assistance";
    public const string AiSearch = "ai.search";

    public static readonly IReadOnlyList<string> All =
    [
        CommunityVideoComments, CommunityArticleComments, CommunityMember, CommunityModeration, CommunityUgc, CommunityAppeals,
        MemberBookmarks, AdsSponsoredPublication, AdsDisplaySlots, AdsShortsInstream, AdsCampaignAdvanced, DiscoverySeries,
        DiscoverySearchSuggestions, CmsRelatedSuggestions, AnalyticsOperations, AiAssistance, AiSearch,
    ];

    /// <summary>Mặc định production: mọi cờ OFF trừ community.moderation (công cụ staff).</summary>
    public static bool ProductionDefault(string key) => key == CommunityModeration;
}

public interface IFeatureFlagReader
{
    ValueTask<bool> IsEnabledAsync(string key, CancellationToken cancellationToken = default);

    ValueTask<string> VersionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Sprint 0: đọc mặc định production, cho phép ghi đè qua cấu hình "FeatureFlags:{key}" (chỉ dùng local/test).
/// Production phải đọc tt.feature_gates (migration 0300) — thay bằng reader DB ở WBS-020/WBS-059.
/// </summary>
public sealed class ConfigurationFeatureFlagReader(IConfiguration configuration) : IFeatureFlagReader
{
    public ValueTask<bool> IsEnabledAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!FeatureFlagKeys.All.Contains(key)) throw new ArgumentException($"Unknown feature flag '{key}'", nameof(key));
        return ValueTask.FromResult(configuration.GetValue($"FeatureFlags:{key}", FeatureFlagKeys.ProductionDefault(key)));
    }

    public ValueTask<string> VersionAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult("config-" + string.Concat(FeatureFlagKeys.All.Select(k => configuration[$"FeatureFlags:{k}"] is null ? "d" : "o")));
}

using Microsoft.AspNetCore.Mvc;
using Tinhte.Platform.Flags;
using Tinhte.Platform.Mvc;

namespace Tinhte.Api.Controllers;

/// <summary>Cấu hình công khai theo site (hợp đồng v0.3: GET /site/config → SitePublicConfig).</summary>
[Route(ApiRoutes.Prefix + "/site")]
[Tags("PublicHome")]
public sealed class SiteController(IFeatureFlagReader flags) : TinhteControllerBase
{
    /// <summary>SitePublicConfig — chỉ là gợi ý UX, KHÔNG phải cơ chế phân quyền (backend luôn tự kiểm cờ).</summary>
    public sealed record SitePublicConfig(
        bool VideoCommentsEnabled,
        bool MemberLoginEnabled,
        bool SponsoredContentEnabled,
        bool AdSlotsEnabled,
        bool ShortsInstreamAdsEnabled,
        string FlagsVersion);

    [HttpGet("config", Name = "getSiteConfig")]
    [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<SitePublicConfig>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SitePublicConfig>> GetConfig(CancellationToken cancellationToken) =>
        Ok(new SitePublicConfig(
            await flags.IsEnabledAsync(FeatureFlagKeys.CommunityVideoComments, cancellationToken),
            await flags.IsEnabledAsync(FeatureFlagKeys.CommunityMember, cancellationToken),
            await flags.IsEnabledAsync(FeatureFlagKeys.AdsSponsoredPublication, cancellationToken),
            await flags.IsEnabledAsync(FeatureFlagKeys.AdsDisplaySlots, cancellationToken),
            await flags.IsEnabledAsync(FeatureFlagKeys.AdsShortsInstream, cancellationToken),
            await flags.VersionAsync(cancellationToken)));
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Tinhte.Platform.Flags;

namespace Tinhte.Platform.Endpoints;

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
        app.MapGet("/health/ready", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription(); // thêm kiểm DB ở WBS-013

        // GET /site/config — SitePublicConfig (hợp đồng v0.3). UX hint, không phải cơ chế phân quyền.
        app.MapGet($"{PlatformExtensions.ApiPrefix}/site/config", async (IFeatureFlagReader flags, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "public, max-age=30";
            return Results.Ok(new
            {
                videoCommentsEnabled = await flags.IsEnabledAsync(FeatureFlagKeys.CommunityVideoComments, ct),
                memberLoginEnabled = await flags.IsEnabledAsync(FeatureFlagKeys.CommunityMember, ct),
                sponsoredContentEnabled = await flags.IsEnabledAsync(FeatureFlagKeys.AdsSponsoredPublication, ct),
                adSlotsEnabled = await flags.IsEnabledAsync(FeatureFlagKeys.AdsDisplaySlots, ct),
                shortsInstreamAdsEnabled = await flags.IsEnabledAsync(FeatureFlagKeys.AdsShortsInstream, ct),
                flagsVersion = await flags.VersionAsync(ct),
            });
        }).WithName("getSiteConfig").WithTags("PublicHome");

        return app;
    }
}

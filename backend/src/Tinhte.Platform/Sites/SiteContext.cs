using Microsoft.AspNetCore.Http;

namespace Tinhte.Platform.Sites;

/// <summary>Bề mặt API theo đường dẫn (ADR-0001 D5): admin chỉ phục vụ trên site có apiSurfaces chứa "admin".</summary>
public static class ApiSurfaces
{
    public const string Public = "public";
    public const string Admin = "admin";

    public static string? For(PathString path)
    {
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.StartsWithSegments("/api/v1/admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/v1/auth/staff", StringComparison.OrdinalIgnoreCase))
        {
            return Admin;
        }

        return Public;
    }
}

public interface ISiteFeature
{
    SiteDefinition Site { get; }
}

internal sealed record SiteFeature(SiteDefinition Site) : ISiteFeature;

public static class SiteHttpContextExtensions
{
    /// <summary>Site của request hiện tại (null với endpoint không qua phân giải, ví dụ /health).</summary>
    public static SiteDefinition? GetSite(this HttpContext context) => context.Features.Get<ISiteFeature>()?.Site;

    public static SiteDefinition RequireSite(this HttpContext context) =>
        context.GetSite() ?? throw new InvalidOperationException("Site was not resolved for this request.");
}

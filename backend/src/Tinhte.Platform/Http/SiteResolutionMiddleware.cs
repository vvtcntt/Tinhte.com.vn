using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Sites;

namespace Tinhte.Platform.Http;

/// <summary>
/// ADR-0001 D2/D5: phân giải site từ Host. Host lạ → 400; alias → 308 về host chính tắc;
/// bề mặt API không thuộc site → 404. /health bỏ qua (orchestrator gọi theo IP).
/// </summary>
public sealed class SiteResolutionMiddleware(RequestDelegate next, SiteRegistry registry, ILogger<SiteResolutionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var match = registry.Resolve(context.Request.Host.Value);
        if (match is null)
        {
            await Problems.WriteAsync(context, ErrorCodes.BadRequest, "Unknown host", "The Host header does not match any site in the registry.");
            return;
        }

        if (match.IsAlias)
        {
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location =
                $"{registry.OriginFor(match.Site)}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            return;
        }

        var surface = ApiSurfaces.For(context.Request.Path);
        if (surface is not null && !match.Site.ApiSurfaces.Contains(surface))
        {
            await Problems.WriteAsync(context, ErrorCodes.NotFound, "Not found", $"API surface '{surface}' is not served on site '{match.Site.Key}'.");
            return;
        }

        context.Features.Set<ISiteFeature>(new SiteFeature(match.Site));
        context.Response.Headers["X-TT-Site"] = match.Site.Key;
        using (logger.BeginScope(new Dictionary<string, object> { ["SiteKey"] = match.Site.Key }))
        {
            await next(context);
        }
    }
}

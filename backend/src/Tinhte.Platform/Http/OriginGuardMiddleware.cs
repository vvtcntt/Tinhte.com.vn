using Microsoft.AspNetCore.Http;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Sites;

namespace Tinhte.Platform.Http;

/// <summary>
/// ADR-0001 D4 — lớp CSRF 1: API cùng origin theo site, nên mọi request ghi từ trình duyệt phải có
/// Origin trùng host + scheme của chính site đó. Request ghi mang cookie mà không có Origin bị từ chối.
/// Lớp 2 (double-submit X-CSRF-Token) làm cùng staff/member auth (WBS-017, WBS-059).
/// </summary>
public sealed class OriginGuardMiddleware(RequestDelegate next, SiteRegistry registry)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var site = context.GetSite();
        if (site is null || HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
        {
            await next(context);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, registry.Environment.Scheme, StringComparison.OrdinalIgnoreCase)
                || SiteRegistry.NormalizeHost(uri.Host) != registry.HostFor(site))
            {
                await Problems.WriteAsync(context, ErrorCodes.CsrfInvalid, "Cross-origin request rejected", "Origin does not match the site of this request.");
                return;
            }
        }
        else if (context.Request.Cookies.Count > 0)
        {
            await Problems.WriteAsync(context, ErrorCodes.CsrfInvalid, "Cross-origin request rejected", "Cookie-authenticated mutations must send an Origin header.");
            return;
        }

        await next(context);
    }
}

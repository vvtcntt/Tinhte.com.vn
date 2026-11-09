using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Tinhte.Modules.Identity.Infrastructure;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Identity.Application;

public abstract record StaffSignInResult
{
    public sealed record Success(string SessionToken, string CsrfToken, DateTimeOffset ExpiresAt) : StaffSignInResult;

    /// <summary>Đăng nhập IdP thành công nhưng chưa được cấp vai trò CMS (đã tạo user để admin cấp quyền).</summary>
    public sealed record NoRoles : StaffSignInResult;

    public sealed record Inactive : StaffSignInResult;
}

/// <summary>
/// Callback OIDC → phiên staff (FR-ID-006): tạo/tìm user theo (issuer, subject), đọc vai trò từ DB (không tin vai trò do IdP gửi),
/// đánh dấu MFA theo amr/acr, tạo phiên mới (xoay phiên cũ) và token CSRF.
/// </summary>
public sealed class StaffSignInService(StaffAccessStore store, IOptions<StaffAuthOptions> options, TimeProvider clock)
{
    public async Task<StaffSignInResult> SignInAsync(ClaimsPrincipal external, HttpContext http, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var subject = external.FindFirst("sub")?.Value ?? throw new InvalidOperationException("id_token has no 'sub' claim.");
        var issuer = external.FindFirst("iss")?.Value ?? external.FindFirst("sub")!.Issuer;
        var displayName = new[] { "name", "preferred_username", "email" }.Select(t => external.FindFirst(t)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? subject;
        if (displayName.Length > 80) displayName = displayName[..80];

        var account = await store.FindOrProvisionAsync(issuer, subject, displayName, cancellationToken);
        if (!string.Equals(account.Status, "ACTIVE", StringComparison.Ordinal)) return new StaffSignInResult.Inactive();
        if (account.Roles.Count == 0) return new StaffSignInResult.NoRoles();

        var amr = external.FindAll("amr").Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        var acr = external.FindFirst("acr")?.Value;
        var mfa = amr.Any(o.MfaAmrValues.Contains) || (acr is not null && o.MfaAcrValues.Contains(acr));
        DateTimeOffset? authTime = long.TryParse(external.FindFirst("auth_time")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var at)
            ? DateTimeOffset.FromUnixTimeSeconds(at) : null;

        var sessionToken = NewToken();
        var csrfToken = NewToken();
        var expiresAt = clock.GetUtcNow().Add(o.AbsoluteLifetime);
        var previous = http.Request.Cookies.TryGetValue(StaffAuthDefaults.SessionCookie, out var old) && !string.IsNullOrEmpty(old) ? Hash(old) : null;
        var userAgent = http.Request.Headers.UserAgent.ToString();

        await store.CreateSessionAsync(new NewStaffSession(Hash(sessionToken), account.UserId, issuer, CsrfMiddleware.TokenHash(csrfToken), mfa, amr,
            authTime, expiresAt, userAgent.Length == 0 ? null : userAgent[..Math.Min(300, userAgent.Length)]), previous, cancellationToken);
        return new StaffSignInResult.Success(sessionToken, csrfToken, expiresAt);
    }

    public static string Hash(string token) => CsrfMiddleware.TokenHash(token);

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Cookie host-only <c>__Host-</c> (ADR-0001 D4): Secure, Path=/, không Domain.</summary>
public static class StaffCookies
{
    public static void Issue(HttpResponse response, StaffSignInResult.Success session, TimeSpan lifetime)
    {
        response.Cookies.Append(StaffAuthDefaults.SessionCookie, session.SessionToken, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = lifetime, IsEssential = true,
        });
        response.Cookies.Append(StaffAuthDefaults.CsrfCookie, session.CsrfToken, new CookieOptions
        {
            HttpOnly = false, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = lifetime, IsEssential = true,
        });
    }

    public static void Clear(HttpResponse response)
    {
        foreach (var name in new[] { StaffAuthDefaults.SessionCookie, StaffAuthDefaults.CsrfCookie })
        {
            response.Cookies.Delete(name, new CookieOptions { Secure = true, Path = "/", SameSite = SameSiteMode.Lax });
        }
    }
}

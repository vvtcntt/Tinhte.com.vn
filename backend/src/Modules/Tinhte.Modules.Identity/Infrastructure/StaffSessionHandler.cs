using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tinhte.Modules.Identity.Application;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;
using Tinhte.Platform.Sites;

namespace Tinhte.Modules.Identity.Infrastructure;

/// <summary>
/// Xác thực cookie <c>__Host-tt_cms_session</c> bằng phiên phía server (tt.staff_sessions). Chỉ chạy trên site có
/// <c>session: staff</c> (cms) — cookie lọt sang site khác cũng không được chấp nhận (ADR-0001 D4/D5).
/// Vai trò đọc từ DB mỗi request → thu hồi vai trò/phiên có hiệu lực ngay.
/// </summary>
public sealed class StaffSessionHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    StaffAccessStore store,
    ITinhteDatabase database,
    IOptions<StaffAuthOptions> options)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetSite()?.Session != TinhteClaims.StaffSession || !database.IsConfigured) return AuthenticateResult.NoResult();
        if (!Request.Cookies.TryGetValue(StaffAuthDefaults.SessionCookie, out var token) || string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();

        var session = await store.FindActiveAsync(StaffSignInService.Hash(token), options.Value.IdleTimeout, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Staff session is expired, revoked or unknown.");

        var claims = new List<Claim>
        {
            new(TinhteClaims.Subject, session.UserId.ToString()),
            new(TinhteClaims.SessionKind, TinhteClaims.StaffSession),
            new(TinhteClaims.SessionId, session.IdHash),
            new(TinhteClaims.CsrfHash, session.CsrfHash),
            new(TinhteClaims.MfaSatisfied, session.MfaSatisfied ? "true" : "false"),
        };
        claims.AddRange(session.Roles.Select(r => new Claim(TinhteClaims.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, TinhteClaims.Subject, TinhteClaims.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Problems.WriteAsync(Context, ErrorCodes.Unauthenticated, "Authentication required", "Sign in to the CMS.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Problems.WriteAsync(Context, ErrorCodes.Forbidden, "Forbidden", "You are not allowed to perform this action.");
}

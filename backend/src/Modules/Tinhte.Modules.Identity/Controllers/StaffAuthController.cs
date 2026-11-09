using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tinhte.Modules.Identity.Application;
using Tinhte.Modules.Identity.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Identity.Controllers;

/// <summary>Đăng nhập/đăng xuất staff (tag StaffAuth, FR-ID-006). Chỉ phục vụ trên site cms (bề mặt admin, ADR-0001 D5).</summary>
[Route(ApiRoutes.Prefix + "/auth/staff")]
public sealed partial class StaffAuthController(
    IOptions<StaffAuthOptions> options,
    StaffAccessStore store,
    ITinhteDatabase database,
    IOptionsMonitor<OpenIdConnectOptions> oidc,
    ILogger<StaffAuthController> logger) : TinhteControllerBase
{
    /// <summary>
    /// getAuthStaffOidcStart: chuyển tới IdP; state/nonce/PKCE lưu trong cookie __Host- ngắn hạn.
    /// Kiểm trước những gì callback sẽ cần (cấu hình IdP, discovery của IdP, DB phiên) để báo 503 ngay thay vì để người dùng
    /// đăng nhập ở IdP xong mới lỗi. Trình duyệt nhận 302 về /login?error=DEPENDENCY_UNAVAILABLE&amp;reason=unavailable.
    /// </summary>
    [HttpGet("oidc/start", Name = "getAuthStaffOidcStart")]
    public async Task<IActionResult> Start(
        [FromQuery(Name = "returnTo"), StringLength(ReturnTo.MaxLength), RegularExpression(ReturnTo.Pattern)] string? returnTo,
        CancellationToken cancellationToken)
    {
        var target = ReturnTo.Sanitize(returnTo);
        string? unavailable = null;
        if (!options.Value.IsConfigured)
        {
            unavailable = "Staff sign-in is not configured (Auth:Staff).";
        }
        else if (!await database.PingAsync(cancellationToken))
        {
            unavailable = "The session database is not available.";
        }
        else
        {
            try
            {
                var manager = oidc.Get(StaffAuthDefaults.OidcScheme).ConfigurationManager;
                if (manager is not null) await manager.GetConfigurationAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogIdpUnreachable(logger, e);
                unavailable = "The identity provider cannot be reached.";
            }
        }

        if (unavailable is not null)
        {
            await CallbackErrors.WriteAsync(HttpContext, ErrorCodes.DependencyUnavailable, CallbackErrors.Unavailable,
                "Staff sign-in unavailable", unavailable, target);
            return new EmptyResult();
        }

        return Challenge(new AuthenticationProperties { RedirectUri = target }, StaffAuthDefaults.OidcScheme);
    }

    [LoggerMessage(EventId = 4202, Level = LogLevel.Error, Message = "Staff IdP discovery failed")]
    private static partial void LogIdpUnreachable(ILogger logger, Exception exception);

    /// <summary>postAuthStaffLogout: thu hồi phiên phía server + xóa cookie. Cần X-CSRF-Token (CsrfMiddleware).</summary>
    [HttpPost("logout", Name = "postAuthStaffLogout")]
    [RequireStaffSession]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (User.FindFirst(TinhteClaims.SessionId)?.Value is { } sessionId)
        {
            await store.RevokeAsync(sessionId, "LOGOUT", User.GetUserId(), cancellationToken);
        }

        StaffCookies.Clear(Response);
        return NoContent();
    }
}

/// <summary>Phiên staff hiện tại (getAdminAuthSession).</summary>
[Route(ApiRoutes.Prefix + "/admin/auth")]
public sealed class AdminAuthController : TinhteControllerBase
{
    /// <summary>Schema StaffSession của hợp đồng v0.3.</summary>
    public sealed record StaffSessionResponse(Guid UserId, IReadOnlyList<string> Roles, bool MfaSatisfied);

    [HttpGet("session", Name = "getAdminAuthSession")]
    [RequireStaffSession]
    public ActionResult<StaffSessionResponse> Session() =>
        Ok(new StaffSessionResponse(User.GetUserId()!.Value, User.GetRoles(), User.IsMfaSatisfied()));
}

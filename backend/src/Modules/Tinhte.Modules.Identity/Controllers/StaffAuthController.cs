using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Tinhte.Modules.Identity.Application;
using Tinhte.Modules.Identity.Infrastructure;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Identity.Controllers;

/// <summary>Đăng nhập/đăng xuất staff (tag StaffAuth, FR-ID-006). Chỉ phục vụ trên site cms (bề mặt admin, ADR-0001 D5).</summary>
[Route(ApiRoutes.Prefix + "/auth/staff")]
public sealed class StaffAuthController(IOptions<StaffAuthOptions> options, StaffAccessStore store) : TinhteControllerBase
{
    /// <summary>getAuthStaffOidcStart: chuyển tới IdP; state/nonce/PKCE lưu trong cookie __Host- ngắn hạn.</summary>
    [HttpGet("oidc/start", Name = "getAuthStaffOidcStart")]
    public IActionResult Start([FromQuery(Name = "returnTo"), StringLength(ReturnTo.MaxLength), RegularExpression(ReturnTo.Pattern)] string? returnTo)
    {
        if (!options.Value.IsConfigured)
        {
            return Problem(ErrorCodes.DependencyUnavailable, "Staff sign-in unavailable", "The identity provider is not configured.");
        }

        return Challenge(new AuthenticationProperties { RedirectUri = ReturnTo.Sanitize(returnTo) }, StaffAuthDefaults.OidcScheme);
    }

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

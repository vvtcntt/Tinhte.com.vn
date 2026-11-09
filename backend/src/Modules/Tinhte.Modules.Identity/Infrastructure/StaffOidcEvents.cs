using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tinhte.Modules.Identity.Application;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.Identity.Infrastructure;

/// <summary>
/// Sự kiện OIDC của đăng nhập staff (GET /auth/staff/oidc/start → IdP → /auth/staff/oidc/callback).
/// State, nonce, PKCE do handler chuẩn của ASP.NET Core sinh/kiểm (cookie __Host- ngắn hạn). Callback hợp lệ → tạo phiên của mình
/// (không dùng cookie auth của ASP.NET) rồi chuyển về returnTo. Lỗi trả Problem theo hợp đồng:
/// state/nonce/PKCE sai → 400 OIDC_STATE_INVALID; IdP báo lỗi → 400 BAD_REQUEST; IdP từ chối / chưa có vai trò / bị khóa → 403 FORBIDDEN.
/// </summary>
public sealed partial class StaffOidcEvents(StaffSignInService signIn, IOptions<StaffAuthOptions> options, ILogger<StaffOidcEvents> logger)
    : OpenIdConnectEvents
{
    public override Task RedirectToIdentityProvider(RedirectContext context)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.AcrValues)) context.ProtocolMessage.AcrValues = options.Value.AcrValues;
        return Task.CompletedTask;
    }

    public override async Task TicketReceived(TicketReceivedContext context)
    {
        context.HandleResponse(); // tự xử lý: không SignIn bằng scheme cookie của ASP.NET
        var result = await signIn.SignInAsync(context.Principal!, context.HttpContext, context.HttpContext.RequestAborted);
        switch (result)
        {
            case StaffSignInResult.Success success:
                StaffCookies.Issue(context.Response, success, options.Value.AbsoluteLifetime);
                // RemoteAuthenticationHandler chuyển Properties.RedirectUri sang ReturnUri trước sự kiện này.
                context.Response.Redirect(ReturnTo.Sanitize(context.ReturnUri));
                break;
            case StaffSignInResult.NoRoles:
                await Problems.WriteAsync(context.HttpContext, ErrorCodes.Forbidden, "No CMS role",
                    "Your account has no CMS role yet. Ask an administrator to grant access.");
                break;
            default:
                await Problems.WriteAsync(context.HttpContext, ErrorCodes.Forbidden, "Account inactive", "Your account is suspended or inactive.");
                break;
        }
    }

    public override async Task AccessDenied(AccessDeniedContext context)
    {
        context.HandleResponse();
        await Problems.WriteAsync(context.HttpContext, ErrorCodes.Forbidden, "Sign-in denied", "The identity provider denied the sign-in.");
    }

    public override async Task RemoteFailure(RemoteFailureContext context)
    {
        context.HandleResponse();
        LogFailure(logger, context.Failure);
        if (context.Request.Query.ContainsKey("error"))
        {
            await Problems.WriteAsync(context.HttpContext, ErrorCodes.BadRequest, "Sign-in failed", "The identity provider returned an error.");
            return;
        }

        await Problems.WriteAsync(context.HttpContext, ErrorCodes.OidcStateInvalid, "Sign-in verification failed",
            "The sign-in response could not be verified (state, nonce or PKCE). Start the sign-in again.");
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Warning, Message = "Staff OIDC callback failed")]
    private static partial void LogFailure(ILogger logger, Exception? exception);
}

/// <summary>returnTo phải là đường dẫn tương đối trên chính host CMS (components.parameters.ReturnTo) — chặn open redirect.</summary>
public static class ReturnTo
{
    public const string Pattern = @"^/(?!/)[^\s\\]*$";
    public const int MaxLength = 500;

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength } && System.Text.RegularExpressions.Regex.IsMatch(value, Pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));

    public static string Sanitize(string? value) => IsValid(value) ? value! : "/";
}

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Tinhte.Modules.Identity.Application;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.Identity.Infrastructure;

/// <summary>
/// Sự kiện OIDC của đăng nhập staff (GET /auth/staff/oidc/start → IdP → /auth/staff/oidc/callback).
/// State, nonce, PKCE do handler chuẩn của ASP.NET Core sinh/kiểm (cookie __Host- ngắn hạn). Callback hợp lệ → tạo phiên của mình
/// (không dùng cookie auth của ASP.NET) rồi chuyển về returnTo. Lỗi theo hợp đồng:
/// state/nonce/PKCE sai → 400 OIDC_STATE_INVALID; IdP báo lỗi → 400 BAD_REQUEST; IdP từ chối / chưa có vai trò / bị khóa → 403 FORBIDDEN.
/// Trình duyệt (Accept ưu tiên text/html) nhận 302 về trang CMS /login?error=&amp;reason= thay vì JSON (x-browser-error-redirect, ADR-0004 A-4).
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
        StaffSignInResult result;
        try
        {
            result = await signIn.SignInAsync(context.Principal!, context.HttpContext, context.HttpContext.RequestAborted);
        }
        catch (Exception e) when (e is DomainProblemException { Code: ErrorCodes.DependencyUnavailable } or NpgsqlException)
        {
            // Sự kiện OIDC nằm ngoài MVC (không có DomainExceptionFilter): tự trả 503 thay vì để thành 500.
            LogSessionStoreUnavailable(logger, e);
            await CallbackErrors.WriteAsync(context.HttpContext, ErrorCodes.DependencyUnavailable, CallbackErrors.Unavailable,
                "Staff sign-in unavailable", "The session store is unavailable. Try again later.", context.ReturnUri);
            return;
        }

        switch (result)
        {
            case StaffSignInResult.Success success:
                StaffCookies.Issue(context.Response, success, options.Value.AbsoluteLifetime);
                // RemoteAuthenticationHandler chuyển Properties.RedirectUri sang ReturnUri trước sự kiện này.
                context.Response.Redirect(ReturnTo.Sanitize(context.ReturnUri));
                break;
            case StaffSignInResult.NoRoles:
                await CallbackErrors.WriteAsync(context.HttpContext, ErrorCodes.Forbidden, CallbackErrors.NoRole, "No CMS role",
                    "Your account has no CMS role yet. Ask an administrator to grant access.", context.ReturnUri);
                break;
            default:
                await CallbackErrors.WriteAsync(context.HttpContext, ErrorCodes.Forbidden, CallbackErrors.Inactive, "Account inactive",
                    "Your account is suspended or inactive.", context.ReturnUri);
                break;
        }
    }

    public override async Task AccessDenied(AccessDeniedContext context)
    {
        context.HandleResponse();
        await CallbackErrors.WriteAsync(context.HttpContext, ErrorCodes.Forbidden, CallbackErrors.Denied, "Sign-in denied",
            "The identity provider denied the sign-in.", context.ReturnUrl);
    }

    public override async Task RemoteFailure(RemoteFailureContext context)
    {
        context.HandleResponse();
        LogFailure(logger, context.Failure);
        if (IsUnreachable(context.Failure))
        {
            await CallbackErrors.WriteAsync(context.HttpContext, ErrorCodes.DependencyUnavailable, CallbackErrors.Unavailable,
                "Staff sign-in unavailable", "The identity provider cannot be reached. Try again later.", context.Properties?.RedirectUri);
            return;
        }

        if (context.Request.Query.ContainsKey("error"))
        {
            await CallbackErrors.WriteAsync(context.HttpContext, ErrorCodes.BadRequest, CallbackErrors.IdpError, "Sign-in failed",
                "The identity provider returned an error.", context.Properties?.RedirectUri);
            return;
        }

        // State không xác minh được → không tin returnTo trong đó.
        await CallbackErrors.WriteAsync(context.HttpContext, ErrorCodes.OidcStateInvalid, CallbackErrors.StateInvalid, "Sign-in verification failed",
            "The sign-in response could not be verified (state, nonce or PKCE). Start the sign-in again.", null);
    }

    /// <summary>Không gọi được token endpoint / discovery của IdP (mạng, DNS, timeout) — khác với phản hồi sai.</summary>
    private static bool IsUnreachable(Exception? failure)
    {
        for (var e = failure; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException) return true;
        }

        return false;
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Warning, Message = "Staff OIDC callback failed")]
    private static partial void LogFailure(ILogger logger, Exception? exception);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Error, Message = "Staff session store unavailable during sign-in")]
    private static partial void LogSessionStoreUnavailable(ILogger logger, Exception exception);
}

/// <summary>
/// Lỗi callback: Problem cho client API; 302 về trang đăng nhập CMS cho trình duyệt (x-browser-error-redirect của hợp đồng).
/// </summary>
public static class CallbackErrors
{
    public const string LoginPath = "/login";
    public const string StateInvalid = "state_invalid";
    public const string IdpError = "idp_error";
    public const string Denied = "denied";
    public const string NoRole = "no_role";
    public const string Inactive = "inactive";
    public const string Unavailable = "unavailable";

    public static Task WriteAsync(HttpContext http, string code, string reason, string title, string detail, string? returnTo)
    {
        if (!PrefersHtml(http.Request))
        {
            return Problems.WriteAsync(http, code, title, detail);
        }

        var location = QueryHelpers.AddQueryString(LoginPath, new Dictionary<string, string?>
        {
            ["error"] = code,
            ["reason"] = reason,
            ["returnTo"] = ReturnTo.IsValid(returnTo) && returnTo != "/" ? returnTo : null,
        }.Where(kv => kv.Value is not null));
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Redirect(location);
        return Task.CompletedTask;
    }

    /// <summary>Trình duyệt điều hướng gửi Accept: text/html,...; client API gửi application/json hoặc problem+json.</summary>
    public static bool PrefersHtml(HttpRequest request)
    {
        double html = 0, json = 0;
        foreach (var media in request.GetTypedHeaders().Accept)
        {
            var q = media.Quality ?? 1;
            var type = media.MediaType.Value;
            if (string.Equals(type, "text/html", StringComparison.OrdinalIgnoreCase)) html = Math.Max(html, q);
            else if (string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "application/problem+json", StringComparison.OrdinalIgnoreCase)) json = Math.Max(json, q);
        }

        return html > json;
    }
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

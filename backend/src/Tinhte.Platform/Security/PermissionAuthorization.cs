using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Tinhte.Platform.Errors;

namespace Tinhte.Platform.Security;

/// <summary>
/// <c>[RequirePermission(Permissions.ContentPublish)]</c> trên action/controller admin. Dùng cơ chế authorization chuẩn của
/// ASP.NET Core (IAuthorizationRequirementData), lỗi trả Problem theo hợp đồng: chưa đăng nhập → 401 UNAUTHENTICATED;
/// thiếu quyền hoặc thiếu MFA → 403 FORBIDDEN. Service vẫn nên gọi <c>User.RequirePermission</c> khi quyết định phụ thuộc dữ liệu.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : AuthorizeAttribute, IAuthorizationRequirementData
{
    public string Permission { get; } = PermissionMatrix.AllPermissions.Contains(permission)
        ? permission
        : throw new ArgumentException($"Unknown permission '{permission}'", nameof(permission));

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new PermissionRequirement(Permission)];
}

/// <summary><c>[RequireStaffSession]</c>: chỉ cần phiên staff hợp lệ (ví dụ đăng xuất, xem phiên hiện tại).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireStaffSessionAttribute : AuthorizeAttribute, IAuthorizationRequirementData
{
    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new StaffSessionRequirement()];
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed record StaffSessionRequirement : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (var requirement in context.PendingRequirements.ToList())
        {
            switch (requirement)
            {
                case StaffSessionRequirement when context.User.IsStaff():
                    context.Succeed(requirement);
                    break;
                case PermissionRequirement p:
                    switch (context.User.Evaluate(p.Permission))
                    {
                        case AccessDecision.Allowed:
                            context.Succeed(requirement);
                            break;
                        case AccessDecision.MfaRequired:
                            context.Fail(new AuthorizationFailureReason(this, $"MFA|'{p.Permission}' requires a session with multi-factor authentication. Sign in again with MFA."));
                            break;
                        default:
                            context.Fail(new AuthorizationFailureReason(this, $"PERM|Your roles do not grant '{p.Permission}'."));
                            break;
                    }

                    break;
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Thay 401/403 trống mặc định bằng application/problem+json đúng catalog.</summary>
public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded) return _default.HandleAsync(next, context, policy, authorizeResult);

        if (context.User.Identity?.IsAuthenticated != true || authorizeResult.Challenged)
        {
            return Problems.WriteAsync(context, ErrorCodes.Unauthenticated, "Authentication required", "Sign in to continue.");
        }

        var reason = authorizeResult.AuthorizationFailure?.FailureReasons.FirstOrDefault()?.Message;
        var (title, detail) = reason switch
        {
            { } r when r.StartsWith("MFA|", StringComparison.Ordinal) => ("MFA required", r[4..]),
            { } r when r.StartsWith("PERM|", StringComparison.Ordinal) => ("Forbidden", r[5..]),
            _ => ("Forbidden", "You are not allowed to perform this action."),
        };
        return Problems.WriteAsync(context, ErrorCodes.Forbidden, title, detail);
    }
}

/// <summary>
/// Lớp CSRF 2 (ADR-0001 D4): request ghi được xác thực bằng cookie phiên phải gửi <c>X-CSRF-Token</c> khớp token gắn với phiên.
/// Chạy SAU authentication và TRƯỚC authorization → thứ tự lỗi của catalog: 401 → 403 CSRF_INVALID → 403 FORBIDDEN.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-CSRF-Token";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsSafe(context.Request.Method) && context.User.FindFirst(TinhteClaims.CsrfHash)?.Value is { } expected
            && !Matches(context.Request.Headers[HeaderName].ToString(), expected))
        {
            await Problems.WriteAsync(context, ErrorCodes.CsrfInvalid, "CSRF token invalid", $"Send the {HeaderName} header issued with your session.");
            return;
        }

        await next(context);
    }

    public static bool Matches(string token, string expectedHash) =>
        token.Length >= 8 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(TokenHash(token)), System.Text.Encoding.ASCII.GetBytes(expectedHash));

    public static string TokenHash(string token) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
}

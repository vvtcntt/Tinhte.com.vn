using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests.Support;

/// <summary>
/// Đăng nhập giả cho test (chỉ nằm trong assembly test): header <c>X-Test-User</c> → claim <c>sub</c>.
/// Thêm <c>X-Test-Staff-Roles</c> (ví dụ "EDITOR,APPROVER") → principal staff như StaffSessionHandler tạo;
/// <c>X-Test-Mfa: true</c> → đã qua MFA; <c>X-Test-Csrf</c> → token CSRF gắn với "phiên".
/// Luồng thật (OIDC + cookie phiên) được kiểm ở StaffAuthFlowTests.
/// </summary>
internal sealed class TestUserStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-User";
    public const string RolesHeader = "X-Test-Staff-Roles";
    public const string MfaHeader = "X-Test-Mfa";
    public const string CsrfHeader = "X-Test-Csrf";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var user) && !string.IsNullOrEmpty(user))
            {
                var claims = new List<Claim> { new("sub", user.ToString()) };
                if (context.Request.Headers.TryGetValue(RolesHeader, out var roles))
                {
                    claims.Add(new Claim(TinhteClaims.SessionKind, TinhteClaims.StaffSession));
                    claims.Add(new Claim(TinhteClaims.MfaSatisfied, context.Request.Headers[MfaHeader] == "true" ? "true" : "false"));
                    claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(TinhteClaims.Role, r.Trim())));
                }

                if (context.Request.Headers.TryGetValue(CsrfHeader, out var csrf))
                {
                    claims.Add(new Claim(TinhteClaims.CsrfHash, CsrfMiddleware.TokenHash(csrf.ToString())));
                }

                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "sub", TinhteClaims.Role));
            }

            await nextMiddleware(context);
        });
        next(app);
    };
}

internal static class TestHost
{
    public static readonly Uri Www = new("http://www.tinhte.localhost");
    public static readonly Uri Cms = new("http://cms.tinhte.localhost");
    public const string WwwOrigin = "http://www.tinhte.localhost:8080";
    public const string CmsOrigin = "http://cms.tinhte.localhost:8080";

    /// <summary>App + controller probe trong assembly test + đăng nhập giả; tùy chọn trỏ tới PostgreSQL.</summary>
    public static WebApplicationFactory<Program> WithProbes(this WebApplicationFactory<Program> factory, string? connectionString = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            if (connectionString is not null) builder.UseSetting("ConnectionStrings:Tinhte", connectionString);
            builder.ConfigureTestServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(TestHost).Assembly);
                services.AddTransient<IStartupFilter, TestUserStartupFilter>();
            });
        });

    public static HttpClient Client(this WebApplicationFactory<Program> factory, Uri? baseAddress = null) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = baseAddress ?? Www, AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>Request ghi (POST/PATCH…) phải có Origin hợp lệ — OriginGuardMiddleware (ADR-0001).</summary>
    public static HttpRequestMessage Write(HttpMethod method, string path, string? json = null, string? user = null, string? idempotencyKey = null, string? ifMatch = null, string origin = WwwOrigin)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("Origin", origin);
        if (user is not null) request.Headers.Add(TestUserStartupFilter.Header, user);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }
}

/// <summary>
/// Test cần PostgreSQL: chạy khi có biến môi trường <c>TT_TEST_PG</c> (chuỗi kết nối Npgsql), ngược lại bỏ qua.
/// CI dùng PostgreSQL thật; máy dev không có Docker dùng tools/pglite-server (Pooling=false).
/// </summary>
public sealed class DbFactAttribute : FactAttribute
{
    public const string EnvVar = "TT_TEST_PG";

    public DbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            Skip = $"{EnvVar} is not set (PostgreSQL connection string for DB tests).";
        }
    }
}

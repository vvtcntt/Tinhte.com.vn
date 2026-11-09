using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Tinhte.Api.Tests.Support;

/// <summary>
/// Đăng nhập giả cho test: header <c>X-Test-User</c> → claim <c>sub</c>. Chỉ nằm trong assembly test;
/// xác thực thật (OIDC staff/member) thuộc WBS-017.
/// </summary>
internal sealed class TestUserStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-User";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var user) && !string.IsNullOrEmpty(user))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "Test"));
            }

            await nextMiddleware(context);
        });
        next(app);
    };
}

internal static class TestHost
{
    public static readonly Uri Www = new("http://www.tinhte.localhost");
    public const string WwwOrigin = "http://www.tinhte.localhost:8080";

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

    public static HttpClient Client(this WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = Www, AllowAutoRedirect = false });

    /// <summary>Request ghi (POST/PATCH…) phải có Origin hợp lệ — OriginGuardMiddleware (ADR-0001).</summary>
    public static HttpRequestMessage Write(HttpMethod method, string path, string? json = null, string? user = null, string? idempotencyKey = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("Origin", WwwOrigin);
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

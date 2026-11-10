using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Modules.Identity.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Identity.Infrastructure;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>WBS-018: RBAC + CSRF + thứ tự lỗi (không cần DB). Luồng đăng nhập thật: StaffAuthFlowTests.</summary>
public sealed partial class AuthRbacTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Cms() => factory.WithProbes().Client(TestHost.Cms);

    private static HttpRequestMessage Get(string path, string? user = null, string? roles = null, bool mfa = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (user is not null) request.Headers.Add(TestUserStartupFilter.Header, user);
        if (roles is not null) request.Headers.Add(TestUserStartupFilter.RolesHeader, roles);
        if (mfa) request.Headers.Add(TestUserStartupFilter.MfaHeader, "true");
        return request;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    // ---------------------------------------------------------------- ma trận quyền

    [Fact]
    public void Role_catalog_matches_migration_0313_check_constraint()
    {
        var sql = File.ReadAllText(RepoPaths.File("doc", "07_AI_REVIEW", "v0.3", "db", "migrations", "0313_staff_access.up.sql"));
        var check = RoleCheck().Match(sql);
        Assert.True(check.Success);
        var roles = Regex.Matches(check.Groups["list"].Value, "'([A-Z_]+)'").Select(m => m.Groups[1].Value).Order().ToList();
        Assert.Equal(StaffRoles.All.Order(), roles);
    }

    [Fact]
    public void Code_constants_match_the_shared_rbac_json()
    {
        var constants = typeof(Permissions).GetFields().Select(f => (string)f.GetValue(null)!).Order().ToList();
        Assert.Equal(constants, PermissionMatrix.Document.Permissions.Keys.Order());
        Assert.Equal(StaffRoles.All.Order(), PermissionMatrix.Document.Roles.Keys.Order());
        // File nhúng phải trùng file trong repo (CMS đọc trực tiếp file này).
        using var json = JsonDocument.Parse(File.ReadAllText(RepoPaths.File("config", "rbac", "rbac.json")));
        Assert.Equal(json.RootElement.GetProperty("roles").EnumerateObject().Select(p => p.Name).Order(), PermissionMatrix.Document.Roles.Keys.Order());
    }

    [Fact]
    public void Every_role_grants_known_permissions_and_privileged_permissions_need_mfa()
    {
        foreach (var role in StaffRoles.All)
        {
            Assert.NotEmpty(PermissionMatrix.PermissionsOf(role));
            Assert.All(PermissionMatrix.PermissionsOf(role), p => Assert.Contains(p, PermissionMatrix.AllPermissions));
        }

        Assert.All(PermissionMatrix.MfaRequired, p => Assert.Contains(p, PermissionMatrix.AllPermissions));
        Assert.All(PermissionMatrix.AllPermissions, p => Assert.Contains(StaffRoles.All, r => PermissionMatrix.PermissionsOf(r).Contains(p)));
        Assert.True(PermissionMatrix.IsPrivileged(StaffRoles.Approver));
        Assert.True(PermissionMatrix.IsPrivileged(StaffRoles.Admin));
        Assert.False(PermissionMatrix.IsPrivileged(StaffRoles.Author));
    }

    [Theory]
    [InlineData("AUTHOR", false, Permissions.ContentWriteOwn, AccessDecision.Allowed)]
    [InlineData("AUTHOR", false, Permissions.ContentWriteAny, AccessDecision.MissingPermission)]
    [InlineData("AUTHOR", true, Permissions.ContentPublish, AccessDecision.MissingPermission)]
    [InlineData("EDITOR", false, Permissions.ContentReview, AccessDecision.Allowed)]
    [InlineData("EDITOR", true, Permissions.ContentApprove, AccessDecision.MissingPermission)]   // tách author/editor/approver (FR-ID-002)
    [InlineData("APPROVER", false, Permissions.ContentPublish, AccessDecision.MfaRequired)]
    [InlineData("APPROVER", true, Permissions.ContentPublish, AccessDecision.Allowed)]
    [InlineData("MODERATOR", true, Permissions.ContentPublish, AccessDecision.MissingPermission)]
    [InlineData("ADMIN", true, Permissions.ContentPublish, AccessDecision.MissingPermission)]     // admin quản lý user, không tự xuất bản
    [InlineData("ADMIN", false, Permissions.UsersRead, AccessDecision.Allowed)]
    [InlineData("ADMIN", false, Permissions.UsersManage, AccessDecision.MfaRequired)]
    [InlineData("UNKNOWN_ROLE", true, Permissions.ContentRead, AccessDecision.MissingPermission)]
    // Radar tin GĐ1 (ADR-0008 §8, DRAFT): EDITOR quản lý nguồn, duyệt chế độ khai thác cần MFA (APPROVER tạm thời — A-3).
    [InlineData("EDITOR", false, Permissions.AggregationSourcesManage, AccessDecision.Allowed)]
    [InlineData("EDITOR", true, Permissions.AggregationSourcesApprove, AccessDecision.MissingPermission)]
    [InlineData("AUTHOR", false, Permissions.AggregationRead, AccessDecision.MissingPermission)]
    [InlineData("APPROVER", false, Permissions.AggregationSourcesApprove, AccessDecision.MfaRequired)]
    [InlineData("APPROVER", true, Permissions.AggregationSourcesApprove, AccessDecision.Allowed)]
    [InlineData("APPROVER", true, Permissions.AggregationSourcesManage, AccessDecision.MissingPermission)]
    public void Matrix_decisions(string role, bool mfa, string permission, AccessDecision expected) =>
        Assert.Equal(expected, PermissionMatrix.Evaluate([role], mfa, permission));

    [Fact]
    public void Unknown_permission_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => PermissionMatrix.Evaluate(["ADMIN"], true, "users.delete_everything"));
        Assert.Throws<ArgumentException>(() => new RequirePermissionAttribute("nope"));
    }

    // ---------------------------------------------------------------- RBAC qua HTTP (bề mặt admin, site cms)

    [Fact]
    public async Task Anonymous_is_401_unauthenticated()
    {
        using var client = Cms();
        using var response = await client.SendAsync(Get("/api/v1/admin/_rbac/publish"));
        await ProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task Authenticated_non_staff_principal_is_403()
    {
        using var client = Cms();
        using var response = await client.SendAsync(Get("/api/v1/admin/_rbac/read", user: Guid.NewGuid().ToString()));
        await ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("EDITOR", false, HttpStatusCode.Forbidden, "Forbidden")]
    [InlineData("APPROVER", false, HttpStatusCode.Forbidden, "MFA required")]
    [InlineData("EDITOR,APPROVER", true, HttpStatusCode.OK, null)]
    public async Task Attribute_permission_check(string roles, bool mfa, HttpStatusCode status, string? title)
    {
        using var client = Cms();
        var user = Guid.NewGuid();
        using var response = await client.SendAsync(Get("/api/v1/admin/_rbac/publish", user.ToString(), roles, mfa));
        Assert.Equal(status, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (title is null) Assert.Equal(user, body.GetProperty("userId").GetGuid());
        else Assert.Equal(title, body.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(null, null, false, HttpStatusCode.Unauthorized)]
    [InlineData("EDITOR", Permissions.ContentPublish, true, HttpStatusCode.Forbidden)]
    [InlineData("APPROVER", Permissions.ContentPublish, false, HttpStatusCode.Forbidden)]
    [InlineData("APPROVER", Permissions.ContentPublish, true, HttpStatusCode.OK)]
    public async Task Domain_layer_permission_check(string? roles, string? permission, bool mfa, HttpStatusCode status)
    {
        using var client = Cms();
        var user = roles is null ? null : Guid.NewGuid().ToString();
        using var response = await client.SendAsync(Get($"/api/v1/admin/_rbac/domain?permission={permission ?? Permissions.ContentRead}", user, roles, mfa));
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task Admin_surface_is_not_served_on_public_site()
    {
        using var client = factory.WithProbes().Client(TestHost.Www);
        using var response = await client.SendAsync(Get("/api/v1/admin/_rbac/read", Guid.NewGuid().ToString(), "EDITOR"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- CSRF lớp 2 + thứ tự lỗi

    private static HttpRequestMessage Mutation(string? user, string? roles, string? sessionCsrf, string? headerCsrf)
    {
        var request = TestHost.Write(HttpMethod.Post, "/api/v1/admin/_rbac/taxonomy", "{}", user: user, origin: TestHost.CmsOrigin);
        if (roles is not null) request.Headers.Add(TestUserStartupFilter.RolesHeader, roles);
        if (sessionCsrf is not null) request.Headers.Add(TestUserStartupFilter.CsrfHeader, sessionCsrf);
        if (headerCsrf is not null) request.Headers.Add(CsrfMiddleware.HeaderName, headerCsrf);
        return request;
    }

    [Fact]
    public async Task Unauthenticated_mutation_is_401_before_csrf()
    {
        using var client = Cms();
        using var response = await client.SendAsync(Mutation(null, null, null, null));
        await ProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token-123")]
    [InlineData("short")]
    public async Task Session_mutation_without_matching_csrf_token_is_403_csrf_invalid(string? header)
    {
        using var client = Cms();
        using var response = await client.SendAsync(Mutation(Guid.NewGuid().ToString(), "EDITOR", "csrf-token-of-session", header));
        await ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.CsrfInvalid);
    }

    [Fact]
    public async Task Csrf_is_checked_before_permission()
    {
        using var client = Cms();
        using var response = await client.SendAsync(Mutation(Guid.NewGuid().ToString(), "AUTHOR", "csrf-token-of-session", null));
        await ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.CsrfInvalid);
        using var withToken = await client.SendAsync(Mutation(Guid.NewGuid().ToString(), "AUTHOR", "csrf-token-of-session", "csrf-token-of-session"));
        await ProblemAsync(withToken, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Session_mutation_with_matching_csrf_token_succeeds()
    {
        using var client = Cms();
        using var response = await client.SendAsync(Mutation(Guid.NewGuid().ToString(), "EDITOR", "csrf-token-of-session", "csrf-token-of-session"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------- endpoint đăng nhập (chưa cấu hình IdP)

    [Theory]
    [InlineData("/")]
    [InlineData("/articles?status=DRAFT")]
    [InlineData("/a/b-c_d/e")]
    public void Return_to_accepts_relative_paths(string value) => Assert.True(ReturnTo.IsValid(value));

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example/")]
    [InlineData("/\\evil.example")]
    [InlineData("/a b")]
    [InlineData("articles")]
    [InlineData("")]
    public void Return_to_rejects_open_redirects(string value)
    {
        Assert.False(ReturnTo.IsValid(value));
        Assert.Equal("/", ReturnTo.Sanitize(value));
    }

    [Theory]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", true)]
    [InlineData("text/html", true)]
    [InlineData("application/json", false)]
    [InlineData("application/problem+json", false)]
    [InlineData("text/html;q=0.5, application/json", false)]
    [InlineData("*/*", false)]
    [InlineData("", false)]
    public void Callback_errors_redirect_only_browsers(string accept, bool expected)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (accept.Length > 0) context.Request.Headers.Accept = accept;
        Assert.Equal(expected, CallbackErrors.PrefersHtml(context.Request));
    }

    [Fact]
    public async Task Oidc_start_rejects_absolute_return_to_with_400()
    {
        using var client = Cms();
        using var response = await client.GetAsync("/api/v1/auth/staff/oidc/start?returnTo=" + Uri.EscapeDataString("https://evil.example/"));
        await ProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
    }

    [Fact]
    public async Task Oidc_start_without_configured_idp_is_503()
    {
        using var client = Cms();
        using var response = await client.GetAsync("/api/v1/auth/staff/oidc/start");
        await ProblemAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
    }

    [Fact]
    public async Task Oidc_start_without_configured_idp_redirects_browser_to_login()
    {
        using var client = Cms();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/staff/oidc/start?returnTo=%2Fvideos");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?error=DEPENDENCY_UNAVAILABLE&reason=unavailable&returnTo=%2Fvideos", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Oidc_start_with_unreachable_database_is_503_before_going_to_the_idp()
    {
        using var idp = new FakeOidcProvider();
        using var app = factory.WithProbes("Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=2").WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Staff:Authority", FakeOidcProvider.Issuer);
            b.UseSetting("Auth:Staff:ClientId", FakeOidcProvider.ClientId);
            b.ConfigureTestServices(s => s.Configure<OpenIdConnectOptions>(StaffAuthDefaults.OidcScheme, o => o.BackchannelHttpHandler = idp));
        });
        using var client = app.Client(TestHost.Cms);
        using var response = await client.GetAsync("/api/v1/auth/staff/oidc/start");
        await ProblemAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
    }

    [Fact]
    public async Task Staff_session_endpoint_requires_a_session()
    {
        using var client = Cms();
        using var response = await client.GetAsync("/api/v1/admin/auth/session");
        await ProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    [Fact]
    public void Production_without_staff_idp_configuration_fails_at_startup()
    {
        using var production = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting(HmacCursorCodec.SigningKeyConfig, Convert.ToBase64String(new byte[32]));
        });
        var error = Assert.ThrowsAny<Exception>(() => production.CreateClient());
        Assert.Contains("Auth:Staff", error.ToString(), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"ck_user_roles_role CHECK \(role IN \((?<list>[^)]*)\)\)")]
    private static partial Regex RoleCheck();
}

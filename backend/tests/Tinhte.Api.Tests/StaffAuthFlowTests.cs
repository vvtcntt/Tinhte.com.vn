using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Identity.Application;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>
/// WBS-017 đầu-cuối trên PostgreSQL thật + IdP giả: start → IdP → callback → cookie phiên → API admin → đăng xuất.
/// Kiểm: state/nonce/PKCE, MFA theo amr, vai trò từ DB, cookie __Host-, CSRF, thu hồi/hết hạn phiên, cô lập theo site.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StaffAuthFlowTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly MigratedDatabaseFixture _db;
    private readonly FakeOidcProvider _idp = new();
    private readonly WebApplicationFactory<Program> _app;

    public StaffAuthFlowTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory)
    {
        _db = db;
        _app = factory.WithProbes(db.ConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Staff:Authority", FakeOidcProvider.Issuer);
            builder.UseSetting("Auth:Staff:ClientId", FakeOidcProvider.ClientId);
            builder.UseSetting("Auth:Staff:ClientSecret", FakeOidcProvider.ClientSecret);
            builder.ConfigureTestServices(services =>
                services.Configure<OpenIdConnectOptions>(StaffAuthDefaults.OidcScheme, o => o.BackchannelHttpHandler = _idp));
        });
    }

    public void Dispose()
    {
        _app.Dispose();
        _idp.Dispose();
    }

    private sealed record LoginResult(HttpResponseMessage Callback, Dictionary<string, string> Cookies, IReadOnlyList<string> SetCookieHeaders);

    private HttpClient Cms() => _app.Client(TestHost.Cms);

    private static Dictionary<string, string> CookiesOf(HttpResponseMessage response) =>
        (response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
            .Select(v => v.Split(';')[0].Split('=', 2))
            .Where(p => p[1].Length > 0)
            .ToDictionary(p => p[0], p => p[1]);

    private static string CookieHeader(IDictionary<string, string> cookies) => string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}"));

    private async Task SeedStaffAsync(string subject, params string[] roles)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using var user = new NpgsqlCommand("INSERT INTO tt.users (display_name) VALUES (@n) RETURNING id", c, tx);
        user.Parameters.AddWithValue("n", "Staff " + subject[..8]);
        var userId = (Guid)(await user.ExecuteScalarAsync())!;
        await using var identity = new NpgsqlCommand("INSERT INTO tt.user_identities (issuer, subject, user_id) VALUES (@i, @s, @u)", c, tx);
        identity.Parameters.AddWithValue("i", FakeOidcProvider.Issuer);
        identity.Parameters.AddWithValue("s", subject);
        identity.Parameters.AddWithValue("u", userId);
        await identity.ExecuteNonQueryAsync();
        foreach (var role in roles)
        {
            await using var grant = new NpgsqlCommand("INSERT INTO tt.user_roles (user_id, role, reason) VALUES (@u, @r, 'test seed')", c, tx);
            grant.Parameters.AddWithValue("u", userId);
            grant.Parameters.AddWithValue("r", role);
            await grant.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private async Task<LoginResult> LoginAsync(HttpClient client, string subject, string[] amr, string returnTo = "/articles", Func<string, string>? tamperState = null, IDictionary<string, string>? existingCookies = null)
    {
        using var start = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/staff/oidc/start?returnTo=" + Uri.EscapeDataString(returnTo));
        using var startResponse = await client.SendAsync(start);
        Assert.Equal(HttpStatusCode.Redirect, startResponse.StatusCode);
        var authorize = startResponse.Headers.Location!;
        Assert.StartsWith(FakeOidcProvider.Issuer + "/authorize", authorize.ToString(), StringComparison.Ordinal);
        var query = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("http://cms.tinhte.localhost/api/v1/auth/staff/oidc/callback", query["redirect_uri"]);

        var flowCookies = CookiesOf(startResponse);
        Assert.All(flowCookies.Keys, k => Assert.StartsWith("__Host-tt_cms_oidc_", k, StringComparison.Ordinal));
        var code = _idp.Authorize(subject, query["nonce"]!, query["code_challenge"]!, amr, "Staff Tester");
        var state = tamperState is null ? query["state"]! : tamperState(query["state"]!);

        var callback = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/auth/staff/oidc/callback?code={code}&state={Uri.EscapeDataString(state)}");
        var sendCookies = new Dictionary<string, string>(flowCookies);
        foreach (var c in existingCookies ?? new Dictionary<string, string>()) sendCookies[c.Key] = c.Value;
        callback.Headers.Add("Cookie", CookieHeader(sendCookies));
        var response = await client.SendAsync(callback);
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var raw) ? raw.ToList() : [];
        return new LoginResult(response, CookiesOf(response), setCookies);
    }

    private static HttpRequestMessage WithSession(HttpMethod method, string path, IDictionary<string, string> cookies, bool csrf = false)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", CookieHeader(cookies.Where(c => c.Key.StartsWith("__Host-tt_cms_", StringComparison.Ordinal) && !c.Key.Contains("oidc", StringComparison.Ordinal)).ToDictionary()));
        if (method != HttpMethod.Get) request.Headers.Add("Origin", TestHost.CmsOrigin);
        if (csrf) request.Headers.Add(CsrfMiddleware.HeaderName, cookies[StaffAuthDefaults.CsrfCookie]);
        return request;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    // ---------------------------------------------------------------- đăng nhập

    [DbFact]
    public async Task Login_with_mfa_creates_host_only_session_and_redirects_to_return_to()
    {
        var subject = Guid.NewGuid().ToString();
        await SeedStaffAsync(subject, StaffRoles.Approver);
        using var client = Cms();
        var login = await LoginAsync(client, subject, ["pwd", "mfa"], "/articles?status=DRAFT");

        Assert.Equal(HttpStatusCode.Redirect, login.Callback.StatusCode);
        Assert.Equal("/articles?status=DRAFT", login.Callback.Headers.Location?.OriginalString);

        var session = login.SetCookieHeaders.Single(h => h.StartsWith(StaffAuthDefaults.SessionCookie + "=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("secure", session, StringComparison.Ordinal);
        Assert.Contains("httponly", session, StringComparison.Ordinal);
        Assert.Contains("samesite=lax", session, StringComparison.Ordinal);
        Assert.Contains("path=/", session, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", session, StringComparison.Ordinal);   // ADR-0001 D4: không Domain=.tinhte.com.vn
        var csrf = login.SetCookieHeaders.Single(h => h.StartsWith(StaffAuthDefaults.CsrfCookie + "=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.DoesNotContain("httponly", csrf, StringComparison.Ordinal);    // JS của CMS đọc để gửi X-CSRF-Token

        using var me = await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/auth/session", login.Cookies));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["APPROVER"], body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.True(body.GetProperty("mfaSatisfied").GetBoolean());

        using var publish = await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/_rbac/publish", login.Cookies));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Equal(1L, await _db.ScalarAsync<long>(
            $"SELECT count(*) FROM tt.staff_sessions s JOIN tt.user_identities i ON i.user_id = s.user_id WHERE i.subject = '{subject}' AND s.revoked_at IS NULL"));
    }

    [DbFact]
    public async Task Login_without_mfa_keeps_privileged_permissions_locked()
    {
        var subject = Guid.NewGuid().ToString();
        await SeedStaffAsync(subject, StaffRoles.Approver);
        using var client = Cms();
        var login = await LoginAsync(client, subject, ["pwd"]);
        Assert.Equal(HttpStatusCode.Redirect, login.Callback.StatusCode);

        var body = await (await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/auth/session", login.Cookies))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("mfaSatisfied").GetBoolean());
        using var publish = await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/_rbac/publish", login.Cookies));
        var problem = await ProblemAsync(publish, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.Equal("MFA required", problem.GetProperty("title").GetString());
        using var read = await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/_rbac/read", login.Cookies));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);   // quyền thường vẫn dùng được
    }

    [DbFact]
    public async Task First_login_without_role_is_provisioned_but_gets_no_session()
    {
        var subject = Guid.NewGuid().ToString();
        using var client = Cms();
        var login = await LoginAsync(client, subject, ["mfa"]);
        await ProblemAsync(login.Callback, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        Assert.False(login.Cookies.ContainsKey(StaffAuthDefaults.SessionCookie));
        Assert.Equal(1L, await _db.ScalarAsync<long>($"SELECT count(*) FROM tt.user_identities WHERE subject = '{subject}'"));
        Assert.Equal(0L, await _db.ScalarAsync<long>(
            $"SELECT count(*) FROM tt.staff_sessions s JOIN tt.user_identities i ON i.user_id = s.user_id WHERE i.subject = '{subject}'"));
    }

    [DbFact]
    public async Task Tampered_state_is_400_oidc_state_invalid()
    {
        var subject = Guid.NewGuid().ToString();
        await SeedStaffAsync(subject, StaffRoles.Editor);
        using var client = Cms();
        var login = await LoginAsync(client, subject, ["mfa"], tamperState: s => s[..^4] + "AAAA");
        await ProblemAsync(login.Callback, HttpStatusCode.BadRequest, ErrorCodes.OidcStateInvalid);
        Assert.False(login.Cookies.ContainsKey(StaffAuthDefaults.SessionCookie));
    }

    [DbFact]
    public async Task Callback_without_correlation_cookie_is_400_oidc_state_invalid()
    {
        using var client = Cms();
        using var start = await client.GetAsync("/api/v1/auth/staff/oidc/start");
        var query = HttpUtility.ParseQueryString(start.Headers.Location!.Query);
        using var callback = await client.GetAsync($"/api/v1/auth/staff/oidc/callback?code=abc&state={Uri.EscapeDataString(query["state"]!)}");
        await ProblemAsync(callback, HttpStatusCode.BadRequest, ErrorCodes.OidcStateInvalid);
    }

    [DbFact]
    public async Task Idp_access_denied_is_403()
    {
        using var client = Cms();
        using var start = await client.GetAsync("/api/v1/auth/staff/oidc/start");
        var query = HttpUtility.ParseQueryString(start.Headers.Location!.Query);
        using var callback = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/auth/staff/oidc/callback?error=access_denied&state={Uri.EscapeDataString(query["state"]!)}");
        callback.Headers.Add("Cookie", CookieHeader(CookiesOf(start)));
        using var response = await client.SendAsync(callback);
        await ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    // ---------------------------------------------------------------- phiên: cô lập site, CSRF, thu hồi, hết hạn

    [DbFact]
    public async Task Staff_session_cookie_is_ignored_on_public_site()
    {
        var subject = Guid.NewGuid().ToString();
        await SeedStaffAsync(subject, StaffRoles.Editor);
        var login = await LoginAsync(Cms(), subject, ["mfa"]);
        using var www = _app.Client(TestHost.Www);
        var who = await (await www.SendAsync(WithSession(HttpMethod.Get, "/api/v1/_whoami", login.Cookies))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(who.GetProperty("authenticated").GetBoolean());
    }

    [DbFact]
    public async Task Logout_requires_csrf_then_revokes_the_session()
    {
        var subject = Guid.NewGuid().ToString();
        await SeedStaffAsync(subject, StaffRoles.Editor);
        using var client = Cms();
        var login = await LoginAsync(client, subject, ["mfa"]);

        using var noCsrf = await client.SendAsync(WithSession(HttpMethod.Post, "/api/v1/auth/staff/logout", login.Cookies));
        await ProblemAsync(noCsrf, HttpStatusCode.Forbidden, ErrorCodes.CsrfInvalid);

        using var logout = await client.SendAsync(WithSession(HttpMethod.Post, "/api/v1/auth/staff/logout", login.Cookies, csrf: true));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), h => h.StartsWith(StaffAuthDefaults.SessionCookie + "=;", StringComparison.Ordinal));

        using var after = await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/auth/session", login.Cookies));
        await ProblemAsync(after, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        Assert.Equal("LOGOUT", await _db.ScalarAsync<string>(
            $"SELECT s.revoked_reason FROM tt.staff_sessions s JOIN tt.user_identities i ON i.user_id = s.user_id WHERE i.subject = '{subject}'"));
    }

    [DbFact]
    public async Task Logging_in_again_rotates_the_previous_session()
    {
        var subject = Guid.NewGuid().ToString();
        await SeedStaffAsync(subject, StaffRoles.Editor);
        using var client = Cms();
        var first = await LoginAsync(client, subject, ["mfa"]);
        var second = await LoginAsync(client, subject, ["mfa"], existingCookies: first.Cookies.Where(c => !c.Key.Contains("oidc", StringComparison.Ordinal)).ToDictionary());
        Assert.Equal(HttpStatusCode.Redirect, second.Callback.StatusCode);

        using var old = await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/auth/session", first.Cookies));
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var current = await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/auth/session", second.Cookies));
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [DbFact]
    public async Task Idle_timeout_suspension_and_role_revocation_apply_immediately()
    {
        var subject = Guid.NewGuid().ToString();
        await SeedStaffAsync(subject, StaffRoles.Editor, StaffRoles.Author);
        using var client = Cms();
        var login = await LoginAsync(client, subject, ["mfa"]);
        var userId = await _db.ScalarAsync<Guid>($"SELECT user_id FROM tt.user_identities WHERE subject = '{subject}'");

        // Thu hồi vai trò cần lý do (FR-ID-007): thiếu → REASON_REQUIRED; có → lịch sử REVOKE.
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        await using (var noReason = new NpgsqlCommand($"DELETE FROM tt.user_roles WHERE user_id = '{userId}' AND role = 'EDITOR'", c))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => noReason.ExecuteNonQueryAsync());
            Assert.Equal("REASON_REQUIRED", error.MessageText);
        }

        // Kết nối mới sau lỗi (PGlite không dùng lại được kết nối vừa lỗi — tools/pglite-server).
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await using var revoke = new NpgsqlCommand(
                $"SELECT set_config('tt.reason', 'moved team', true); DELETE FROM tt.user_roles WHERE user_id = '{userId}' AND role = 'EDITOR';", c, tx);
            await revoke.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        var roles = (await (await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/auth/session", login.Cookies))).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(["AUTHOR"], roles);
        Assert.Equal("GRANT,GRANT,REVOKE", await _db.ScalarAsync<string>(
            $"SELECT string_agg(action, ',' ORDER BY id) FROM tt.user_role_history WHERE user_id = '{userId}'"));

        // Idle quá hạn → 401.
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        await using (var idle = new NpgsqlCommand($"UPDATE tt.staff_sessions SET last_seen_at = now() - interval '31 minutes' WHERE user_id = '{userId}'", c))
        {
            await idle.ExecuteNonQueryAsync();
        }

        using var expired = await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/auth/session", login.Cookies));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        // Đăng nhập lại rồi bị đình chỉ → phiên mất hiệu lực ngay.
        var again = await LoginAsync(client, subject, ["mfa"]);
        await using (var c = await _db.DataSource.OpenConnectionAsync())
        await using (var suspend = new NpgsqlCommand($"UPDATE tt.users SET status = 'SUSPENDED' WHERE id = '{userId}'", c))
        {
            await suspend.ExecuteNonQueryAsync();
        }

        using var suspended = await client.SendAsync(WithSession(HttpMethod.Get, "/api/v1/admin/auth/session", again.Cookies));
        Assert.Equal(HttpStatusCode.Unauthorized, suspended.StatusCode);
        var blocked = await LoginAsync(client, subject, ["mfa"]);
        await ProblemAsync(blocked.Callback, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [DbFact]
    public async Task Role_history_is_append_only()
    {
        await SeedStaffAsync(Guid.NewGuid().ToString(), StaffRoles.Author); // bảo đảm có dòng lịch sử
        await using var c = await _db.DataSource.OpenConnectionAsync();
        await using var tamper = new NpgsqlCommand("UPDATE tt.user_role_history SET reason = 'rewritten'", c);
        var error = await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        Assert.Equal("APPEND_ONLY", error.MessageText);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>
/// WBS-115 trên PostgreSQL: /admin/users* — RBAC (users.read / users.manage + MFA), ETag/If-Match, lý do bắt buộc,
/// lịch sử (0313 vai trò, 0314 trạng thái), thu hồi phiên khi đình chỉ, chống tự khóa, idempotency.
/// Người thao tác là principal staff giả (X-Test-User = id user thật trong DB để khóa ngoại actor_id hợp lệ).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdminUsersTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Cms() => factory.WithProbes(db.ConnectionString).Client(TestHost.Cms);

    private async Task<Guid> SeedUserAsync(string name, params string[] roles)
    {
        await using var c = await db.DataSource.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using var user = new NpgsqlCommand("INSERT INTO tt.users (display_name) VALUES (@n) RETURNING id", c, tx);
        user.Parameters.AddWithValue("n", name);
        var id = (Guid)(await user.ExecuteScalarAsync())!;
        foreach (var role in roles)
        {
            await using var grant = new NpgsqlCommand("INSERT INTO tt.user_roles (user_id, role, reason) VALUES (@u, @r, 'seed')", c, tx);
            grant.Parameters.AddWithValue("u", id);
            grant.Parameters.AddWithValue("r", role);
            await grant.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return id;
    }

    private static HttpRequestMessage As(HttpMethod method, string path, Guid actor, string roles, bool mfa = true, object? body = null, string? ifMatch = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestUserStartupFilter.Header, actor.ToString());
        request.Headers.Add(TestUserStartupFilter.RolesHeader, roles);
        if (mfa) request.Headers.Add(TestUserStartupFilter.MfaHeader, "true");
        if (method != HttpMethod.Get) request.Headers.Add("Origin", TestHost.CmsOrigin);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private static string NewKey() => "k-" + Guid.NewGuid().ToString("N");

    // ---------------------------------------------------------------- đọc

    [DbFact]
    public async Task Listing_requires_users_read_and_supports_filters_and_cursor()
    {
        var admin = await SeedUserAsync("Admin " + Guid.NewGuid().ToString("N")[..6], StaffRoles.Admin);
        var tag = Guid.NewGuid().ToString("N")[..10];
        var first = await SeedUserAsync($"Moderator A {tag}", StaffRoles.Moderator);
        var second = await SeedUserAsync($"Moderator B {tag}", StaffRoles.Moderator);
        using var client = Cms();

        using var editor = await client.SendAsync(As(HttpMethod.Get, "/api/v1/admin/users", admin, StaffRoles.Editor));
        await ProblemAsync(editor, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        // users.read không cần MFA.
        var page1 = await (await client.SendAsync(As(HttpMethod.Get, $"/api/v1/admin/users?q={tag}&limit=1", admin, StaffRoles.Admin, mfa: false)))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(second, page1.GetProperty("items")[0].GetProperty("id").GetGuid()); // mới nhất trước
        var cursor = page1.GetProperty("nextCursor").GetString()!;
        var page2 = await (await client.SendAsync(As(HttpMethod.Get, $"/api/v1/admin/users?q={tag}&limit=1&cursor={Uri.EscapeDataString(cursor)}", admin, StaffRoles.Admin)))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first, page2.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);

        // Cursor gắn với bộ lọc: dùng với bộ lọc khác → INVALID_CURSOR.
        using var otherFilter = await client.SendAsync(As(HttpMethod.Get, $"/api/v1/admin/users?q=other&cursor={Uri.EscapeDataString(cursor)}", admin, StaffRoles.Admin));
        await ProblemAsync(otherFilter, HttpStatusCode.BadRequest, ErrorCodes.InvalidCursor);

        var byRole = await (await client.SendAsync(As(HttpMethod.Get, $"/api/v1/admin/users?role=MODERATOR&q={tag}", admin, StaffRoles.Admin)))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, byRole.GetProperty("items").GetArrayLength());
        var byRoleNone = await (await client.SendAsync(As(HttpMethod.Get, $"/api/v1/admin/users?role=EDITOR&q={tag}", admin, StaffRoles.Admin)))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, byRoleNone.GetProperty("items").GetArrayLength());

        // Ký tự đại diện của LIKE trong q được coi là chữ thường.
        var wildcard = await (await client.SendAsync(As(HttpMethod.Get, "/api/v1/admin/users?q=%25", admin, StaffRoles.Admin)))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, wildcard.GetProperty("items").GetArrayLength());

        using var badStatus = await client.SendAsync(As(HttpMethod.Get, "/api/v1/admin/users?status=DELETED", admin, StaffRoles.Admin));
        await ProblemAsync(badStatus, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
        using var badRole = await client.SendAsync(As(HttpMethod.Get, "/api/v1/admin/users?role=ROOT", admin, StaffRoles.Admin));
        await ProblemAsync(badRole, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
    }

    [DbFact]
    public async Task Detail_returns_etag_and_404_for_unknown_user()
    {
        var admin = await SeedUserAsync("Admin", StaffRoles.Admin);
        var target = await SeedUserAsync("Target", StaffRoles.Author);
        using var client = Cms();
        using var detail = await client.SendAsync(As(HttpMethod.Get, $"/api/v1/admin/users/{target}", admin, StaffRoles.Admin));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"W/\"{body.GetProperty("rowVersion").GetInt64()}\"", detail.Headers.ETag?.ToString());
        Assert.Equal(["AUTHOR"], body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());

        using var missing = await client.SendAsync(As(HttpMethod.Get, $"/api/v1/admin/users/{Guid.NewGuid()}", admin, StaffRoles.Admin));
        await ProblemAsync(missing, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    // ---------------------------------------------------------------- đình chỉ / kích hoạt lại

    [DbFact]
    public async Task Suspend_and_reactivate_with_reason_audit_and_session_revocation()
    {
        var admin = await SeedUserAsync("Admin", StaffRoles.Admin);
        var target = await SeedUserAsync("Target", StaffRoles.Editor);
        await using (var c = await db.DataSource.OpenConnectionAsync())
        await using (var session = new NpgsqlCommand("""
            INSERT INTO tt.staff_sessions (id_hash, user_id, issuer, csrf_hash, mfa_satisfied, expires_at)
            VALUES (repeat('a', 63) || substr(md5(random()::text), 1, 1), @u, 'https://idp.test', repeat('0', 64), true, now() + interval '1 hour')
            """, c))
        {
            session.Parameters.AddWithValue("u", target);
            await session.ExecuteNonQueryAsync();
        }

        using var client = Cms();
        var path = $"/api/v1/admin/users/{target}";

        using var noMfa = await client.SendAsync(As(HttpMethod.Post, path + "/suspend", admin, StaffRoles.Admin, mfa: false, body: new { reason = "spam account" }, idempotencyKey: NewKey()));
        Assert.Equal("MFA required", (await ProblemAsync(noMfa, HttpStatusCode.Forbidden, ErrorCodes.Forbidden)).GetProperty("title").GetString());

        using var noKey = await client.SendAsync(As(HttpMethod.Post, path + "/suspend", admin, StaffRoles.Admin, body: new { reason = "spam account" }));
        await ProblemAsync(noKey, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);

        using var noReason = await client.SendAsync(As(HttpMethod.Post, path + "/suspend", admin, StaffRoles.Admin, body: new { reason = "  " }, idempotencyKey: NewKey()));
        await ProblemAsync(noReason, HttpStatusCode.UnprocessableEntity, ErrorCodes.ReasonRequired);
        using var shortReason = await client.SendAsync(As(HttpMethod.Post, path + "/suspend", admin, StaffRoles.Admin, body: new { reason = "bad" }, idempotencyKey: NewKey()));
        await ProblemAsync(shortReason, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);

        var key = NewKey();
        using var suspended = await client.SendAsync(As(HttpMethod.Post, path + "/suspend", admin, StaffRoles.Admin, body: new { reason = "spam account" }, idempotencyKey: key));
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        var suspendedBody = await suspended.Content.ReadAsStringAsync();
        Assert.Equal("SUSPENDED", JsonDocument.Parse(suspendedBody).RootElement.GetProperty("status").GetString());

        // Gửi lại cùng khóa → phát lại, không lỗi 409.
        using var replay = await client.SendAsync(As(HttpMethod.Post, path + "/suspend", admin, StaffRoles.Admin, body: new { reason = "spam account" }, idempotencyKey: key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(suspendedBody, await replay.Content.ReadAsStringAsync());

        using var again = await client.SendAsync(As(HttpMethod.Post, path + "/suspend", admin, StaffRoles.Admin, body: new { reason = "spam account" }, idempotencyKey: NewKey()));
        await ProblemAsync(again, HttpStatusCode.Conflict, ErrorCodes.StateConflict);

        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.staff_sessions WHERE user_id = '{target}' AND revoked_at IS NULL"));
        Assert.Equal("SUSPENDED", await db.ScalarAsync<string>($"SELECT revoked_reason FROM tt.staff_sessions WHERE user_id = '{target}'"));

        using var reactivated = await client.SendAsync(As(HttpMethod.Post, path + "/reactivate", admin, StaffRoles.Admin, body: new { reason = "appeal accepted" }, idempotencyKey: NewKey()));
        Assert.Equal("ACTIVE", (await reactivated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        Assert.Equal($"ACTIVE>SUSPENDED:spam account:{admin},SUSPENDED>ACTIVE:appeal accepted:{admin}", await db.ScalarAsync<string>(
            $"SELECT string_agg(from_status || '>' || to_status || ':' || reason || ':' || actor_id, ',' ORDER BY id) FROM tt.user_status_history WHERE user_id = '{target}'"));
    }

    [DbFact]
    public async Task Admin_cannot_suspend_self_and_unknown_user_is_404()
    {
        var admin = await SeedUserAsync("Admin", StaffRoles.Admin);
        using var client = Cms();
        using var self = await client.SendAsync(As(HttpMethod.Post, $"/api/v1/admin/users/{admin}/suspend", admin, StaffRoles.Admin, body: new { reason = "testing self" }, idempotencyKey: NewKey()));
        await ProblemAsync(self, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        using var missing = await client.SendAsync(As(HttpMethod.Post, $"/api/v1/admin/users/{Guid.NewGuid()}/suspend", admin, StaffRoles.Admin, body: new { reason = "testing missing" }, idempotencyKey: NewKey()));
        await ProblemAsync(missing, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [DbFact]
    public async Task Status_change_without_reason_is_rejected_by_the_database()
    {
        var target = await SeedUserAsync("Raw", StaffRoles.Author);
        await using var c = await db.DataSource.OpenConnectionAsync();
        await using var update = new NpgsqlCommand($"UPDATE tt.users SET status = 'SUSPENDED' WHERE id = '{target}'", c);
        var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("REASON_REQUIRED", error.MessageText);
    }

    // ---------------------------------------------------------------- vai trò

    [DbFact]
    public async Task Set_roles_with_if_match_reason_and_history()
    {
        var admin = await SeedUserAsync("Admin", StaffRoles.Admin);
        var target = await SeedUserAsync("Target", StaffRoles.Author, StaffRoles.Editor);
        using var client = Cms();
        var path = $"/api/v1/admin/users/{target}/roles";
        var body = new { roles = new[] { "EDITOR", "APPROVER" }, reason = "promoted to approver" };

        using var detail = await client.SendAsync(As(HttpMethod.Get, $"/api/v1/admin/users/{target}", admin, StaffRoles.Admin));
        var etag = detail.Headers.ETag!.ToString();

        using var noIfMatch = await client.SendAsync(As(HttpMethod.Put, path, admin, StaffRoles.Admin, body: body));
        await ProblemAsync(noIfMatch, HttpStatusCode.PreconditionRequired, ErrorCodes.PreconditionRequired);
        using var stale = await client.SendAsync(As(HttpMethod.Put, path, admin, StaffRoles.Admin, body: body, ifMatch: "W/\"999\""));
        await ProblemAsync(stale, HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
        using var noReason = await client.SendAsync(As(HttpMethod.Put, path, admin, StaffRoles.Admin, body: new { roles = new[] { "EDITOR" } }, ifMatch: etag));
        await ProblemAsync(noReason, HttpStatusCode.UnprocessableEntity, ErrorCodes.ReasonRequired);
        using var unknown = await client.SendAsync(As(HttpMethod.Put, path, admin, StaffRoles.Admin, body: new { roles = new[] { "ROOT" }, reason = "bad role here" }, ifMatch: etag));
        await ProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        using var missing = await client.SendAsync(As(HttpMethod.Put, $"/api/v1/admin/users/{Guid.NewGuid()}/roles", admin, StaffRoles.Admin, body: body));
        await ProblemAsync(missing, HttpStatusCode.NotFound, ErrorCodes.NotFound);

        using var ok = await client.SendAsync(As(HttpMethod.Put, path, admin, StaffRoles.Admin, body: body, ifMatch: etag));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["APPROVER", "EDITOR"], updated.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.NotEqual(etag, ok.Headers.ETag?.ToString());

        // ETag cũ không dùng lại được (lost update).
        using var reuse = await client.SendAsync(As(HttpMethod.Put, path, admin, StaffRoles.Admin, body: new { roles = new[] { "AUTHOR" }, reason = "second change" }, ifMatch: etag));
        await ProblemAsync(reuse, HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);

        Assert.Equal($"GRANT:AUTHOR:seed:,GRANT:EDITOR:seed:,REVOKE:AUTHOR:promoted to approver:{admin},GRANT:APPROVER:promoted to approver:{admin}",
            await db.ScalarAsync<string>(
                $"SELECT string_agg(action || ':' || role || ':' || reason || ':' || coalesce(actor_id::text, ''), ',' ORDER BY id) FROM tt.user_role_history WHERE user_id = '{target}'"));
    }

    [DbFact]
    public async Task Admin_cannot_remove_own_admin_role()
    {
        var admin = await SeedUserAsync("Admin", StaffRoles.Admin, StaffRoles.Editor);
        using var client = Cms();
        using var detail = await client.SendAsync(As(HttpMethod.Get, $"/api/v1/admin/users/{admin}", admin, StaffRoles.Admin));
        using var response = await client.SendAsync(As(HttpMethod.Put, $"/api/v1/admin/users/{admin}/roles", admin, StaffRoles.Admin,
            body: new { roles = new[] { "EDITOR" }, reason = "oops removing myself" }, ifMatch: detail.Headers.ETag!.ToString()));
        await ProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
    }
}

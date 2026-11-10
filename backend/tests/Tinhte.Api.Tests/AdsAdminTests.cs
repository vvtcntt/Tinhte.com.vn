using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Ads.Application;
using Tinhte.Platform.Errors;

namespace Tinhte.Api.Tests;

/// <summary>
/// Quản trị vị trí/đặt chỗ quảng cáo (FR-ADS-002, P0-ADS; DRAFT v0.3 mục 16/19) — phần không cần DB.
/// Quyền ads.manage/ads.approve + vai trò AD_OPS là ĐỀ XUẤT trong config/rbac/rbac.json (chờ BA/PO).
/// </summary>
public sealed class AdsAdminUnitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Guid User = Guid.NewGuid();

    [Theory]
    [InlineData("DRAFT", "APPROVED", true, true)]
    [InlineData("APPROVED", "ACTIVE", true, true)]
    [InlineData("ACTIVE", "PAUSED", true, false)]
    [InlineData("PAUSED", "ACTIVE", true, true)]       // mở lại cũng cần quyền duyệt
    [InlineData("APPROVED", "DRAFT", true, false)]
    [InlineData("PAUSED", "DRAFT", true, false)]
    [InlineData("ACTIVE", "EXPIRED", true, false)]
    [InlineData("DRAFT", "EXPIRED", true, false)]
    [InlineData("ACTIVE", "ACTIVE", true, false)]      // giữ nguyên (đổi ưu tiên) không cần duyệt lại
    [InlineData("DRAFT", "ACTIVE", false, true)]       // phải qua APPROVED
    [InlineData("ACTIVE", "DRAFT", false, false)]      // phải tạm dừng trước
    [InlineData("APPROVED", "PAUSED", false, false)]
    [InlineData("EXPIRED", "DRAFT", false, false)]     // EXPIRED là cuối
    [InlineData("EXPIRED", "ACTIVE", false, true)]
    public void Lifecycle_and_approval_rules(string from, string to, bool allowed, bool needsApproval)
    {
        Assert.Equal(allowed, AdStatuses.CanTransition(from, to));
        Assert.Equal(needsApproval, AdStatuses.NeedsApproval(from, to));
    }

    [Theory]
    [InlineData("https://acme.vn/khuyen-mai?utm=tinhte", true)]
    [InlineData("http://acme.vn", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("https://", false)]
    [InlineData("https://acme.vn/a b", false)]
    [InlineData("HTTPS://acme.vn", false)]                 // pattern ^https:// của hợp đồng
    public void Target_url_must_be_absolute_https(string url, bool ok) => Assert.Equal(ok, AdAdminService.IsSafeTargetUrl(url));

    [Fact]
    public async Task Admin_ad_endpoints_live_only_on_the_cms_site_and_check_permissions_first()
    {
        var app = factory.WithProbes();
        using var www = app.Client(TestHost.Www);
        using (var onWww = await www.GetAsync("/api/v1/admin/ad-placements"))
        {
            await NewsHttp.ProblemAsync(onWww, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        using var cms = app.Client(TestHost.Cms);
        using (var anonymous = await cms.GetAsync("/api/v1/admin/ad-placements"))
        {
            await NewsHttp.ProblemAsync(anonymous, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        }

        using (var editor = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/ad-placements", User, "EDITOR")))
        {
            await NewsHttp.ProblemAsync(editor, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }

        // siteKey sai được kiểm trước khi chạm DB → 400; hợp lệ thì cần DB (ở đây không có) → 503.
        foreach (var bad in new[] { "cms", "media", "khong-co", "Bad_Key" })
        {
            using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/ad-placements?siteKey={bad}", User, "AD_OPS"));
            await NewsHttp.ProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
        }

        using var noDb = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/ad-placements?siteKey=www", User, "AD_OPS"));
        await NewsHttp.ProblemAsync(noDb, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
    }

    [Fact]
    public async Task Writes_need_idempotency_key_and_valid_bodies()
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        using (var noKey = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/ad-placements", User, "AD_OPS",
            json: """{"siteKey":"www","code":"home_top","label":"Quảng cáo","sizes":[{"breakpoint":"MOBILE","width":320,"height":100}]}""")))
        {
            await NewsHttp.ProblemAsync(noKey, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
        }

        // PUT không có Idempotency-Key: body sai hợp đồng → 422 trước khi chạm DB.
        var body = await NewsHttp.ProblemAsync(await cms.SendAsync(NewsHttp.Admin(HttpMethod.Put, $"/api/v1/admin/ad-bookings/{Guid.NewGuid()}", User, "AD_OPS",
            json: """{"status":"LIVE","priority":101,"creatives":[{"breakpoint":"WATCH","width":0,"height":90,"altText":""}]}""", ifMatch: "W/\"1\"")),
            HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        var fields = body.GetProperty("errors").EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.Contains(fields, f => f.EndsWith("status", StringComparison.Ordinal));
        Assert.Contains(fields, f => f.EndsWith("priority", StringComparison.Ordinal));
        Assert.Contains(fields, f => f.Contains("breakpoint", StringComparison.Ordinal));
        Assert.Contains(fields, f => f.Contains("mediaassetid", StringComparison.Ordinal));
    }
}

/// <summary>
/// Quản trị quảng cáo trên PostgreSQL thật: AD_OPS chuẩn bị, APPROVER (MFA) duyệt/mở, quy tắc vòng đời, banner theo kích thước của vị trí,
/// If-Match, idempotency, và kết quả phục vụ ở GET /ads/placements của site www.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdsAdminDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AdOps = "AD_OPS";
    private const string Approver = "APPROVER";
    private readonly Guid _ops = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly Guid _image = Guid.NewGuid();
    private readonly Guid _privateImage = Guid.NewGuid();
    private readonly Guid _video = Guid.NewGuid();
    private readonly string _code = "adm-" + Guid.NewGuid().ToString("N")[..12];

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private Task SeedAsync() => ExecAsync($"""
        INSERT INTO tt.users (id, display_name) VALUES ('{_ops}', 'Vận hành quảng cáo'), ('{_approver}', 'Người duyệt');
        INSERT INTO tt.media_assets (id, status, media_type, visibility, rights_status, storage_key) VALUES
          ('{_image}', 'READY', 'IMAGE', 'PUBLIC', 'CLEARED', 'ads/{_code}/banner.png'),
          ('{_privateImage}', 'READY', 'IMAGE', 'PRIVATE', 'UNVERIFIED', 'ads/{_code}/private.png'),
          ('{_video}', 'READY', 'VIDEO', 'PUBLIC', 'CLEARED', 'videos/{_code}.mp4');
        """);

    private Task SetFlagAsync(bool on) => ExecAsync(on
        ? $"UPDATE tt.feature_gates SET enabled = true, approval_reference = 'TEST-ADS-ADMIN', approved_by = '{_approver}', approved_at = now() WHERE key = 'ads.display_slots'"
        : "UPDATE tt.feature_gates SET enabled = false WHERE key = 'ads.display_slots'");

    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task<(JsonElement Body, string ETag)> OkAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        using (response)
        {
            var body = await NewsHttp.JsonAsync(response, status);
            return (body, response.Headers.ETag?.ToString() ?? "");
        }
    }

    private static async Task<JsonElement> FailAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response) return await NewsHttp.ProblemAsync(response, status, code);
    }

    private HttpRequestMessage Ops(HttpMethod method, string path, object? body = null, string? ifMatch = null, string? key = null) =>
        NewsHttp.Admin(method, path, _ops, AdOps, json: body is null ? null : Json(body), ifMatch: ifMatch,
            idempotencyKey: key ?? (method == HttpMethod.Post ? Guid.NewGuid().ToString() : null));

    private HttpRequestMessage Approve(HttpMethod method, string path, object body, string? ifMatch, bool mfa = true) =>
        NewsHttp.Admin(method, path, _approver, Approver, mfa, Json(body), ifMatch);

    private object Creative(string bp, int w, int h, Guid? media = null, string alt = "Banner ACME") =>
        new { breakpoint = bp, width = w, height = h, mediaAssetId = media ?? _image, altText = alt };

    [DbFact]
    public async Task Ad_ops_prepare_approver_opens_and_the_public_slot_follows()
    {
        await SeedAsync();
        var app = factory.WithProbes(db.ConnectionString);
        using var cms = app.Client(TestHost.Cms);
        using var www = app.Client(TestHost.Www);

        try
        {
            // ---------------------------------------------------------------- vị trí
            var sizes = new[]
            {
                new { breakpoint = "DESKTOP", width = 970, height = 250 }, new { breakpoint = "MOBILE", width = 320, height = 100 },
                new { breakpoint = "DESKTOP", width = 970, height = 90 },
            };
            var create = new { siteKey = "www", code = _code, label = "Quảng cáo", sizes };
            HttpResponseMessage created = await cms.SendAsync(Ops(HttpMethod.Post, "/api/v1/admin/ad-placements", create));
            var location = created.Headers.Location?.OriginalString;
            var (placement, etag) = await OkAsync(created, HttpStatusCode.Created);
            var placementId = placement.GetProperty("id").GetGuid();
            Assert.EndsWith($"/api/v1/admin/ad-placements/{placementId}", location, StringComparison.Ordinal);
            Assert.Equal("W/\"1\"", etag);
            Assert.Equal("DRAFT", placement.GetProperty("status").GetString());
            Assert.Equal("www", placement.GetProperty("siteKey").GetString());
            // Kích thước sắp theo breakpoint (nhỏ trước) rồi theo kích thước.
            Assert.Equal(["MOBILE 320x100", "DESKTOP 970x90", "DESKTOP 970x250"], placement.GetProperty("sizes").EnumerateArray()
                .Select(s => $"{s.GetProperty("breakpoint").GetString()} {s.GetProperty("width").GetInt32()}x{s.GetProperty("height").GetInt32()}"));
            Assert.Equal(JsonValueKind.Null, placement.GetProperty("approvedBy").ValueKind);

            var duplicate = await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, "/api/v1/admin/ad-placements", create)),
                HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
            Assert.True(duplicate.GetProperty("errors").TryGetProperty("code", out _));
            var onCms = await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, "/api/v1/admin/ad-placements", create with { siteKey = "cms" })),
                HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
            Assert.True(onCms.GetProperty("errors").TryGetProperty("siteKey", out _));
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, "/api/v1/admin/ad-placements",
                create with { code = _code + "-2", sizes = [sizes[0], sizes[0]] })), HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);

            var (list, _) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Get, "/api/v1/admin/ad-placements?siteKey=www")));
            Assert.Contains(list.GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == placementId);
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Get, $"/api/v1/admin/ad-placements/{Guid.NewGuid()}")), HttpStatusCode.NotFound, ErrorCodes.NotFound);

            // Mở vị trí: AD_OPS không có quyền duyệt; APPROVER cần MFA; If-Match bắt buộc và phải khớp.
            var path = $"/api/v1/admin/ad-placements/{placementId}";
            var approve = new { code = _code, label = "Quảng cáo đầu trang", status = "APPROVED", sizes };
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Put, path, approve, etag)), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
            await FailAsync(await cms.SendAsync(Approve(HttpMethod.Put, path, approve, etag, mfa: false)), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
            await FailAsync(await cms.SendAsync(Approve(HttpMethod.Put, path, approve, null)), HttpStatusCode.PreconditionRequired, ErrorCodes.PreconditionRequired);
            await FailAsync(await cms.SendAsync(Approve(HttpMethod.Put, path, approve, "W/\"9\"")), HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
            (placement, etag) = await OkAsync(await cms.SendAsync(Approve(HttpMethod.Put, path, approve, etag)));
            Assert.Equal("APPROVED", placement.GetProperty("status").GetString());
            Assert.Equal(_approver, placement.GetProperty("approvedBy").GetGuid());
            Assert.Equal("Quảng cáo đầu trang", placement.GetProperty("label").GetString());
            Assert.Equal("W/\"2\"", etag);

            // APPROVED → PAUSED không có trong vòng đời.
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Put, path, approve with { status = "PAUSED" }, etag)), HttpStatusCode.Conflict, ErrorCodes.StateConflict);
            (placement, etag) = await OkAsync(await cms.SendAsync(Approve(HttpMethod.Put, path, approve with { status = "ACTIVE" }, etag)));
            Assert.Equal(_approver, placement.GetProperty("approvedBy").GetGuid());

            // ---------------------------------------------------------------- đặt chỗ
            var bookings = $"{path}/bookings";
            var booking = new
            {
                priority = 10, advertiserName = "ACME", targetUrl = "https://acme.vn/khuyen-mai", startsAt = (DateTimeOffset?)null, endsAt = (DateTimeOffset?)null,
                creatives = new[] { Creative("DESKTOP", 970, 250), Creative("MOBILE", 320, 100, alt: "Banner ACME mobile") },
            };
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, bookings, booking with { creatives = [Creative("TABLET", 728, 90)] })),
                HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
            var wrongMedia = await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, bookings, booking with { creatives = [Creative("DESKTOP", 970, 90, _video)] })),
                HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
            Assert.True(wrongMedia.GetProperty("errors").TryGetProperty("creatives[0].mediaAssetId", out _));
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, bookings, booking with { creatives = [Creative("DESKTOP", 970, 90), Creative("DESKTOP", 970, 250)] })),
                HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, bookings, booking with { targetUrl = "javascript:alert(1)" })),
                HttpStatusCode.UnprocessableEntity, ErrorCodes.UnsafeUrl);
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, bookings, booking with { startsAt = DateTimeOffset.UtcNow, endsAt = DateTimeOffset.UtcNow.AddDays(-1) })),
                HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, $"/api/v1/admin/ad-placements/{Guid.NewGuid()}/bookings", booking)),
                HttpStatusCode.NotFound, ErrorCodes.NotFound);

            var key = Guid.NewGuid().ToString();
            var (draft, bookingEtag) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Post, bookings, booking, key: key)), HttpStatusCode.Created);
            using (var replay = await cms.SendAsync(Ops(HttpMethod.Post, bookings, booking, key: key)))
            {
                Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
                Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
            }

            var bookingId = draft.GetProperty("id").GetGuid();
            Assert.Equal("DRAFT", draft.GetProperty("status").GetString());
            var creatives = draft.GetProperty("creatives").EnumerateArray().ToList();
            Assert.Equal(["MOBILE", "DESKTOP"], creatives.Select(c => c.GetProperty("breakpoint").GetString()));
            Assert.EndsWith($"/ads/{_code}/banner.png", creatives[0].GetProperty("creativeUrl").GetString(), StringComparison.Ordinal);
            var (queue, _) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Get, bookings)));
            Assert.Equal(bookingId, Assert.Single(queue.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

            var bookingPath = $"/api/v1/admin/ad-bookings/{bookingId}";
            object Update(string status, int priority = 10, string target = "https://acme.vn/khuyen-mai", object[]? items = null) =>
                new { status, priority, advertiserName = "ACME", targetUrl = target, startsAt = (DateTimeOffset?)null, endsAt = (DateTimeOffset?)null, creatives = items ?? booking.creatives };

            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Put, bookingPath, Update("APPROVED"), bookingEtag)), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
            (draft, bookingEtag) = await OkAsync(await cms.SendAsync(Approve(HttpMethod.Put, bookingPath, Update("APPROVED"), bookingEtag)));
            Assert.Equal(_approver, draft.GetProperty("approvedBy").GetGuid());

            // Đã duyệt: đổi link → 409 (phải về DRAFT trước); chỉ đổi ưu tiên → được.
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Put, bookingPath, Update("APPROVED", target: "https://acme.vn/khac"), bookingEtag)),
                HttpStatusCode.Conflict, ErrorCodes.StateConflict);
            (draft, bookingEtag) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Put, bookingPath, Update("APPROVED", priority: 20), bookingEtag)));
            Assert.Equal(20, draft.GetProperty("priority").GetInt32());

            // Kích thước đang có banner dùng không gỡ được; kích thước chưa dùng thì gỡ được.
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Put, path, approve with { status = "ACTIVE", sizes = [sizes[1], sizes[2]] }, etag)),
                HttpStatusCode.Conflict, ErrorCodes.StateConflict);
            (placement, etag) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Put, path, approve with { status = "ACTIVE", sizes = [sizes[0], sizes[1]] }, etag)));
            Assert.Equal(2, placement.GetProperty("sizes").GetArrayLength());

            // Mở đặt chỗ + bật cờ → slot công khai có 2 banner; AD_OPS tạm dừng (không cần duyệt) → slot biến mất.
            (draft, bookingEtag) = await OkAsync(await cms.SendAsync(Approve(HttpMethod.Put, bookingPath, Update("ACTIVE", priority: 20), bookingEtag)));
            await SetFlagAsync(true);
            var (slots, _) = await OkAsync(await www.GetAsync($"/api/v1/ads/placements?placementCodes={_code}"));
            var slot = Assert.Single(slots.GetProperty("items").EnumerateArray());
            Assert.Equal("Quảng cáo đầu trang", slot.GetProperty("label").GetString());
            Assert.Equal(2, slot.GetProperty("creatives").GetArrayLength());

            (draft, bookingEtag) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Put, bookingPath, Update("PAUSED", priority: 20), bookingEtag)));
            (slots, _) = await OkAsync(await www.GetAsync($"/api/v1/ads/placements?placementCodes={_code}"));
            Assert.Empty(slots.GetProperty("items").EnumerateArray());
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Put, bookingPath, Update("ACTIVE", priority: 20), bookingEtag)), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

            // Về DRAFT: xóa người duyệt, sửa banner được (media riêng tư → creativeUrl null); APPROVED cần link.
            (draft, bookingEtag) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Put, bookingPath, Update("DRAFT", priority: 20), bookingEtag)));
            Assert.Equal(JsonValueKind.Null, draft.GetProperty("approvedBy").ValueKind);
            (draft, bookingEtag) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Put, bookingPath,
                Update("DRAFT", priority: 20, items: [Creative("MOBILE", 320, 100, _privateImage, "Bản mới")]), bookingEtag)));
            var edited = Assert.Single(draft.GetProperty("creatives").EnumerateArray());
            Assert.Equal("Bản mới", edited.GetProperty("altText").GetString());
            Assert.Equal(JsonValueKind.Null, edited.GetProperty("creativeUrl").ValueKind);
            await FailAsync(await cms.SendAsync(Approve(HttpMethod.Put, bookingPath,
                new { status = "APPROVED", priority = 20, creatives = new[] { Creative("MOBILE", 320, 100, _privateImage, "Bản mới") } }, bookingEtag)),
                HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);

            // EXPIRED là cuối; vị trí EXPIRED không nhận đặt chỗ mới.
            (draft, bookingEtag) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Put, bookingPath,
                Update("EXPIRED", priority: 20, items: [Creative("MOBILE", 320, 100, _privateImage, "Bản mới")]), bookingEtag)));
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Put, bookingPath,
                Update("DRAFT", priority: 20, items: [Creative("MOBILE", 320, 100, _privateImage, "Bản mới")]), bookingEtag)), HttpStatusCode.Conflict, ErrorCodes.StateConflict);
            (placement, etag) = await OkAsync(await cms.SendAsync(Ops(HttpMethod.Put, path, approve with { status = "EXPIRED", sizes = [sizes[0], sizes[1]] }, etag)));
            await FailAsync(await cms.SendAsync(Ops(HttpMethod.Post, bookings, booking)), HttpStatusCode.Conflict, ErrorCodes.StateConflict);

            Assert.Equal(_ops, await db.ScalarAsync<Guid>($"SELECT updated_by FROM tt.ad_bookings WHERE id = '{bookingId}'"));
            Assert.Equal(_ops, await db.ScalarAsync<Guid>($"SELECT created_by FROM tt.ad_placements WHERE id = '{placementId}'"));
        }
        finally
        {
            await SetFlagAsync(false);
        }
    }
}

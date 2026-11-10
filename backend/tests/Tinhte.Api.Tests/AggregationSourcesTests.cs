using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>Phân giải DNS giả cho test: chỉ các tên được khai báo mới có địa chỉ.</summary>
internal sealed class FakeFeedHostResolver(Dictionary<string, string[]> hosts) : IFeedHostResolver
{
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IPAddress>>(hosts.TryGetValue(host, out var ips) ? ips.Select(IPAddress.Parse).ToList() : []);
}

/// <summary>Radar tin GĐ1 — quản lý nguồn/feed (FR-AGG-001/002, ADR-0008): phần không cần DB.</summary>
public sealed class AggregationUnitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static string Code(Action action) => Assert.Throws<DomainProblemException>(action).Code;

    [Theory]
    [InlineData("http://news.example.org/rss")]          // chỉ https
    [InlineData("ftp://news.example.org/rss")]
    [InlineData("https://user:pass@news.example.org/rss")]
    [InlineData("https://news.example.org:8443/rss")]    // chỉ cổng 443
    [InlineData("https://127.0.0.1/rss")]
    [InlineData("https://127.1/rss")]
    [InlineData("https://10.1.2.3/rss")]
    [InlineData("https://172.20.0.1/rss")]
    [InlineData("https://192.168.1.1/rss")]
    [InlineData("https://169.254.169.254/latest/meta-data")]   // metadata đám mây
    [InlineData("https://100.100.100.200/rss")]               // CGNAT (metadata Alibaba)
    [InlineData("https://0.0.0.0/rss")]
    [InlineData("https://[::1]/rss")]
    [InlineData("https://[fd00:ec2::254]/rss")]               // ULA (metadata AWS IPv6)
    [InlineData("https://[fe80::1]/rss")]
    [InlineData("https://[::ffff:127.0.0.1]/rss")]            // IPv4-mapped
    [InlineData("https://localhost/rss")]
    [InlineData("https://feeds.localhost/rss")]
    [InlineData("https://printer.local/rss")]
    [InlineData("https://metadata.google.internal/rss")]
    [InlineData("https://intranet/rss")]                      // tên một nhãn
    public void Unsafe_feed_urls_are_rejected(string url) =>
        Assert.Equal(ErrorCodes.UnsafeUrl, Code(() => FeedUrlPolicy.Parse("url", url)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("không phải url")]
    [InlineData("/rss")]
    public void Malformed_feed_urls_are_validation_failures(string? url) =>
        Assert.Equal(ErrorCodes.ValidationFailed, Code(() => FeedUrlPolicy.Parse("url", url)));

    [Fact]
    public void Feed_url_length_is_capped() =>
        Assert.Equal(ErrorCodes.ValidationFailed, Code(() => FeedUrlPolicy.Parse("url", "https://news.example.org/" + new string('a', 2048))));

    [Theory]
    [InlineData("93.184.215.14", false)]
    [InlineData("2606:4700::1111", false)]
    [InlineData("203.0.113.9", true)]      // tài liệu
    [InlineData("224.0.0.1", true)]        // multicast
    [InlineData("255.255.255.255", true)]
    [InlineData("2002:0a00:0001::1", true)]   // 6to4 nhúng 10.0.0.1
    [InlineData("2002:5db8:d70e::1", false)]  // 6to4 nhúng IP công khai
    [InlineData("ff02::1", true)]
    public void Address_classification(string ip, bool blocked) => Assert.Equal(blocked, FeedUrlPolicy.IsBlocked(IPAddress.Parse(ip)));

    [Fact]
    public async Task Dns_answers_are_checked_but_unresolvable_names_are_accepted()
    {
        var policy = new FeedUrlPolicy(new FakeFeedHostResolver(new Dictionary<string, string[]>
        {
            ["rebind.example.org"] = ["93.184.215.14", "10.0.0.7"],
            ["news.example.org"] = ["93.184.215.14"],
        }));
        var e = await Assert.ThrowsAsync<DomainProblemException>(() => policy.CheckAsync("url", "https://rebind.example.org/rss", CancellationToken.None));
        Assert.Equal(ErrorCodes.UnsafeUrl, e.Code);
        Assert.Equal("https://news.example.org/rss", await policy.CheckAsync("url", "https://News.Example.org/rss", CancellationToken.None));
        Assert.Equal("https://chua-co-dns.example.org/feed", await policy.CheckAsync("url", "https://chua-co-dns.example.org/feed", CancellationToken.None));
    }

    private static IDictionary<string, string[]> SourcePatchErrors(string json) =>
        Assert.Throws<DomainProblemException>(() => AggregationSourcePatch.Parse(JsonDocument.Parse(json).RootElement)).Errors!;

    [Fact]
    public void Source_patch_rejects_usage_mode_licence_and_unknown_fields()
    {
        Assert.Contains("/approval", SourcePatchErrors("""{"usageMode":"M2"}""")["usageMode"][0], StringComparison.Ordinal);
        Assert.Contains("/approval", SourcePatchErrors("""{"licenseId":"00000000-0000-0000-0000-000000000001"}""")["licenseId"][0], StringComparison.Ordinal);
        Assert.True(SourcePatchErrors("""{"status":"ENABLED"}""").ContainsKey("status"));
        Assert.True(SourcePatchErrors("{}").ContainsKey("$"));
        Assert.True(SourcePatchErrors("""{"trustWeight":6}""").ContainsKey("trustWeight"));
        Assert.True(SourcePatchErrors("""{"language":"vie"}""").ContainsKey("language"));
        Assert.True(SourcePatchErrors("""{"siteKeys":["www","www"]}""").ContainsKey("siteKeys"));
        Assert.True(SourcePatchErrors("""{"siteKeys":[]}""").ContainsKey("siteKeys"));

        var patch = AggregationSourcePatch.Parse(JsonDocument.Parse("""{"trustWeight":5,"siteKeys":["www"]}""").RootElement);
        Assert.Equal(5, patch.TrustWeight);
        Assert.Null(patch.Language);
        Assert.Equal(["www"], patch.SiteKeys!);
    }

    [Fact]
    public void Feed_patch_validates_each_field()
    {
        IDictionary<string, string[]> Errors(string json) =>
            Assert.Throws<DomainProblemException>(() => AggregationFeedPatch.Parse(JsonDocument.Parse(json).RootElement)).Errors!;
        Assert.True(Errors("""{"scheduleMinutes":4}""").ContainsKey("scheduleMinutes"));
        Assert.True(Errors("""{"format":"XML"}""").ContainsKey("format"));
        Assert.True(Errors("""{"isEnabled":"yes"}""").ContainsKey("isEnabled"));
        Assert.True(Errors("""{"categoryMapping":[{"feedCategory":"","categoryId":"x"}]}""").ContainsKey("categoryMapping[0]"));
        Assert.True(Errors("""{"health":"OK"}""").ContainsKey("health"));

        var patch = AggregationFeedPatch.Parse(JsonDocument.Parse("""{"isEnabled":false,"scheduleMinutes":30}""").RootElement);
        Assert.False(patch.IsEnabled);
        Assert.Equal(30, patch.ScheduleMinutes);
        Assert.Null(patch.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(" abc ")]
    public void Short_reason_is_reason_required(string? reason) =>
        Assert.Equal(ErrorCodes.ReasonRequired, Code(() => AggregationLimits.CheckReason(reason)));

    [Theory]
    [InlineData("GET", "", StaffRoles.Author, false)]                    // AUTHOR không có aggregation.read
    [InlineData("POST", "", StaffRoles.Approver, true)]                  // APPROVER không quản lý nguồn
    [InlineData("POST", "/{id}/approval", StaffRoles.Editor, true)]      // EDITOR không duyệt chế độ khai thác
    [InlineData("POST", "/{id}/approval", StaffRoles.Approver, false)]   // aggregation.sources_approve cần MFA
    [InlineData("POST", "/{id}/pause", StaffRoles.Moderator, true)]
    [InlineData("POST", "/{id}/feeds", StaffRoles.Author, false)]
    public async Task Permission_is_checked_before_touching_data(string method, string path, string roles, bool mfa)
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        var url = "/api/v1/admin/aggregation/sources" + path.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
        using var response = await cms.SendAsync(NewsHttp.Admin(new HttpMethod(method), url, Guid.NewGuid(), roles, mfa,
            json: """{"usageMode":"M1","termsEvidenceRef":"dms://t","reason":"Lý do đủ dài","url":"https://news.example.org/rss"}""",
            ifMatch: "W/\"1\"", idempotencyKey: "perm-" + Guid.NewGuid()));
        await NewsHttp.ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Admin_routes_are_not_served_on_the_public_site()
    {
        using var www = factory.WithProbes().Client(TestHost.Www);
        using var response = await www.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/aggregation/sources", Guid.NewGuid(), StaffRoles.Editor));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// Radar tin GĐ1 trên PostgreSQL: hồ sơ nguồn (tạo/đọc/sửa/danh sách), duyệt chế độ khai thác (MFA, SoD, giấy phép), tạm dừng/tiếp tục/ngừng,
/// feed (SSRF, trùng URL, ánh xạ chuyên mục, gửi lại duyệt), cờ aggregation.radar.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AggregationDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Base = "/api/v1/admin/aggregation";
    private const string EditorApprover = $"{StaffRoles.Editor},{StaffRoles.Approver}";
    private readonly Guid _editor = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly Guid _licence = Guid.NewGuid();
    private readonly Guid _expiredLicence = Guid.NewGuid();
    private readonly Guid _category = Guid.NewGuid();
    private readonly Guid _disabledCategory = Guid.NewGuid();
    private WebApplicationFactory<Program>? _app;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) return;
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await ExecAsync($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_editor}', 'Biên tập'), ('{_approver}', 'Người duyệt');
            INSERT INTO tt.licenses (id, code, name, usage_scope, evidence_ref, valid_from, valid_to) VALUES
              ('{_licence}', 'LIC-{suffix}', 'Thỏa thuận trích dẫn', 'Dùng dữ kiện, ghi nguồn', 'dms://lic', now() - interval '1 day', now() + interval '1 year'),
              ('{_expiredLicence}', 'OLD-{suffix}', 'Thỏa thuận cũ', 'Hết hạn', 'dms://old', now() - interval '2 years', now() - interval '1 day');
            INSERT INTO tt.categories (id, slug, name) VALUES ('{_category}', 'agg-{suffix.ToLowerInvariant()}', 'Điện thoại');
            INSERT INTO tt.categories (id, slug, name, is_enabled) VALUES ('{_disabledCategory}', 'agg-off-{suffix.ToLowerInvariant()}', 'Đã tắt', false);
            """);
        await SetFlagAsync(true);
        _app = factory.WithProbes(db.ConnectionString).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IFeedHostResolver>(new FakeFeedHostResolver(new Dictionary<string, string[]>
            {
                ["news.example.org"] = ["93.184.215.14"],
                ["internal-feed.example.org"] = ["10.20.30.40"],
            }))));
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private HttpClient Cms() => _app!.Client(TestHost.Cms);

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Bật cờ cần hồ sơ duyệt (CHECK của 0300) — chỉ trong DB test.</summary>
    private Task SetFlagAsync(bool on) => ExecAsync(on
        ? $"UPDATE tt.feature_gates SET enabled = true, approval_reference = 'TEST-AGG', approved_by = '{_approver}', approved_at = now() WHERE key = 'aggregation.radar'"
        : "UPDATE tt.feature_gates SET enabled = false WHERE key = 'aggregation.radar'");

    private async Task<Guid> CatalogueSourceAsync(bool enabled = true)
    {
        var id = Guid.NewGuid();
        await ExecAsync($"INSERT INTO tt.content_sources (id, name, kind, url, is_enabled) VALUES ('{id}', 'Nguồn {id:N}', 'AGENCY', 'https://news.example.org', {enabled})");
        return id;
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, Guid user, string roles = StaffRoles.Editor, bool mfa = false, string? json = null,
        string? ifMatch = null, bool idempotent = false) =>
        NewsHttp.Admin(method, Base + path, user, roles, mfa, json, ifMatch, idempotent ? "agg-" + Guid.NewGuid() : null);

    private async Task<(JsonElement Body, string ETag)> CreateProfileAsync(HttpClient cms, Guid sourceId, string sites = """["www"]""")
    {
        using var response = await cms.SendAsync(Req(HttpMethod.Post, "/sources", _editor,
            json: $$"""{"contentSourceId":"{{sourceId}}","language":"vi","siteKeys":{{sites}}}""", idempotent: true));
        var body = await NewsHttp.JsonAsync(response, HttpStatusCode.Created);
        return (body, response.Headers.ETag!.ToString());
    }

    private async Task<(JsonElement Body, string ETag)> SendAsync(HttpClient cms, HttpRequestMessage request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await cms.SendAsync(request);
        var body = await NewsHttp.JsonAsync(response, expected);
        return (body, response.Headers.ETag?.ToString() ?? "");
    }

    private Task<(JsonElement Body, string ETag)> ApproveAsync(HttpClient cms, Guid sourceId, string etag, string mode = "M1", Guid? licence = null,
        Guid? by = null, string roles = StaffRoles.Approver) =>
        SendAsync(cms, Req(HttpMethod.Post, $"/sources/{sourceId}/approval", by ?? _approver, roles, mfa: true,
            json: $$"""{"usageMode":"{{mode}}","licenseId":{{(licence is { } l ? $"\"{l}\"" : "null")}},"termsEvidenceRef":"dms://terms/{{sourceId:N}}","note":"Đã đọc điều khoản"}""",
            ifMatch: etag, idempotent: true));

    // ---------------------------------------------------------------- cờ

    [DbFact]
    public async Task Flag_off_blocks_every_operation()
    {
        using var cms = Cms();
        var sourceId = await CatalogueSourceAsync();
        await SetFlagAsync(false);
        try
        {
            using var list = await cms.SendAsync(Req(HttpMethod.Get, "/sources", _editor));
            await NewsHttp.ProblemAsync(list, HttpStatusCode.Forbidden, ErrorCodes.FeatureDisabled);
            using var create = await cms.SendAsync(Req(HttpMethod.Post, "/sources", _editor,
                json: $$"""{"contentSourceId":"{{sourceId}}","language":"vi","siteKeys":["www"]}""", idempotent: true));
            await NewsHttp.ProblemAsync(create, HttpStatusCode.Forbidden, ErrorCodes.FeatureDisabled);
            Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_source_profiles WHERE content_source_id = '{sourceId}'"));
        }
        finally
        {
            await SetFlagAsync(true);
        }
    }

    // ---------------------------------------------------------------- hồ sơ nguồn

    [DbFact]
    public async Task Profile_is_created_proposed_m0_then_listed_and_read()
    {
        using var cms = Cms();
        var sourceId = await CatalogueSourceAsync();
        using var response = await cms.SendAsync(Req(HttpMethod.Post, "/sources", _editor,
            json: $$"""{"contentSourceId":"{{sourceId}}","trustWeight":4,"language":"vi","siteKeys":["www"]}""", idempotent: true));
        var created = await NewsHttp.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal($"W/\"1\"", response.Headers.ETag!.ToString());
        Assert.EndsWith($"/api/v1/admin/aggregation/sources/{sourceId}", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(sourceId, created.GetProperty("id").GetGuid());
        Assert.Equal($"Nguồn {sourceId:N}", created.GetProperty("name").GetString());
        Assert.Equal("PROPOSED", created.GetProperty("status").GetString());
        Assert.Equal("M0", created.GetProperty("usageMode").GetString());
        Assert.Equal(4, created.GetProperty("trustWeight").GetInt32());
        Assert.Equal(_editor, created.GetProperty("proposedBy").GetGuid());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("approvedAt").ValueKind);
        Assert.Equal(0, created.GetProperty("feeds").GetArrayLength());
        Assert.Equal(0, created.GetProperty("approvals").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("worstFeedHealth").ValueKind);

        var (read, etag) = await SendAsync(cms, Req(HttpMethod.Get, $"/sources/{sourceId}", _editor));
        Assert.Equal("W/\"1\"", etag);
        Assert.Equal("vi", read.GetProperty("language").GetString());

        var (list, _) = await SendAsync(cms, Req(HttpMethod.Get, $"/sources?q={sourceId:N}&status=PROPOSED", _editor));
        Assert.Equal(sourceId, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var (none, _) = await SendAsync(cms, Req(HttpMethod.Get, $"/sources?q={sourceId:N}&status=ENABLED,PAUSED", _editor));
        Assert.Empty(none.GetProperty("items").EnumerateArray());
        using var bad = await cms.SendAsync(Req(HttpMethod.Get, "/sources?status=LIVE", _editor));
        await NewsHttp.ProblemAsync(bad, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
    }

    [DbFact]
    public async Task List_pages_with_a_scoped_cursor()
    {
        using var cms = Cms();
        var tag = Guid.NewGuid().ToString("N")[..10];
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid();
            await ExecAsync($"INSERT INTO tt.content_sources (id, name) VALUES ('{id}', 'Trang {tag} {i}')");
            await CreateProfileAsync(cms, id);
        }

        var (first, _) = await SendAsync(cms, Req(HttpMethod.Get, $"/sources?q={tag}&limit=2", _editor));
        Assert.Equal([$"Trang {tag} 0", $"Trang {tag} 1"], first.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var (second, _) = await SendAsync(cms, Req(HttpMethod.Get, $"/sources?q={tag}&limit=2&cursor={Uri.EscapeDataString(cursor)}", _editor));
        Assert.Equal($"Trang {tag} 2", Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        // Cursor gắn với bộ lọc: đổi q → INVALID_CURSOR.
        using var other = await cms.SendAsync(Req(HttpMethod.Get, $"/sources?q=khac&limit=2&cursor={Uri.EscapeDataString(cursor)}", _editor));
        await NewsHttp.ProblemAsync(other, HttpStatusCode.BadRequest, ErrorCodes.InvalidCursor);
    }

    [DbFact]
    public async Task Create_rejects_duplicates_unknown_sources_and_non_content_sites()
    {
        using var cms = Cms();
        var sourceId = await CatalogueSourceAsync();
        var key = "agg-replay-" + Guid.NewGuid();
        var json = $$"""{"contentSourceId":"{{sourceId}}","language":"vi","siteKeys":["www"]}""";
        using (var first = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, Base + "/sources", _editor, StaffRoles.Editor, json: json, idempotencyKey: key)))
        {
            await NewsHttp.JsonAsync(first, HttpStatusCode.Created);
        }

        using (var replay = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, Base + "/sources", _editor, StaffRoles.Editor, json: json, idempotencyKey: key)))
        {
            await NewsHttp.JsonAsync(replay, HttpStatusCode.Created);
            Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        }

        using var duplicate = await cms.SendAsync(Req(HttpMethod.Post, "/sources", _editor, json: json, idempotent: true));
        await NewsHttp.ProblemAsync(duplicate, HttpStatusCode.Conflict, ErrorCodes.StateConflict);

        using var unknown = await cms.SendAsync(Req(HttpMethod.Post, "/sources", _editor,
            json: $$"""{"contentSourceId":"{{Guid.NewGuid()}}","language":"vi","siteKeys":["www"]}""", idempotent: true));
        Assert.True((await NewsHttp.ProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed)).GetProperty("errors").TryGetProperty("contentSourceId", out _));

        var disabled = await CatalogueSourceAsync(enabled: false);
        using var off = await cms.SendAsync(Req(HttpMethod.Post, "/sources", _editor,
            json: $$"""{"contentSourceId":"{{disabled}}","language":"vi","siteKeys":["www"]}""", idempotent: true));
        await NewsHttp.ProblemAsync(off, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);

        var other = await CatalogueSourceAsync();
        using var cmsSite = await cms.SendAsync(Req(HttpMethod.Post, "/sources", _editor,
            json: $$"""{"contentSourceId":"{{other}}","language":"vi","siteKeys":["www","cms"]}""", idempotent: true));
        Assert.Contains("cms", (await NewsHttp.ProblemAsync(cmsSite, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed)).GetProperty("detail").GetString(), StringComparison.Ordinal);

        // additionalProperties: false — chế độ khai thác không đặt được lúc tạo.
        using var withMode = await cms.SendAsync(Req(HttpMethod.Post, "/sources", _editor,
            json: $$"""{"contentSourceId":"{{other}}","language":"vi","siteKeys":["www"],"usageMode":"M3"}""", idempotent: true));
        await NewsHttp.ProblemAsync(withMode, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
    }

    [DbFact]
    public async Task Patch_needs_if_match_and_cannot_change_usage_mode()
    {
        using var cms = Cms();
        var sourceId = await CatalogueSourceAsync();
        var (_, etag) = await CreateProfileAsync(cms, sourceId);

        using (var missing = await cms.SendAsync(Req(HttpMethod.Patch, $"/sources/{sourceId}", _editor, json: """{"trustWeight":5}""")))
        {
            await NewsHttp.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, ErrorCodes.PreconditionRequired);
        }

        using (var stale = await cms.SendAsync(Req(HttpMethod.Patch, $"/sources/{sourceId}", _editor, json: """{"trustWeight":5}""", ifMatch: "W/\"9\"")))
        {
            await NewsHttp.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
        }

        using (var mode = await cms.SendAsync(Req(HttpMethod.Patch, $"/sources/{sourceId}", _editor, json: """{"usageMode":"M3"}""", ifMatch: etag)))
        {
            Assert.True((await NewsHttp.ProblemAsync(mode, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed)).GetProperty("errors").TryGetProperty("usageMode", out _));
        }

        using (var notFound = await cms.SendAsync(Req(HttpMethod.Patch, $"/sources/{Guid.NewGuid()}", _editor, json: """{"trustWeight":5}""", ifMatch: etag)))
        {
            await NewsHttp.ProblemAsync(notFound, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        var (patched, newEtag) = await SendAsync(cms, Req(HttpMethod.Patch, $"/sources/{sourceId}", _editor, json: """{"trustWeight":5,"language":"en"}""", ifMatch: etag));
        Assert.Equal("W/\"2\"", newEtag);
        Assert.Equal(5, patched.GetProperty("trustWeight").GetInt32());
        Assert.Equal("en", patched.GetProperty("language").GetString());
        Assert.Equal("M0", patched.GetProperty("usageMode").GetString());
    }

    // ---------------------------------------------------------------- duyệt

    [DbFact]
    public async Task Approval_enforces_mfa_licence_and_separation_of_duties()
    {
        using var cms = Cms();
        var sourceId = await CatalogueSourceAsync();
        var (_, etag) = await CreateProfileAsync(cms, sourceId);   // _editor đề xuất

        // M2 thiếu giấy phép → 422 (if/then của schema).
        using (var noLicence = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/approval", _approver, StaffRoles.Approver, mfa: true,
            json: """{"usageMode":"M2","termsEvidenceRef":"dms://t"}""", ifMatch: etag, idempotent: true)))
        {
            await NewsHttp.ProblemAsync(noLicence, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        }

        // Người đề xuất (có cả vai trò APPROVER) không tự duyệt M2 → SOD_VIOLATION; M1 thì được.
        using (var sod = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/approval", _editor, EditorApprover, mfa: true,
            json: $$"""{"usageMode":"M2","licenseId":"{{_licence}}","termsEvidenceRef":"dms://t"}""", ifMatch: etag, idempotent: true)))
        {
            await NewsHttp.ProblemAsync(sod, HttpStatusCode.Forbidden, ErrorCodes.SodViolation);
        }

        // Giấy phép hết hạn → 422.
        using (var expired = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/approval", _approver, StaffRoles.Approver, mfa: true,
            json: $$"""{"usageMode":"M3","licenseId":"{{_expiredLicence}}","termsEvidenceRef":"dms://t"}""", ifMatch: etag, idempotent: true)))
        {
            Assert.True((await NewsHttp.ProblemAsync(expired, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed)).GetProperty("errors").TryGetProperty("licenseId", out _));
        }

        var (m1, etagM1) = await ApproveAsync(cms, sourceId, etag, "M1", by: _editor, roles: EditorApprover);
        Assert.Equal("ENABLED", m1.GetProperty("status").GetString());
        Assert.Equal(_editor, m1.GetProperty("approvedBy").GetGuid());

        var (m2, _) = await ApproveAsync(cms, sourceId, etagM1, "M2", _licence);
        Assert.Equal("M2", m2.GetProperty("usageMode").GetString());
        Assert.Equal(_licence, m2.GetProperty("licenseId").GetGuid());
        var approvals = m2.GetProperty("approvals").EnumerateArray().ToList();
        Assert.Equal(["M2", "M1"], approvals.Select(a => a.GetProperty("usageMode").GetString()));   // mới nhất trước
        Assert.Equal(_approver, approvals[0].GetProperty("approvedBy").GetGuid());
        Assert.Equal("Đã đọc điều khoản", approvals[0].GetProperty("note").GetString());

        // ETag cũ → 412; không tạo thêm bản ghi duyệt.
        using (var stale = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/approval", _approver, StaffRoles.Approver, mfa: true,
            json: """{"usageMode":"M0","termsEvidenceRef":"dms://t"}""", ifMatch: etag, idempotent: true)))
        {
            await NewsHttp.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
        }

        Assert.Equal(2L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_source_approvals WHERE content_source_id = '{sourceId}'"));
    }

    // ---------------------------------------------------------------- trạng thái

    [DbFact]
    public async Task Pause_resume_retire_follow_the_lifecycle()
    {
        using var cms = Cms();
        var sourceId = await CatalogueSourceAsync();
        var (_, etag) = await CreateProfileAsync(cms, sourceId);

        using (var noReason = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/pause", _editor, json: """{"reason":"x"}""", ifMatch: etag)))
        {
            await NewsHttp.ProblemAsync(noReason, HttpStatusCode.UnprocessableEntity, ErrorCodes.ReasonRequired);
        }

        var (paused, etagPaused) = await SendAsync(cms, Req(HttpMethod.Post, $"/sources/{sourceId}/pause", _editor, json: """{"reason":"Chờ xem điều khoản"}""", ifMatch: etag));
        Assert.Equal("PAUSED", paused.GetProperty("status").GetString());

        using (var again = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/pause", _editor, json: """{"reason":"Lần nữa nhé"}""", ifMatch: etagPaused)))
        {
            await NewsHttp.ProblemAsync(again, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        }

        // Chưa từng được duyệt → không tiếp tục được.
        using (var neverApproved = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/resume", _editor, ifMatch: etagPaused)))
        {
            await NewsHttp.ProblemAsync(neverApproved, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        }

        var (approved, etagApproved) = await ApproveAsync(cms, sourceId, etagPaused);
        Assert.Equal("ENABLED", approved.GetProperty("status").GetString());
        var (_, etagPaused2) = await SendAsync(cms, Req(HttpMethod.Post, $"/sources/{sourceId}/pause", _editor, json: """{"reason":"Nguồn lỗi tạm thời"}""", ifMatch: etagApproved));
        var (resumed, etagResumed) = await SendAsync(cms, Req(HttpMethod.Post, $"/sources/{sourceId}/resume", _editor, ifMatch: etagPaused2));
        Assert.Equal("ENABLED", resumed.GetProperty("status").GetString());

        using (var noRetireReason = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/retire", _editor, json: "{}", ifMatch: etagResumed)))
        {
            await NewsHttp.ProblemAsync(noRetireReason, HttpStatusCode.UnprocessableEntity, ErrorCodes.ReasonRequired);
        }

        var (retired, etagRetired) = await SendAsync(cms, Req(HttpMethod.Post, $"/sources/{sourceId}/retire", _editor, json: """{"reason":"Nguồn ngừng xuất bản"}""", ifMatch: etagResumed));
        Assert.Equal("RETIRED", retired.GetProperty("status").GetString());
        foreach (var (method, path, json) in new[] { ("POST", "/resume", (string?)null), ("PATCH", "", """{"trustWeight":1}"""), ("POST", "/retire", """{"reason":"Lần hai nhé"}""") })
        {
            using var blocked = await cms.SendAsync(Req(new HttpMethod(method), $"/sources/{sourceId}{path}", _editor, json: json, ifMatch: etagRetired));
            await NewsHttp.ProblemAsync(blocked, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        }

        Assert.Equal("->PROPOSED,PROPOSED>PAUSED:Chờ xem điều khoản,PAUSED>ENABLED,ENABLED>PAUSED:Nguồn lỗi tạm thời,PAUSED>ENABLED,ENABLED>RETIRED:Nguồn ngừng xuất bản",
            await db.ScalarAsync<string>($"""
                SELECT string_agg(coalesce(from_status, '-') || '>' || to_status || coalesce(':' || reason, ''), ',' ORDER BY id)
                  FROM tt.agg_source_status_history WHERE content_source_id = '{sourceId}'
                """));
    }

    // ---------------------------------------------------------------- feed

    [DbFact]
    public async Task Feeds_are_ssrf_checked_and_send_the_source_back_for_approval()
    {
        using var cms = Cms();
        var sourceId = await CatalogueSourceAsync();
        var (_, etag) = await CreateProfileAsync(cms, sourceId);
        var (_, etagEnabled) = await ApproveAsync(cms, sourceId, etag);

        foreach (var unsafeUrl in new[] { "http://news.example.org/rss", "https://127.0.0.1/rss", "https://internal-feed.example.org/rss" })
        {
            using var rejected = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/feeds", _editor, json: $$"""{"url":"{{unsafeUrl}}"}""", idempotent: true));
            await NewsHttp.ProblemAsync(rejected, HttpStatusCode.UnprocessableEntity, ErrorCodes.UnsafeUrl);
        }

        using (var badCategory = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{sourceId}/feeds", _editor,
            json: $$"""{"url":"https://news.example.org/rss-{{sourceId:N}}","categoryMapping":[{"feedCategory":"Mobile","categoryId":"{{_disabledCategory}}"}]}""", idempotent: true)))
        {
            await NewsHttp.ProblemAsync(badCategory, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        }

        var url = $"https://news.example.org/rss-{sourceId:N}";
        var (feed, feedEtag) = await SendAsync(cms, Req(HttpMethod.Post, $"/sources/{sourceId}/feeds", _editor,
            json: $$"""{"url":"{{url}}","format":"RSS2","scheduleMinutes":30,"categoryMapping":[{"feedCategory":"Mobile","categoryId":"{{_category}}"}]}""",
            idempotent: true), HttpStatusCode.Created);
        Assert.Equal("W/\"1\"", feedEtag);
        Assert.Equal(sourceId, feed.GetProperty("sourceId").GetGuid());
        Assert.Equal("NEVER_FETCHED", feed.GetProperty("health").GetString());
        Assert.Equal(30, feed.GetProperty("scheduleMinutes").GetInt32());
        Assert.Equal(_category, feed.GetProperty("categoryMapping")[0].GetProperty("categoryId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, feed.GetProperty("nextFetchAt").ValueKind);

        // BR-01: thêm feed → nguồn chờ duyệt lại (ETag cũ của nguồn hết hiệu lực).
        var (source, sourceEtag) = await SendAsync(cms, Req(HttpMethod.Get, $"/sources/{sourceId}", _editor));
        Assert.Equal("PROPOSED", source.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("approvedBy").ValueKind);
        Assert.Equal("M1", source.GetProperty("usageMode").GetString());
        Assert.Equal(1, source.GetProperty("feedCount").GetInt32());
        Assert.Equal("NEVER_FETCHED", source.GetProperty("worstFeedHealth").GetString());
        Assert.NotEqual(etagEnabled, sourceEtag);

        var other = await CatalogueSourceAsync();
        await CreateProfileAsync(cms, other);
        using (var duplicate = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{other}/feeds", _editor, json: $$"""{"url":"{{url}}"}""", idempotent: true)))
        {
            await NewsHttp.ProblemAsync(duplicate, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        }

        using (var noSource = await cms.SendAsync(Req(HttpMethod.Post, $"/sources/{Guid.NewGuid()}/feeds", _editor, json: """{"url":"https://news.example.org/x"}""", idempotent: true)))
        {
            await NewsHttp.ProblemAsync(noSource, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        // PATCH feed: If-Match, tắt → DISABLED + không còn lịch; đổi URL → làm lại từ đầu + nguồn chờ duyệt lại.
        var feedId = feed.GetProperty("id").GetGuid();
        using (var missing = await cms.SendAsync(Req(HttpMethod.Patch, $"/feeds/{feedId}", _editor, json: """{"isEnabled":false}""")))
        {
            await NewsHttp.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, ErrorCodes.PreconditionRequired);
        }

        using (var unknownField = await cms.SendAsync(Req(HttpMethod.Patch, $"/feeds/{feedId}", _editor, json: """{"health":"OK"}""", ifMatch: feedEtag)))
        {
            await NewsHttp.ProblemAsync(unknownField, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        }

        using (var noFeed = await cms.SendAsync(Req(HttpMethod.Patch, $"/feeds/{Guid.NewGuid()}", _editor, json: """{"isEnabled":false}""", ifMatch: feedEtag)))
        {
            await NewsHttp.ProblemAsync(noFeed, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        var (disabled, disabledEtag) = await SendAsync(cms, Req(HttpMethod.Patch, $"/feeds/{feedId}", _editor, json: """{"isEnabled":false}""", ifMatch: feedEtag));
        Assert.Equal("W/\"2\"", disabledEtag);
        Assert.Equal("DISABLED", disabled.GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.Null, disabled.GetProperty("nextFetchAt").ValueKind);

        var (_, approvedEtag) = await ApproveAsync(cms, sourceId, sourceEtag);
        await ExecAsync($"UPDATE tt.agg_feeds SET health = 'FAILING', consecutive_failures = 4, last_failure = 'TIMEOUT', etag = 'W/\"abc\"' WHERE id = '{feedId}'");
        var (moved, _) = await SendAsync(cms, Req(HttpMethod.Patch, $"/feeds/{feedId}", _editor,
            json: $$"""{"url":"https://news.example.org/moi-{{sourceId:N}}","isEnabled":true}""", ifMatch: "W/\"3\""));
        Assert.Equal("NEVER_FETCHED", moved.GetProperty("health").GetString());
        Assert.Equal(0, moved.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("lastFailure").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, moved.GetProperty("nextFetchAt").ValueKind);
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_feeds WHERE id = '{feedId}' AND etag IS NOT NULL"));

        var (back, backEtag) = await SendAsync(cms, Req(HttpMethod.Get, $"/sources/{sourceId}", _editor));
        Assert.Equal("PROPOSED", back.GetProperty("status").GetString());
        Assert.NotEqual(approvedEtag, backEtag);
    }
}

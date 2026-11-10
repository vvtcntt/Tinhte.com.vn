using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Ads.Application;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Sites;

namespace Tinhte.Api.Tests;

/// <summary>Slot quảng cáo công khai (FR-ADS-001/002, WBS-133; DRAFT đợt 3) — phần không cần DB.</summary>
public sealed class AdsUnitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/api/v1/ads/placements")]                                   // thiếu placementCodes
    [InlineData("/api/v1/ads/placements?placementCodes=")]
    [InlineData("/api/v1/ads/placements?placementCodes=home_top,,home_mid")]
    [InlineData("/api/v1/ads/placements?placementCodes=Home_Top")]           // sai mẫu ^[a-z0-9_-]{2,80}$
    [InlineData("/api/v1/ads/placements?placementCodes=a1,a2,a3,a4,a5,a6,a7,a8,a9,a10,a11")] // quá maxItems 10
    public async Task Invalid_placement_codes_are_400(string path)
    {
        using var www = factory.WithProbes().Client(TestHost.Www);
        using var response = await www.GetAsync(path);
        await NewsHttp.ProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
    }

    [Fact]
    public void Placement_codes_accept_both_query_styles_and_drop_duplicates()
    {
        Assert.Equal(["home_top", "home_mid", "home_feed"], PublicAdSlotService.ParseCodes(["home_top,home_mid", "home_top", "home_feed"]));
        Assert.Equal(10, PublicAdSlotService.ParseCodes([string.Join(',', Enumerable.Range(1, 10).Select(i => $"c{i}")) + ",c1"])!.Count);
        Assert.Null(PublicAdSlotService.ParseCodes([]));
        Assert.Null(PublicAdSlotService.ParseCodes(null));
    }

    [Fact]
    public async Task Public_slots_are_not_served_on_the_cms_site_and_need_the_database()
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        using (var onCms = await cms.GetAsync("/api/v1/ads/placements?placementCodes=home_top"))
        {
            Assert.Equal(HttpStatusCode.NotFound, onCms.StatusCode);
        }

        using var www = factory.WithProbes().Client(TestHost.Www);
        using var response = await www.GetAsync("/api/v1/ads/placements?placementCodes=home_top");
        await NewsHttp.ProblemAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
    }

    [Fact]
    public void Creative_url_comes_from_the_media_site_of_the_registry()
    {
        foreach (var env in new[] { "production", "staging", "local" })
        {
            var registry = SiteRegistry.Load(RepoPaths.File("config", "sites", "sites.json"), env);
            var media = registry.ResolvableSites.Single(s => s.Kind == "media");
            var origin = AdCreativeUrls.MediaOrigin(registry);
            Assert.Equal(registry.OriginFor(media), origin);
            Assert.Equal($"{origin}/ads/2026/11/banner%20top.png", AdCreativeUrls.For(origin!, "ads/2026/11/banner top.png"));
        }
    }
}

/// <summary>
/// Slot quảng cáo trên PostgreSQL thật: lọc theo site của Host, chọn banner theo breakpoint/ưu tiên/khung giờ, media fail-closed,
/// cờ ads.display_slots OFF → rỗng.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdsDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Path = "/api/v1/ads/placements?placementCodes=home_top,home_mid,home_sidebar,home_bottom,khong_co";
    private readonly Guid _approver = Guid.NewGuid();

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private string Media(string storageKey, string visibility = "PUBLIC", string status = "READY")
    {
        var id = Guid.NewGuid();
        _media.Add($"""
            INSERT INTO tt.media_assets (id, status, media_type, visibility, rights_status, storage_key)
            VALUES ('{id}', '{status}', 'IMAGE', '{visibility}', '{(visibility == "PUBLIC" ? "CLEARED" : "UNVERIFIED")}', '{storageKey}');
            """);
        return id.ToString();
    }

    private readonly List<string> _media = [];

    private static string Placement(Guid id, string site, string code, string status = "ACTIVE", string label = "Quảng cáo") => $"""
        INSERT INTO tt.ad_placements (id, site_key, code, label, status, approved_by, approved_at)
        VALUES ('{id}', '{site}', '{code}', '{label}', '{status}', {(status is "ACTIVE" or "APPROVED" ? "@approver, now()" : "NULL, NULL")});
        INSERT INTO tt.ad_placement_sizes (placement_id, breakpoint, width, height) VALUES
          ('{id}', 'MOBILE', 320, 100), ('{id}', 'TABLET', 728, 90), ('{id}', 'DESKTOP', 970, 90), ('{id}', 'DESKTOP', 970, 250);
        """;

    private static string Booking(Guid placement, int priority, string window = "NULL, NULL", string status = "ACTIVE", params (string Bp, int W, int H, string Media)[] creatives)
    {
        var id = Guid.NewGuid();
        var approved = status is "ACTIVE" or "APPROVED" ? "@approver, now()" : "NULL, NULL";
        return $"""
            INSERT INTO tt.ad_bookings (id, placement_id, status, priority, target_url, starts_at, ends_at, approved_by, approved_at)
            VALUES ('{id}', '{placement}', '{status}', {priority}, 'https://acme.example/{priority}', {window}, {approved});
            """ + string.Concat(creatives.Select(c => $"""
            INSERT INTO tt.ad_booking_creatives (booking_id, placement_id, breakpoint, width, height, media_asset_id, alt_text)
            VALUES ('{id}', '{placement}', '{c.Bp}', {c.W}, {c.H}, '{c.Media}', 'Banner {priority} {c.Bp}');
            """));
    }

    private async Task<JsonElement[]> SlotsAsync(HttpClient www)
    {
        using var response = await www.GetAsync(Path);
        var body = await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private Task SetFlagAsync(bool on) => ExecAsync(on
        ? $"UPDATE tt.feature_gates SET enabled = true, approval_reference = 'TEST-ADS', approved_by = '{_approver}', approved_at = now() WHERE key = 'ads.display_slots'"
        : "UPDATE tt.feature_gates SET enabled = false WHERE key = 'ads.display_slots'");

    [DbFact]
    public async Task Slots_follow_site_breakpoint_priority_schedule_and_flag()
    {
        var app = factory.WithProbes(db.ConnectionString);
        using var www = app.Client(TestHost.Www);
        var mediaOrigin = AdCreativeUrls.MediaOrigin(app.Services.GetRequiredService<SiteRegistry>());

        Guid top = Guid.NewGuid(), mid = Guid.NewGuid(), sidebar = Guid.NewGuid(), bottom = Guid.NewGuid(), otherSite = Guid.NewGuid();
        var low = Media("ads/low-desktop.png");
        var lowMobile = Media("ads/low mobile.png");
        var high = Media("ads/high-desktop.png");
        var future = Media("ads/future.png");
        var privateMedia = Media("ads/private.png", visibility: "PRIVATE");
        var processing = Media("ads/processing.png", status: "PROCESSING");
        var other = Media("ads/other-site.png");

        var sql = string.Concat(_media) + $"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_approver}', 'Người duyệt quảng cáo');
            INSERT INTO tt.sites (key, label, kind, status, scope) VALUES ('ads-probe', 'Site thử quảng cáo', 'public-web', 'planned', 'P1')
              ON CONFLICT (key) DO UPDATE SET status = 'planned';
            """
            + Placement(top, "www", "home_top")
            // DESKTOP: ưu tiên 50 thắng 10; MOBILE chỉ có ở đặt chỗ 10; đặt chỗ 100 chưa tới giờ, 90 đã hết giờ, 80 bị tạm dừng.
            + Booking(top, 10, creatives: [("DESKTOP", 970, 90, low), ("MOBILE", 320, 100, lowMobile)])
            + Booking(top, 50, creatives: [("DESKTOP", 970, 250, high)])
            + Booking(top, 100, "now() + interval '1 day', NULL", creatives: [("DESKTOP", 970, 90, future)])
            + Booking(top, 90, "now() - interval '2 days', now() - interval '1 day'", creatives: [("TABLET", 728, 90, future)])
            + Booking(top, 80, status: "PAUSED", creatives: [("TABLET", 728, 90, future)])
            // Vị trí DRAFT: đặt chỗ ACTIVE cũng không được phục vụ.
            + Placement(mid, "www", "home_mid", status: "DRAFT")
            + Booking(mid, 10, creatives: [("DESKTOP", 970, 90, low)])
            // Media chưa công khai / chưa xử lý xong → không có banner → không có slot.
            + Placement(sidebar, "www", "home_sidebar")
            + Booking(sidebar, 10, creatives: [("DESKTOP", 970, 90, privateMedia), ("MOBILE", 320, 100, processing)])
            // Cùng mã ở site khác: không lọt sang www.
            + Placement(otherSite, "ads-probe", "home_bottom")
            + Booking(otherSite, 10, creatives: [("DESKTOP", 970, 90, other)])
            + Placement(bottom, "www", "home_bottom", label: "Tài trợ")
            + Booking(bottom, 10, creatives: [("TABLET", 728, 90, low)]);

        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("approver", _approver);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            // Cờ OFF (mặc định production) → 200 rỗng.
            Assert.Empty(await SlotsAsync(www));

            await SetFlagAsync(true);
            var slots = await SlotsAsync(www);
            Assert.Equal(["home_top", "home_bottom"], slots.Select(s => s.GetProperty("code").GetString()));
            Assert.All(slots, s => Assert.Equal("sponsored noopener", s.GetProperty("rel").GetString()));

            var topCreatives = slots[0].GetProperty("creatives").EnumerateArray().ToList();
            Assert.Equal(["MOBILE", "DESKTOP"], topCreatives.Select(c => c.GetProperty("breakpoint").GetString()));
            Assert.Equal((320, 100), (topCreatives[0].GetProperty("width").GetInt32(), topCreatives[0].GetProperty("height").GetInt32()));
            Assert.Equal($"{mediaOrigin}/ads/low%20mobile.png", topCreatives[0].GetProperty("creativeUrl").GetString());
            Assert.Equal((970, 250), (topCreatives[1].GetProperty("width").GetInt32(), topCreatives[1].GetProperty("height").GetInt32()));
            Assert.Equal($"{mediaOrigin}/ads/high-desktop.png", topCreatives[1].GetProperty("creativeUrl").GetString());
            Assert.Equal("https://acme.example/50", topCreatives[1].GetProperty("targetUrl").GetString());
            Assert.Equal("Banner 50 DESKTOP", topCreatives[1].GetProperty("altText").GetString());

            var bottomSlot = slots[1];
            Assert.Equal("Tài trợ", bottomSlot.GetProperty("label").GetString());
            var bottomCreative = Assert.Single(bottomSlot.GetProperty("creatives").EnumerateArray());
            Assert.Equal("TABLET", bottomCreative.GetProperty("breakpoint").GetString());
            Assert.DoesNotContain("other-site", bottomCreative.GetProperty("creativeUrl").GetString(), StringComparison.Ordinal);

            // Đặt chỗ thắng bị tạm dừng → đặt chỗ kế tiếp lên thay, không cần deploy.
            await ExecAsync($"UPDATE tt.ad_bookings SET status = 'PAUSED' WHERE placement_id = '{top}' AND priority = 50");
            var desktop = (await SlotsAsync(www))[0].GetProperty("creatives").EnumerateArray().Last();
            Assert.Equal($"{mediaOrigin}/ads/low-desktop.png", desktop.GetProperty("creativeUrl").GetString());
        }
        finally
        {
            await SetFlagAsync(false);
            // tt.sites không xóa được (chỉ retired) — trả lại trạng thái để test Registry ↔ tt.sites không thấy site thử.
            await ExecAsync("UPDATE tt.sites SET status = 'retired' WHERE key = 'ads-probe'");
        }
    }
}

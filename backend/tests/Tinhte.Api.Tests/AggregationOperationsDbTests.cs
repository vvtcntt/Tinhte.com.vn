using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Platform.Data;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>Kênh cảnh báo giả: ghi lại cảnh báo đã gửi, hoặc ném lỗi để thử nhánh gửi thất bại.</summary>
internal sealed class FakeAlertSink(bool fail = false) : IAlertSink
{
    public List<PendingAlert> Sent { get; } = [];

    public bool IsConfigured => true;

    public Task SendAsync(PendingAlert alert, CancellationToken ct)
    {
        if (fail) throw new HttpRequestException("webhook down");
        Sent.Add(alert);
        return Task.CompletedTask;
    }
}

/// <summary>Vận hành Radar tin (ADR-0008 §4, §9; BR-06): cảnh báo nguồn tự DEGRADED/PAUSED (outbox + webhook), dọn nhật ký 30 ngày, nhịp tim worker.</summary>
public sealed partial class AggregationCollectionDbTests
{
    private CollectorOperations Operations(IAlertSink sink, int retentionDays = 30)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tinhte"] = db.ConnectionString,
            ["Aggregation:FetchLogRetentionDays"] = retentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        var database = new TinhteDatabase(config);
        _databases.Add(database);
        return new CollectorOperations(database, sink, AggregationOptions.From(config), NullLogger<CollectorOperations>.Instance);
    }

    [DbFact]
    public async Task Automatic_pause_raises_alerts_shown_in_cms_and_sent_to_the_alert_channel()
    {
        var (source, feed, url) = await EnabledFeedAsync();
        try
        {
            var collector = Collector(new FakeFeedHandler().On(url, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            for (var i = 0; i < 5; i++) await RunAsync(collector, feed);
            await ExecAsync($"UPDATE tt.agg_feeds SET failing_since = now() - interval '25 hours' WHERE id = '{feed}'");
            await RunAsync(collector, feed);
            Assert.Equal("SOURCE_DEGRADED,SOURCE_AUTO_PAUSED", await db.ScalarAsync<string>(
                $"SELECT string_agg(kind, ',' ORDER BY id) FROM tt.agg_alerts WHERE content_source_id = '{source}'"));

            // CMS: nguồn cho biết hệ thống tự đổi trạng thái + lý do; thống kê liệt kê cảnh báo của site.
            using var cms = Cms();
            using (var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"{Base}/sources/{source}", _editor, StaffRoles.Editor)))
            {
                var body = await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
                Assert.Equal(("PAUSED", true), (body.GetProperty("status").GetString(), body.GetProperty("statusChangedBySystem").GetBoolean()));
                Assert.StartsWith("Tự động tạm dừng (BR-06)", body.GetProperty("statusReason").GetString(), StringComparison.Ordinal);
                Assert.Equal(JsonValueKind.String, body.GetProperty("statusChangedAt").ValueKind);
            }

            using (var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"{Base}/sources?q={source:N}", _editor, StaffRoles.Editor)))
            {
                var row = (await NewsHttp.JsonAsync(response, HttpStatusCode.OK)).GetProperty("items")[0];
                Assert.True(row.GetProperty("statusChangedBySystem").GetBoolean());
            }

            using (var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"{Base}/stats?siteKey=www", _editor, StaffRoles.Editor)))
            {
                var alerts = (await NewsHttp.JsonAsync(response, HttpStatusCode.OK)).GetProperty("alerts").EnumerateArray()
                    .Where(a => a.GetProperty("sourceId").GetGuid() == source).ToList();
                Assert.Equal(["SOURCE_AUTO_PAUSED", "SOURCE_DEGRADED"], alerts.Select(a => a.GetProperty("kind").GetString()));   // mới trước
                Assert.All(alerts, a => Assert.False(a.GetProperty("delivered").GetBoolean()));
            }

            // Kênh lỗi → tăng số lần thử, giữ chưa gửi; kênh tốt → gửi và đánh dấu đã gửi; không có kênh → không làm gì.
            Assert.Equal(0, await Operations(new NoAlertSink()).DispatchAlertsAsync(CancellationToken.None));
            await Operations(new FakeAlertSink(fail: true)).DispatchAlertsAsync(CancellationToken.None);
            Assert.Equal("1|webhook down|0", await db.ScalarAsync<string>($"""
                SELECT min(attempts) || '|' || min(last_error) || '|' || count(delivered_at) FROM tt.agg_alerts WHERE content_source_id = '{source}'
                """));
            var sink = new FakeAlertSink();
            await Operations(sink).DispatchAlertsAsync(CancellationToken.None);
            Assert.Contains(sink.Sent, a => a.SourceId == source && a.Kind == "SOURCE_AUTO_PAUSED" && a.SourceName == $"Nguồn {source:N}");
            Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_alerts WHERE content_source_id = '{source}' AND delivered_at IS NULL"));
        }
        finally
        {
            await ParkAsync(feed);
        }
    }

    [DbFact]
    public async Task Fetch_log_older_than_retention_is_purged_and_heartbeats_are_reported()
    {
        var (_, feed, _) = await EnabledFeedAsync();
        await ParkAsync(feed);
        await ExecAsync($"""
            INSERT INTO tt.agg_fetch_log (feed_id, started_at, finished_at, outcome) VALUES
              ('{feed}', now() - interval '31 days', now() - interval '31 days', 'OK'),
              ('{feed}', now() - interval '40 days', now() - interval '40 days', 'NOT_MODIFIED'),
              ('{feed}', now() - interval '29 days', now() - interval '29 days', 'OK');
            """);
        var operations = Operations(new NoAlertSink());
        Assert.True(await operations.PurgeFetchLogAsync(CancellationToken.None) >= 2);
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_fetch_log WHERE feed_id = '{feed}'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM tt.agg_fetch_log WHERE started_at < now() - interval '30 days'"));

        var workerId = "test-worker-" + _tag;
        var started = DateTimeOffset.UtcNow.AddMinutes(-3);
        await operations.HeartbeatAsync(workerId, "host-test", "1.0.0", started, 2, true, null, CancellationToken.None);
        await operations.HeartbeatAsync(workerId, "host-test", "1.0.0", started, 0, true, "NpgsqlException: timeout", CancellationToken.None);
        await ExecAsync("INSERT INTO tt.agg_worker_heartbeats (worker_id, started_at, last_seen_at) VALUES ('stale-" + _tag + "', now() - interval '2 days', now() - interval '1 day')");
        using var cms = Cms();
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"{Base}/stats?siteKey=www", _editor, StaffRoles.Editor));
        var workers = (await NewsHttp.JsonAsync(response, HttpStatusCode.OK)).GetProperty("workers").EnumerateArray().ToList();
        var mine = workers.Single(w => w.GetProperty("workerId").GetString() == workerId);
        Assert.Equal((true, "host-test", "NpgsqlException: timeout"), (mine.GetProperty("healthy").GetBoolean(), mine.GetProperty("host").GetString(),
            mine.GetProperty("lastError").GetString()));
        Assert.False(workers.Single(w => w.GetProperty("workerId").GetString() == "stale-" + _tag).GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task Webhook_sink_posts_a_chat_compatible_json_message_over_https()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new FakeFeedHandler().On("https://hooks.example.org/radar", request =>
        {
            captured = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var sink = new WebhookAlertSink(new Uri("https://hooks.example.org/radar"), handler);
        await sink.SendAsync(new PendingAlert(1, "SOURCE_AUTO_PAUSED", Guid.NewGuid(), "Báo Mẫu", "Lỗi 24 giờ", DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("[Radar tin] Nguồn tự tạm dừng: Báo Mẫu — Lỗi 24 giờ", json.GetProperty("text").GetString());
        Assert.Equal("SOURCE_AUTO_PAUSED", json.GetProperty("kind").GetString());

        Assert.Throws<InvalidOperationException>(() => new WebhookAlertSink(new Uri("http://hooks.example.org/radar")));
        var failing = new WebhookAlertSink(new Uri("https://hooks.example.org/missing"), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.SendAsync(new PendingAlert(2, "SOURCE_DEGRADED", Guid.NewGuid(), "X", "Y", DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.IsType<NoAlertSink>(WebhookAlertSink.FromConfiguration(new ConfigurationBuilder().Build()));
    }
}

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Modules.Aggregation.Infrastructure.Collection;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>
/// Radar tin GĐ1 lát 2 trên PostgreSQL: thuê feed (SKIP LOCKED + lease), worker thu thập → khử trùng/phân loại/nhật ký/sức khỏe/backoff,
/// cờ tắt, mất lease; API thử feed, luật chuyên mục, mục tin. HTTP của nguồn luôn là handler giả (không gọi mạng thật).
/// Feed của test có next_fetch_at ở năm 2000 để luôn đứng đầu hàng đợi, không đụng feed của test khác.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AggregationCollectionDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Base = "/api/v1/admin/aggregation";
    private readonly Guid _editor = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly Guid _phones = Guid.NewGuid();
    private readonly Guid _laptops = Guid.NewGuid();
    private readonly Guid _disabledCategory = Guid.NewGuid();
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private readonly List<TinhteDatabase> _databases = [];
    private readonly FakeFeedHandler _apiHandler = new();
    private WebApplicationFactory<Program>? _app;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) return;
        await ExecAsync($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_editor}', 'Biên tập'), ('{_approver}', 'Người duyệt');
            INSERT INTO tt.categories (id, slug, name) VALUES ('{_phones}', 'dt-{_tag}', 'Điện thoại {_tag}'), ('{_laptops}', 'lt-{_tag}', 'Laptop {_tag}');
            INSERT INTO tt.categories (id, slug, name, is_enabled) VALUES ('{_disabledCategory}', 'off-{_tag}', 'Đã tắt {_tag}', false);
            INSERT INTO tt.agg_category_rules (category_id, include_keywords, entity_keywords) VALUES ('{_phones}', ARRAY['dòng{_tag}'], ARRAY['Model{_tag}']);
            """);
        await SetFlagAsync(true);
        _app = factory.WithProbes(db.ConnectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("Aggregation:FeedTests:PerMinute", "3");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton(FeedFixtures.Resolver());
                s.AddSingleton(FeedFixtures.Fetcher(_apiHandler));
            });
        });
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        foreach (var d in _databases) await d.DisposeAsync();
    }

    // ---------------------------------------------------------------- hạ tầng test

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private Task SetFlagAsync(bool on) => ExecAsync(on
        ? $"UPDATE tt.feature_gates SET enabled = true, approval_reference = 'TEST-AGG', approved_by = '{_approver}', approved_at = now() WHERE key = 'aggregation.radar'"
        : "UPDATE tt.feature_gates SET enabled = false WHERE key = 'aggregation.radar'");

    private async Task<bool> IsPgliteAsync() => (await db.ScalarAsync<string>("SELECT version()"))!.Contains("emscripten", StringComparison.OrdinalIgnoreCase);

    private FeedCollectionService Collector(FakeFeedHandler handler, int parallelism = 1)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tinhte"] = db.ConnectionString,
            ["Aggregation:Worker:Parallelism"] = parallelism.ToString(CultureInfo.InvariantCulture),
        }).Build();
        var database = new TinhteDatabase(config);
        _databases.Add(database);
        return new FeedCollectionService(database, FeedFixtures.Fetcher(handler), AggregationOptions.From(config), new NewsroomClock(null),
            SensitiveTopics.Load(RepoPaths.File("config", "aggregation", "sensitive-topics.json")), TimeProvider.System, NullLogger<FeedCollectionService>.Instance);
    }

    /// <summary>Nguồn ENABLED (đã duyệt M1) + một feed đến hạn từ năm 2000 (đứng đầu hàng đợi).</summary>
    private async Task<(Guid Source, Guid Feed, string Url)> EnabledFeedAsync(string mapping = "[]", int minutesAfter2000 = 0)
    {
        var (source, feed) = (Guid.NewGuid(), Guid.NewGuid());
        var url = $"https://news.example.org/rss-{feed:N}";
        await ExecAsync($"""
            BEGIN;
            SET LOCAL tt.actor_id = '{_editor}';
            INSERT INTO tt.content_sources (id, name) VALUES ('{source}', 'Nguồn {source:N}');
            INSERT INTO tt.agg_source_profiles (content_source_id, language, site_keys, proposed_by) VALUES ('{source}', 'vi', ARRAY['www'], '{_editor}');
            SET LOCAL tt.actor_id = '{_approver}';
            INSERT INTO tt.agg_source_approvals (content_source_id, usage_mode, terms_evidence_ref, approved_by) VALUES ('{source}', 'M1', 'dms://t', '{_approver}');
            UPDATE tt.agg_source_profiles p SET usage_mode = 'M1', terms_evidence_ref = 'dms://t', approved_by = a.approved_by, approved_at = a.approved_at, status = 'ENABLED'
              FROM tt.agg_source_approvals a WHERE a.content_source_id = p.content_source_id AND p.content_source_id = '{source}';
            INSERT INTO tt.agg_feeds (id, content_source_id, url, category_mapping, next_fetch_at)
              VALUES ('{feed}', '{source}', '{url}', '{mapping}', timestamptz '2000-01-01 00:00:00+00' + interval '{minutesAfter2000} minutes');
            COMMIT;
            """);
        return (source, feed, url);
    }

    private Task MakeDueAsync(Guid feed) => ExecAsync($"UPDATE tt.agg_feeds SET next_fetch_at = '2000-01-01T00:00:00Z' WHERE id = '{feed}'");

    private Task ParkAsync(params Guid[] feeds) =>
        ExecAsync($"UPDATE tt.agg_feeds SET next_fetch_at = NULL, lease_until = NULL WHERE id = ANY(ARRAY[{string.Join(',', feeds.Select(f => $"'{f}'::uuid"))}])");

    private static async Task<FeedLease> LeaseOneAsync(FeedCollectionService collector, Guid feed)
    {
        var leases = await collector.LeaseAsync(1, CancellationToken.None);
        return Assert.Single(leases, l => l.FeedId == feed);
    }

    private async Task<FeedRunResult> RunAsync(FeedCollectionService collector, Guid feed)
    {
        await MakeDueAsync(feed);
        return await collector.ProcessAsync(await LeaseOneAsync(collector, feed), CancellationToken.None);
    }

    private static DateTimeOffset Ago(double hours) => DateTimeOffset.UtcNow.AddHours(-hours);

    // ---------------------------------------------------------------- thuê feed

    [DbFact]
    public async Task Concurrent_workers_lease_disjoint_feeds_and_expired_leases_are_taken_again()
    {
        var mine = new List<Guid>();
        for (var i = 0; i < 6; i++) mine.Add((await EnabledFeedAsync(minutesAfter2000: i)).Feed);
        try
        {
            var (a, b) = (Collector(new FakeFeedHandler()), Collector(new FakeFeedHandler()));
            var results = await Task.WhenAll(a.LeaseAsync(3, CancellationToken.None), b.LeaseAsync(3, CancellationToken.None));
            var first = results[0].Select(l => l.FeedId).ToHashSet();
            var second = results[1].Select(l => l.FeedId).ToHashSet();
            Assert.Empty(first.Intersect(second));                          // không feed nào bị hai worker cùng thuê
            Assert.Equal(mine.Order(), first.Union(second).Intersect(mine).Order());
            Assert.All(results.SelectMany(r => r), l => Assert.True(l.LeaseUntil > DateTimeOffset.UtcNow.AddSeconds(60)));

            var third = await a.LeaseAsync(10, CancellationToken.None);
            Assert.Empty(third.Select(l => l.FeedId).Intersect(mine));     // đang được thuê → không lấy lại

            // Worker chết: lease hết hạn → feed được thuê lại.
            await ExecAsync($"UPDATE tt.agg_feeds SET lease_until = now() - interval '1 second' WHERE id = ANY(ARRAY[{string.Join(',', mine.Select(f => $"'{f}'::uuid"))}])");
            Assert.Equal(mine.Order(), (await b.LeaseAsync(6, CancellationToken.None)).Select(l => l.FeedId).Order());

            // Feed đang bị khóa bởi transaction khác (worker khác đang thuê) → bỏ qua ngay, không chờ. PGlite chỉ có một phiên: chỉ chạy trên PostgreSQL thật.
            if (!await IsPgliteAsync())
            {
                await ParkAsync([.. mine]);
                await MakeDueAsync(mine[0]);
                await MakeDueAsync(mine[1]);
                await using var connection = await db.DataSource.OpenConnectionAsync();
                await using var tx = await connection.BeginTransactionAsync();
                await using (var hold = new NpgsqlCommand($"SELECT 1 FROM tt.agg_feeds WHERE id = '{mine[0]}' FOR UPDATE", connection, tx))
                {
                    await hold.ExecuteScalarAsync();
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var skipped = await a.LeaseAsync(2, timeout.Token);
                Assert.Contains(skipped, l => l.FeedId == mine[1]);
                Assert.DoesNotContain(skipped, l => l.FeedId == mine[0]);
                await tx.RollbackAsync();
            }
        }
        finally
        {
            await ParkAsync([.. mine]);
        }
    }

    // ---------------------------------------------------------------- thu thập

    [DbFact]
    public async Task Worker_collects_classifies_dedups_and_versions_items()
    {
        var (source, feed, url) = await EnabledFeedAsync($$"""[{"feedCategory":"Laptop","categoryId":"{{_laptops}}"}]""");
        try
        {
            var first = FeedFixtures.Rss(
                ("g-a", "https://news.example.org/a", $"Model{_tag} là dòng{_tag} mỏng nhất", "<p>Mỏng <b>6mm</b></p><script>x()</script>", "Mobile", Ago(1)),
                ("g-b", "https://news.example.org/b?utm_source=rss", "Đánh giá máy mới", "Pin tốt", "Laptop", Ago(2)),
                ("g-c", "https://news.example.org/c", "Tin đồn về giá vàng", "Chưa rõ", "Kinh tế", Ago(3)),
                ("g-old", "https://news.example.org/old", "Tin cũ", "Cũ", "Mobile", Ago(30)),
                ("g-nolink", "", "Không có link", "x", "Mobile", Ago(1)));
            var handler = new FakeFeedHandler().OnFeed(url, first, etag: "\"e1\"");
            var collector = Collector(handler);

            var run = await RunAsync(collector, feed);
            Assert.Equal((true, "OK", 5, 3, 0), (run.Committed, run.Outcome, run.ItemsSeen, run.ItemsNew, run.ItemsUpdated));   // BR-14: bỏ tin > 24 giờ ở lần đầu

            await using (var connection = await db.DataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand($"""
                SELECT guid, status, category_id, category_confidence, needs_review, sensitive, site_key, language, time_reliable, summary, version
                  FROM tt.agg_items WHERE content_source_id = '{source}' ORDER BY guid
                """, connection))
            await using (var r = await command.ExecuteReaderAsync())
            {
                var rows = new List<(string Guid, string Status, Guid? Category, decimal? Confidence, bool Review, bool Sensitive, string Site, string Lang, bool Reliable, string? Summary, int Version)>();
                while (await r.ReadAsync())
                {
                    rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetDecimal(3), r.GetBoolean(4), r.GetBoolean(5),
                        r.GetString(6), r.GetString(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10)));
                }

                Assert.Equal(["g-a", "g-b", "g-c"], rows.Select(x => x.Guid));
                Assert.Equal(("CLASSIFIED", _phones, 0.95m, false, false, "Mỏng 6mm"), (rows[0].Status, rows[0].Category!.Value, rows[0].Confidence!.Value, rows[0].Review, rows[0].Sensitive, rows[0].Summary!));
                Assert.Equal(("CLASSIFIED", _laptops, 1.000m), (rows[1].Status, rows[1].Category!.Value, rows[1].Confidence!.Value));   // ánh xạ category của feed
                Assert.Equal(("OUT_OF_SCOPE", (Guid?)null, true), (rows[2].Status, rows[2].Category, rows[2].Sensitive));                // "tin đồn" — chủ đề nhạy cảm
                Assert.All(rows, x => Assert.Equal(("www", "vi", true, 1), (x.Site, x.Lang, x.Reliable, x.Version)));
            }

            Assert.Equal("OK|e1|0|true|true|1", await db.ScalarAsync<string>($"""
                SELECT health || '|' || btrim(etag, '"') || '|' || consecutive_failures || '|' || (lease_until IS NULL) || '|'
                       || (next_fetch_at > now() + interval '25 minutes') || '|' || row_version
                  FROM tt.agg_feeds WHERE id = '{feed}'
                """));   // ttl 30 phút của feed; worker không đổi row_version (ETag của CMS) — 0342
            Assert.Equal("OK:200:5/3/0", await db.ScalarAsync<string>($"SELECT outcome || ':' || http_status || ':' || items_seen || '/' || items_new || '/' || items_updated FROM tt.agg_fetch_log WHERE feed_id = '{feed}'"));

            // TC-AGG-02: 304 → không tạo mục, không tính lỗi.
            var notModified = await RunAsync(collector, feed);
            Assert.Equal(("NOT_MODIFIED", 0), (notModified.Outcome, notModified.ItemsNew));
            Assert.Equal(0, await db.ScalarAsync<int>($"SELECT consecutive_failures FROM tt.agg_feeds WHERE id = '{feed}'"));

            // BR-13/20: tiêu đề đổi ⇒ phiên bản 2; cùng URL (khác utm) dù guid đổi ⇒ cùng mục, gộp category; tin cũ giờ mới thấy ⇒ nhận (không còn lần đầu).
            var second = FeedFixtures.Rss(
                ("g-a", "https://news.example.org/a", $"Model{_tag} hoãn ra mắt", "<p>Mỏng <b>6mm</b></p>", "Mobile", Ago(1)),
                ("g-b-moi", "https://news.example.org/b?utm_campaign=x#top", "Đánh giá máy mới", "Pin tốt", "Review", Ago(2)),
                ("g-old", "https://news.example.org/old", "Tin cũ", "Cũ", "Mobile", Ago(30)));
            handler.OnFeed(url, second);
            var changed = await RunAsync(collector, feed);
            Assert.Equal((1, 1), (changed.ItemsNew, changed.ItemsUpdated));
            Assert.Equal(4L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_items WHERE content_source_id = '{source}'"));
            Assert.Equal($"Model{_tag} hoãn ra mắt|2|true|g-a", await db.ScalarAsync<string>(
                $"SELECT title || '|' || version || '|' || (source_updated_at IS NOT NULL) || '|' || guid FROM tt.agg_items WHERE content_source_id = '{source}' AND url_hash = (SELECT url_hash FROM tt.agg_items WHERE guid = 'g-a' AND content_source_id = '{source}')"));
            Assert.Equal("Laptop,Review|g-b", await db.ScalarAsync<string>(
                $"SELECT array_to_string(feed_categories, ',') || '|' || guid FROM tt.agg_items WHERE content_source_id = '{source}' AND url = 'https://news.example.org/b?utm_source=rss'"));
            Assert.Equal(3L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_fetch_log WHERE feed_id = '{feed}'"));
        }
        finally
        {
            await ParkAsync(feed);
        }
    }

    [DbFact]
    public async Task Failures_back_off_then_degrade_and_auto_pause_the_source()
    {
        var (source, feed, url) = await EnabledFeedAsync();
        try
        {
            var handler = new FakeFeedHandler().On(url, _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));
                return r;
            });
            var collector = Collector(handler);
            var limited = await RunAsync(collector, feed);
            Assert.Equal(("FAILED", FeedFailures.RateLimitedBySource), (limited.Outcome, limited.Failure));
            Assert.Equal("DEGRADED|1|RATE_LIMITED_BY_SOURCE|true|true", await db.ScalarAsync<string>($"""
                SELECT health || '|' || consecutive_failures || '|' || last_failure || '|' || (next_fetch_at >= now() + interval '59 minutes') || '|' || (failing_since IS NOT NULL)
                  FROM tt.agg_feeds WHERE id = '{feed}'
                """));   // TC-AGG-08: không gọi lại trước 1 giờ
            Assert.Equal("FAILED:429:RATE_LIMITED_BY_SOURCE", await db.ScalarAsync<string>($"SELECT outcome || ':' || http_status || ':' || failure FROM tt.agg_fetch_log WHERE feed_id = '{feed}'"));

            handler.On(url, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            for (var i = 0; i < 4; i++) await RunAsync(collector, feed);
            Assert.Equal("FAILING|5|DEGRADED", await db.ScalarAsync<string>($"""
                SELECT f.health || '|' || f.consecutive_failures || '|' || p.status FROM tt.agg_feeds f JOIN tt.agg_source_profiles p USING (content_source_id) WHERE f.id = '{feed}'
                """));   // BR-06: 5 lần lỗi liên tiếp ⇒ DEGRADED

            await ExecAsync($"UPDATE tt.agg_feeds SET failing_since = now() - interval '25 hours' WHERE id = '{feed}'");
            await RunAsync(collector, feed);
            Assert.Equal("PAUSED", await db.ScalarAsync<string>($"SELECT status FROM tt.agg_source_profiles WHERE content_source_id = '{source}'"));
            var history = await db.ScalarAsync<string>($"""
                SELECT from_status || '>' || to_status || '|' || (actor_id IS NULL) || '|' || reason FROM tt.agg_source_status_history
                 WHERE content_source_id = '{source}' ORDER BY id DESC LIMIT 1
                """);
            Assert.StartsWith("DEGRADED>PAUSED|true|Tự động tạm dừng (BR-06)", history, StringComparison.Ordinal);   // TC-AGG-09

            await MakeDueAsync(feed);
            Assert.DoesNotContain(await collector.LeaseAsync(5, CancellationToken.None), l => l.FeedId == feed);   // nguồn PAUSED không được thu thập
        }
        finally
        {
            await ParkAsync(feed);
        }
    }

    [DbFact]
    public async Task Degraded_source_recovers_after_a_successful_fetch()
    {
        var (source, feed, url) = await EnabledFeedAsync();
        try
        {
            var handler = new FakeFeedHandler().On(url, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
            var collector = Collector(handler);
            for (var i = 0; i < 5; i++) await RunAsync(collector, feed);
            Assert.Equal("DEGRADED", await db.ScalarAsync<string>($"SELECT status FROM tt.agg_source_profiles WHERE content_source_id = '{source}'"));

            handler.OnFeed(url, FeedFixtures.Rss());
            Assert.Equal("OK", (await RunAsync(collector, feed)).Outcome);
            Assert.Equal("ENABLED|OK|0|true", await db.ScalarAsync<string>($"""
                SELECT p.status || '|' || f.health || '|' || f.consecutive_failures || '|' || (f.failing_since IS NULL)
                  FROM tt.agg_feeds f JOIN tt.agg_source_profiles p USING (content_source_id) WHERE f.id = '{feed}'
                """));
        }
        finally
        {
            await ParkAsync(feed);
        }
    }

    [DbFact]
    public async Task Flag_off_lost_leases_and_paused_sources_write_nothing()
    {
        var (source, feed, url) = await EnabledFeedAsync();
        try
        {
            var handler = new FakeFeedHandler().OnFeed(url, FeedFixtures.Rss(("g", "https://news.example.org/x-" + _tag, "Tin", "", "Mobile", Ago(1))));
            var collector = Collector(handler);

            // Cờ OFF: worker không thuê gì; lease đang giữ được trả lại mà không ghi.
            var lease = await LeaseOneAsync(collector, feed);
            await SetFlagAsync(false);
            try
            {
                Assert.Null(await collector.RunOnceAsync(CancellationToken.None));
                var off = await collector.ProcessAsync(lease, CancellationToken.None);
                Assert.False(off.Committed);
                Assert.Equal("0|true", await db.ScalarAsync<string>($"SELECT (SELECT count(*) FROM tt.agg_fetch_log WHERE feed_id = '{feed}') || '|' || (lease_until IS NULL) FROM tt.agg_feeds WHERE id = '{feed}'"));
            }
            finally
            {
                await SetFlagAsync(true);
            }

            // Lease bị lấy mất (CMS đổi URL xóa lease / worker khác thuê lại sau khi hết hạn) → không ghi gì.
            lease = await LeaseOneAsync(collector, feed);
            await ExecAsync($"UPDATE tt.agg_feeds SET lease_until = now() + interval '5 minutes' WHERE id = '{feed}'");
            Assert.False((await collector.ProcessAsync(lease, CancellationToken.None)).Committed);
            Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_items WHERE content_source_id = '{source}'"));

            // Nguồn bị tạm dừng trong lúc tải: ghi nhật ký nhưng bỏ mục tin, không đặt lịch.
            await ExecAsync($"UPDATE tt.agg_feeds SET lease_until = NULL WHERE id = '{feed}'");
            lease = await LeaseOneAsync(collector, feed);
            await ExecAsync($"BEGIN; SET LOCAL tt.reason = 'Tạm dừng để kiểm tra'; UPDATE tt.agg_source_profiles SET status = 'PAUSED' WHERE content_source_id = '{source}'; COMMIT;");
            var paused = await collector.ProcessAsync(lease, CancellationToken.None);
            Assert.Equal((true, 0), (paused.Committed, paused.ItemsNew));
            Assert.Equal("1|0|true|true", await db.ScalarAsync<string>($"""
                SELECT (SELECT count(*) FROM tt.agg_fetch_log WHERE feed_id = '{feed}') || '|' || (SELECT count(*) FROM tt.agg_items WHERE content_source_id = '{source}')
                       || '|' || (lease_until IS NULL) || '|' || (next_fetch_at < '2001-01-01') FROM tt.agg_feeds WHERE id = '{feed}'
                """));

            // Vòng quét đầy đủ: cờ ON, nguồn ENABLED → thuê và thu thập (Parallelism 1 → chỉ feed đứng đầu hàng đợi).
            await ExecAsync($"""
                BEGIN; SET LOCAL tt.actor_id = '{_approver}';
                UPDATE tt.agg_source_profiles SET status = 'ENABLED' WHERE content_source_id = '{source}'; COMMIT;
                """);
            Assert.Equal(1, await collector.RunOnceAsync(CancellationToken.None));
            Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_items WHERE content_source_id = '{source}'"));
        }
        finally
        {
            await ParkAsync(feed);
        }
    }

    // ---------------------------------------------------------------- API: thử feed

    private HttpClient Cms() => _app!.Client(TestHost.Cms);

    private static HttpRequestMessage Req(HttpMethod method, string path, Guid user, string roles = StaffRoles.Editor, string? json = null, string? ifMatch = null) =>
        NewsHttp.Admin(method, Base + path, user, roles, false, json, ifMatch, null);

    [DbFact]
    public async Task Feed_test_reads_without_saving_and_reports_source_errors_as_data()
    {
        using var cms = Cms();
        var items = Enumerable.Range(0, 7).Select(i => ($"t{i}", $"https://news.example.org/t-{_tag}-{i}", $"Tin thử {i}", "Tóm tắt", "Mobile", Ago(i))).ToArray();
        _apiHandler.OnFeed("https://news.example.org/test-rss", FeedFixtures.Rss(items))
            .On("https://news.example.org/test-500", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .Redirect("https://news.example.org/test-redirect", "https://10.1.1.1/rss");
        var itemsBefore = await db.ScalarAsync<long>("SELECT count(*) FROM tt.agg_items");
        var logsBefore = await db.ScalarAsync<long>("SELECT count(*) FROM tt.agg_fetch_log");

        using (var response = await cms.SendAsync(Req(HttpMethod.Post, "/feed-tests", _editor, json: """{"url":"https://news.example.org/test-rss"}""")))
        {
            var ok = await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
            Assert.True(ok.GetProperty("ok").GetBoolean());
            Assert.Equal(("RSS2", "Báo Mẫu", 7, 200, true), (ok.GetProperty("detectedFormat").GetString(), ok.GetProperty("feedTitle").GetString(),
                ok.GetProperty("itemCount").GetInt32(), ok.GetProperty("httpStatus").GetInt32(), ok.GetProperty("robotsAllowed").GetBoolean()));
            var samples = ok.GetProperty("sampleItems").EnumerateArray().ToList();
            Assert.Equal(5, samples.Count);
            Assert.Equal(("Tin thử 0", true), (samples[0].GetProperty("title").GetString(), samples[0].GetProperty("timeReliable").GetBoolean()));
            Assert.Equal(JsonValueKind.Null, ok.GetProperty("failure").ValueKind);
            Assert.True(ok.GetProperty("durationMs").GetInt64() >= 0);
        }

        using (var response = await cms.SendAsync(Req(HttpMethod.Post, "/feed-tests", _editor, json: """{"url":"https://news.example.org/test-500","format":"ATOM"}""")))
        {
            var failed = await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
            Assert.Equal((false, "HTTP_STATUS", 500), (failed.GetProperty("ok").GetBoolean(), failed.GetProperty("failure").GetString(), failed.GetProperty("httpStatus").GetInt32()));
        }

        Assert.Equal(itemsBefore, await db.ScalarAsync<long>("SELECT count(*) FROM tt.agg_items"));       // không ghi gì
        Assert.Equal(logsBefore, await db.ScalarAsync<long>("SELECT count(*) FROM tt.agg_fetch_log"));

        // SSRF: URL nội bộ, hoặc redirect sang IP nội bộ → 422 UNSAFE_URL; không request nào tới 10.1.1.1.
        var other = Guid.NewGuid();
        foreach (var unsafeUrl in new[] { "https://127.0.0.1/rss", "https://rebind.example.org/rss", "https://news.example.org/test-redirect" })
        {
            using var response = await cms.SendAsync(Req(HttpMethod.Post, "/feed-tests", other, json: $$"""{"url":"{{unsafeUrl}}"}"""));
            await NewsHttp.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, ErrorCodes.UnsafeUrl);
        }

        Assert.DoesNotContain(_apiHandler.Requests, r => r.RequestUri!.Host == "10.1.1.1");

        // Giới hạn 3 lần/phút/người (cấu hình test) → 429 + Retry-After.
        using (var limited = await cms.SendAsync(Req(HttpMethod.Post, "/feed-tests", other, json: """{"url":"https://news.example.org/test-rss"}""")))
        {
            await NewsHttp.ProblemAsync(limited, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
            Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single(), CultureInfo.InvariantCulture) is > 0 and <= 60);
        }

        using (var unknownField = await cms.SendAsync(Req(HttpMethod.Post, "/feed-tests", Guid.NewGuid(), json: """{"url":"https://news.example.org/test-rss","save":true}""")))
        {
            await NewsHttp.ProblemAsync(unknownField, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
        }

        using (var badFormat = await cms.SendAsync(Req(HttpMethod.Post, "/feed-tests", Guid.NewGuid(), json: """{"url":"https://news.example.org/test-rss","format":"XML"}""")))
        {
            await NewsHttp.ProblemAsync(badFormat, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        }

        using (var author = await cms.SendAsync(Req(HttpMethod.Post, "/feed-tests", Guid.NewGuid(), StaffRoles.Author, """{"url":"https://news.example.org/test-rss"}""")))
        {
            await NewsHttp.ProblemAsync(author, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }

        await SetFlagAsync(false);
        try
        {
            using var off = await cms.SendAsync(Req(HttpMethod.Post, "/feed-tests", Guid.NewGuid(), json: """{"url":"https://news.example.org/test-rss"}"""));
            await NewsHttp.ProblemAsync(off, HttpStatusCode.Forbidden, ErrorCodes.FeatureDisabled);
        }
        finally
        {
            await SetFlagAsync(true);
        }
    }

    // ---------------------------------------------------------------- API: luật chuyên mục

    [DbFact]
    public async Task Category_rules_are_listed_per_site_and_patched_with_if_match()
    {
        using var cms = Cms();
        using (var list = await cms.SendAsync(Req(HttpMethod.Get, "/category-rules?siteKey=www", _editor)))
        {
            var rules = (await NewsHttp.JsonAsync(list, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().ToList();
            var laptops = Assert.Single(rules, r => r.GetProperty("categoryId").GetGuid() == _laptops);
            Assert.Equal(($"Laptop {_tag}", "www", 0, 1), (laptops.GetProperty("categoryName").GetString(), laptops.GetProperty("siteKey").GetString(),
                laptops.GetProperty("includeKeywords").GetArrayLength(), laptops.GetProperty("rowVersion").GetInt32()));
            Assert.Equal(JsonValueKind.Null, laptops.GetProperty("updatedAt").ValueKind);
            Assert.Equal([$"dòng{_tag}"], rules.Single(r => r.GetProperty("categoryId").GetGuid() == _phones).GetProperty("includeKeywords").EnumerateArray().Select(x => x.GetString()));
            Assert.DoesNotContain(rules, r => r.GetProperty("categoryId").GetGuid() == _disabledCategory);
        }

        foreach (var (query, status, code) in new[] { ("?siteKey=cms", HttpStatusCode.NotFound, ErrorCodes.NotFound), ("?siteKey=Bad!", HttpStatusCode.BadRequest, ErrorCodes.BadRequest), ("", HttpStatusCode.BadRequest, ErrorCodes.BadRequest) })
        {
            using var bad = await cms.SendAsync(Req(HttpMethod.Get, "/category-rules" + query, _editor));
            await NewsHttp.ProblemAsync(bad, status, code);
        }

        var path = $"/category-rules/{_laptops}";
        using (var missing = await cms.SendAsync(Req(HttpMethod.Patch, path, _editor, json: """{"includeKeywords":["laptop"]}""")))
        {
            await NewsHttp.ProblemAsync(missing, HttpStatusCode.PreconditionRequired, ErrorCodes.PreconditionRequired);
        }

        foreach (var json in new[] { """{"includeKeywords":["laptop","Laptop"]}""", """{"includeKeywords":[" "]}""", """{"categoryId":"x"}""", "{}" })
        {
            using var invalid = await cms.SendAsync(Req(HttpMethod.Patch, path, _editor, json: json, ifMatch: "W/\"1\""));
            await NewsHttp.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        }

        using (var patched = await cms.SendAsync(Req(HttpMethod.Patch, path, _editor, json: """{"includeKeywords":[" laptop ","máy tính xách tay"],"entityKeywords":["MacBook"]}""", ifMatch: "W/\"1\"")))
        {
            var rule = await NewsHttp.JsonAsync(patched, HttpStatusCode.OK);
            Assert.Equal("W/\"2\"", patched.Headers.ETag!.ToString());
            Assert.Equal(["laptop", "máy tính xách tay"], rule.GetProperty("includeKeywords").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal((_editor, 2), (rule.GetProperty("updatedBy").GetGuid(), rule.GetProperty("rowVersion").GetInt32()));
        }

        using (var stale = await cms.SendAsync(Req(HttpMethod.Patch, path, _editor, json: """{"excludeKeywords":["ốp lưng"]}""", ifMatch: "W/\"1\"")))
        {
            await NewsHttp.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
        }

        using (var again = await cms.SendAsync(Req(HttpMethod.Patch, path, _editor, json: """{"excludeKeywords":["ốp lưng"]}""", ifMatch: "W/\"2\"")))
        {
            var rule = await NewsHttp.JsonAsync(again, HttpStatusCode.OK);
            Assert.Equal(("W/\"3\"", 2), (again.Headers.ETag!.ToString(), rule.GetProperty("includeKeywords").GetArrayLength()));   // trường vắng mặt giữ nguyên
        }

        using (var notFound = await cms.SendAsync(Req(HttpMethod.Patch, $"/category-rules/{Guid.NewGuid()}", _editor, json: """{"includeKeywords":["x"]}""", ifMatch: "W/\"1\"")))
        {
            await NewsHttp.ProblemAsync(notFound, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        using (var disabled = await cms.SendAsync(Req(HttpMethod.Patch, $"/category-rules/{_disabledCategory}", _editor, json: """{"includeKeywords":["x"]}""", ifMatch: "W/\"1\"")))
        {
            await NewsHttp.ProblemAsync(disabled, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        }

        using (var approver = await cms.SendAsync(Req(HttpMethod.Patch, path, _approver, StaffRoles.Approver, """{"includeKeywords":["x"]}""", "W/\"3\"")))
        {
            await NewsHttp.ProblemAsync(approver, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);   // APPROVER chỉ đọc
        }
    }

    // ---------------------------------------------------------------- API: mục tin

    [DbFact]
    public async Task Items_are_filtered_by_newsroom_day_window_and_paged()
    {
        var clock = new NewsroomClock(null);
        var today = clock.Today(DateTimeOffset.UtcNow);
        var (dayStart, _) = clock.DayRange(today);
        var (source, feed, _) = await EnabledFeedAsync();
        await ParkAsync(feed);
        string At(double hours) => (dayStart + TimeSpan.FromHours(hours)).ToString("O", CultureInfo.InvariantCulture);

        async Task<Guid> ItemAsync(string title, string? published, string firstSeen, string status = "OUT_OF_SCOPE", string? category = null)
        {
            var id = Guid.NewGuid();
            await ExecAsync($"""
                INSERT INTO tt.agg_items (id, content_source_id, feed_id, guid, url, url_hash, title, summary, feed_categories, language, published_at, first_seen_at,
                                          time_reliable, status, site_key, category_id, category_confidence, content_hash)
                VALUES ('{id}', '{source}', '{feed}', '{id}', 'https://news.example.org/{id}', encode(sha256('{id}'::bytea), 'hex'), '{title}', 'Tóm tắt {title}',
                        ARRAY['Mobile'], 'vi', {(published is null ? "NULL" : $"'{published}'")}, '{firstSeen}', {(published is null ? "false" : "true")}, '{status}', 'www',
                        {(category is null ? "NULL" : $"'{category}'")}, {(category is null ? "NULL" : "0.85")}, repeat('c', 64))
                """);
            return id;
        }

        var x1 = await ItemAsync("Một", At(9), At(9.02), "CLASSIFIED", _phones.ToString());
        var x2 = await ItemAsync("Hai", At(-1), At(-0.98));                                                 // hôm qua
        var x3 = await ItemAsync("Ba", null, At(10));                                                       // không có thời điểm → lúc thấy
        var x4 = await ItemAsync("Bốn", "2019-05-01T00:00:00Z", At(8));                                     // TC-AGG-06: năm cũ → không phải hôm nay
        var x5 = await ItemAsync("Năm", (dayStart + TimeSpan.FromDays(3)).ToString("O", CultureInfo.InvariantCulture), At(11));   // TC-AGG-07: tương lai → lúc thấy
        using var cms = Cms();
        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        async Task<(List<Guid> Ids, string? Next, JsonElement Body)> GetAsync(string query)
        {
            using var response = await cms.SendAsync(Req(HttpMethod.Get, $"/items?siteKey=www&sourceId={source}&" + query, _editor));
            var body = await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
            var next = body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : body.GetProperty("nextCursor").GetString();
            return (body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList(), next, body);
        }

        var page1 = await GetAsync($"date={date}&limit=2");
        Assert.Equal([x5, x3], page1.Ids);                         // mới thấy trước
        var page2 = await GetAsync($"date={date}&limit=2&cursor={Uri.EscapeDataString(page1.Next!)}");
        Assert.Equal([x1], page2.Ids);
        Assert.Null(page2.Next);

        var first = page2.Body.GetProperty("items")[0];
        Assert.Equal(($"Nguồn {source:N}", "Tóm tắt Một", "CLASSIFIED", _phones, 0.85m, 1, true), (first.GetProperty("sourceName").GetString(),
            first.GetProperty("summary").GetString(), first.GetProperty("status").GetString(), first.GetProperty("categoryId").GetGuid(),
            first.GetProperty("confidence").GetDecimal(), first.GetProperty("version").GetInt32(), first.GetProperty("timeReliable").GetBoolean()));
        Assert.Equal(["Mobile"], first.GetProperty("feedCategories").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal([x2], (await GetAsync($"date={today.AddDays(-1):yyyy-MM-dd}")).Ids);
        Assert.Equal([x1], (await GetAsync($"date={date}&status=CLASSIFIED")).Ids);
        Assert.Equal([x1], (await GetAsync($"date={date}&categoryId={_phones}")).Ids);
        Assert.Equal([x5, x3], (await GetAsync($"date={date}&status=OUT_OF_SCOPE&status=DUPLICATE")).Ids);
        Assert.Equal([x3], (await GetAsync($"date={date}&q=ba")).Ids);   // tiêu đề/tóm tắt, không phân biệt hoa thường
        Assert.DoesNotContain(x4, (await GetAsync($"date={date}")).Ids);
        Assert.Equal([x4], (await GetAsync("date=2019-05-01")).Ids);

        // LAST_24H: mục thấy 1 giờ trước, chưa có thời điểm (BR-12).
        var recent = await ItemAsync("Gần đây", null, DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture));
        Assert.Contains(recent, (await GetAsync("window=LAST_24H")).Ids);
        Assert.DoesNotContain(x4, (await GetAsync("window=LAST_24H")).Ids);

        // Cursor gắn bộ lọc: đổi ngày → INVALID_CURSOR.
        using (var other = await cms.SendAsync(Req(HttpMethod.Get, $"/items?siteKey=www&sourceId={source}&date={today.AddDays(-1):yyyy-MM-dd}&limit=2&cursor={Uri.EscapeDataString(page1.Next!)}", _editor)))
        {
            await NewsHttp.ProblemAsync(other, HttpStatusCode.BadRequest, ErrorCodes.InvalidCursor);
        }

        foreach (var (query, status, code) in new[]
        {
            ("siteKey=www&date=2026-13-01", HttpStatusCode.BadRequest, ErrorCodes.BadRequest),
            ("siteKey=www&window=WEEK", HttpStatusCode.BadRequest, ErrorCodes.BadRequest),
            ("siteKey=www&status=LIVE", HttpStatusCode.BadRequest, ErrorCodes.BadRequest),
            ("siteKey=www&sourceId=abc", HttpStatusCode.BadRequest, ErrorCodes.BadRequest),
            ("siteKey=cms", HttpStatusCode.NotFound, ErrorCodes.NotFound),
            ("date=2026-11-10", HttpStatusCode.BadRequest, ErrorCodes.BadRequest),
        })
        {
            using var bad = await cms.SendAsync(Req(HttpMethod.Get, "/items?" + query, _editor));
            await NewsHttp.ProblemAsync(bad, status, code);
        }

        using (var author = await cms.SendAsync(Req(HttpMethod.Get, "/items?siteKey=www", Guid.NewGuid(), StaffRoles.Author)))
        {
            await NewsHttp.ProblemAsync(author, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }
    }
}

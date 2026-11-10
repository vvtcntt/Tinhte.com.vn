using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Platform.Ai;
using Tinhte.Platform.Data;

namespace Tinhte.Api.Tests;

/// <summary>
/// Kết quả chốt của chủ dự án ngày 10/11/2026 (BA-01 phiếu xin quyết định): P-02 chu kỳ 30 phút, chỉ tin trong ngày, AI trùng chủ đề tắt mặc định;
/// L-03 giữ mục lẻ 90 ngày; E-01 ba nhóm chủ đề nhạy cảm mới.
/// </summary>
public sealed class AggregationOwnerDecisionsTests
{
    [Fact]
    public void Defaults_follow_the_owner_decisions()
    {
        var options = AggregationOptions.From(new ConfigurationBuilder().Build());
        Assert.Equal((true, 90, 30), (options.TodayOnly, options.UnclusteredItemRetentionDays, AggregationLimits.DefaultScheduleMinutes));

        var custom = AggregationOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aggregation:Intake:TodayOnly"] = "false",
            ["Aggregation:UnclusteredItemRetentionDays"] = "120",
        }).Build());
        Assert.Equal((false, 120), (custom.TodayOnly, custom.UnclusteredItemRetentionDays));
        Assert.Throws<InvalidOperationException>(() => AggregationOptions.From(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Aggregation:UnclusteredItemRetentionDays"] = "3" }).Build()));
    }

    [Fact]
    public void Intake_cutoff_is_midnight_in_the_newsroom_time_zone()
    {
        var clock = new NewsroomClock("Asia/Ho_Chi_Minh");
        var morning = new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero);        // 08:00 giờ Việt Nam
        var midnight = new DateTimeOffset(2026, 11, 9, 17, 0, 0, TimeSpan.Zero);       // 00:00 ngày 10/11 giờ Việt Nam
        Assert.Equal(midnight, ItemNormalizer.IntakeCutoff(morning, firstFetch: true, todayOnly: true, clock));
        Assert.Equal(midnight, ItemNormalizer.IntakeCutoff(morning, firstFetch: false, todayOnly: true, clock));   // mọi lần đọc, không chỉ lần đầu

        var lateUtc = new DateTimeOffset(2026, 11, 10, 18, 30, 0, TimeSpan.Zero);      // 01:30 ngày 11/11 giờ Việt Nam
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 17, 0, 0, TimeSpan.Zero), ItemNormalizer.IntakeCutoff(lateUtc, false, true, clock));

        // Tắt quy tắc ⇒ BR-14 cũ.
        Assert.Equal(morning.AddHours(-24), ItemNormalizer.IntakeCutoff(morning, firstFetch: true, todayOnly: false, clock));
        Assert.Null(ItemNormalizer.IntakeCutoff(morning, firstFetch: false, todayOnly: false, clock));
    }

    [Fact]
    public async Task Default_same_event_judge_never_calls_ai()
    {
        ISameEventJudge judge = new NoAiSameEventJudge();
        Assert.False(judge.UsesAi);
        var verdict = await judge.JudgeAsync(new SameEventCandidate(Guid.NewGuid(), "A", null, "vi"), new SameEventCandidate(Guid.NewGuid(), "A", null, "en"),
            CancellationToken.None);
        Assert.Equal(SameEventVerdict.Unknown, verdict.Verdict);
        Assert.Null(verdict.Provider);
    }

    [Theory]
    [InlineData("Công an khởi tố vụ án lừa đảo qua ứng dụng", true)]
    [InlineData("Police make arrests in crypto fraud charges", true)]
    [InlineData("Bùng phát dịch sởi ở nhiều tỉnh", true)]
    [InlineData("WHO warns of new outbreak", true)]
    [InlineData("Ứng dụng giúp bảo vệ trẻ em trên mạng", true)]
    [InlineData("New child safety rules for app stores", true)]
    [InlineData("Dùng thử miễn phí 30 ngày: free trial cho học sinh", false)]   // từ thông dụng của tin công nghệ không bị gắn nhạy cảm
    public void Sensitive_topics_include_the_three_new_groups(string text, bool sensitive)
    {
        var topics = SensitiveTopics.Load(RepoPaths.File("config", "aggregation", "sensitive-topics.json"));
        Assert.Equal(sensitive, topics.Matches("www", " " + TextNormalizer.Fold(text) + " "));
    }
}

/// <summary>Trên PostgreSQL: chỉ nhận tin trong ngày; gom cụm chỉ trong cùng chuyên mục; xóa mục lẻ sau 90 ngày.</summary>
public sealed partial class AggregationCollectionDbTests
{
    [DbFact]
    public async Task Worker_only_takes_items_of_the_newsroom_day()   // BA-01 P-02 (thay BR-14)
    {
        var (source, feed, url) = await EnabledFeedAsync();
        try
        {
            var clock = new NewsroomClock(null);
            var now = DateTimeOffset.UtcNow;
            var midnight = clock.DayRange(clock.Today(now)).FromUtc;
            var handler = new FakeFeedHandler().OnFeed(url, FeedFixtures.Rss(
                ("today", $"https://news.example.org/{_tag}/today", $"Tin hôm nay {_tag}", "x", "Mobile", now),
                ("yesterday", $"https://news.example.org/{_tag}/yesterday", $"Tin hôm qua {_tag}", "x", "Mobile", midnight.AddHours(-1))));
            var collector = Collector(handler, todayOnly: true);
            var first = await RunAsync(collector, feed);
            Assert.Equal((2, 1), (first.ItemsSeen, first.ItemsNew));

            // Lần đọc sau (không còn "lần đầu") vẫn bỏ tin trước 00:00 hôm nay.
            handler.OnFeed(url, FeedFixtures.Rss(
                ("yesterday-2", $"https://news.example.org/{_tag}/yesterday-2", $"Tin hôm qua nữa {_tag}", "x", "Mobile", midnight.AddMinutes(-5))));
            Assert.Equal(0, (await RunAsync(collector, feed)).ItemsNew);
            Assert.Equal("today", await db.ScalarAsync<string>($"SELECT string_agg(guid, ',') FROM tt.agg_items WHERE content_source_id = '{source}'"));
        }
        finally
        {
            await ParkAsync(feed);
        }
    }

    [DbFact]
    public async Task Clustering_compares_items_only_within_a_shared_category()   // BA-01 P-02
    {
        var mappings = new[]
        {
            $$"""[{"feedCategory":"Mobile","categoryId":"{{_phones}}"}]""",
            $$"""[{"feedCategory":"Laptop","categoryId":"{{_laptops}}"}]""",
            $$"""[{"feedCategory":"Mobile","categoryId":"{{_phones}}"},{"feedCategory":"Laptop","categoryId":"{{_laptops}}"}]""",
        };
        var title = $"{W("cầu")} {W("phố")} khai trương {W("giá")} {W("màu")} {W("pin")}";
        var handler = new FakeFeedHandler();
        var collector = Collector(handler);
        var (sources, feeds) = (new Guid[3], new Guid[3]);
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var (source, feed, url) = await EnabledFeedAsync(mappings[i], minutesAfter2000: i);
                (sources[i], feeds[i]) = (source, feed);
                var rss = FeedFixtures.Rss(($"c{i}", $"https://news.example.org/{_tag}/cat-{i}", title, W("tóm"), i == 1 ? "Laptop" : "Mobile", Ago(1)));
                // Nguồn 2: mục mang hai category của feed ⇒ thuộc cả Điện thoại và Laptop.
                if (i == 2) rss = rss.Replace("<category>Mobile</category>", "<category>Mobile</category><category>Laptop</category>", StringComparison.Ordinal);
                handler.OnFeed(url, rss);
            }

            // Nguồn 0 (Điện thoại) và nguồn 1 (Laptop) cùng tiêu đề nhưng khác chuyên mục ⇒ hai cụm.
            await RunAsync(collector, feeds[0]);
            await RunAsync(collector, feeds[1]);
            var phones = await ClusterOfAsync(sources[0]);
            var laptops = await ClusterOfAsync(sources[1]);
            Assert.NotEqual(phones, laptops);

            await RunAsync(collector, feeds[2]);
            Assert.Equal($"{{{_phones},{_laptops}}}", await db.ScalarAsync<string>(
                $"SELECT category_ids::text FROM tt.agg_items WHERE content_source_id = '{sources[2]}'"));
            Assert.Contains(await ClusterOfAsync(sources[2]), new[] { phones, laptops });   // vào một trong hai cụm, không mở cụm thứ ba
        }
        finally
        {
            await ParkAsync(feeds);
        }
    }

    private async Task<Guid> ClusterOfAsync(Guid source) =>
        (await db.ScalarAsync<Guid?>($"SELECT cluster_id FROM tt.agg_items WHERE content_source_id = '{source}'"))!.Value;

    [DbFact]
    public async Task Unclustered_items_are_purged_after_the_retention_period()   // BA-01 L-03
    {
        var (source, feed, _) = await EnabledFeedAsync();
        await ParkAsync(feed);
        var (oldLoose, recentLoose, oldClustered, oldRepresentative, cluster, repCluster) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string Item(Guid id, int days, Guid? inCluster) => $"""
            INSERT INTO tt.agg_items (id, content_source_id, feed_id, url, url_hash, title, status, site_key, content_hash, first_seen_at, cluster_id)
              VALUES ('{id}', '{source}', '{feed}', 'https://news.example.org/{_tag}/{id}', encode(sha256('{id}'::bytea), 'hex'), 'Tin {_tag}',
                      '{(inCluster is null ? "OUT_OF_SCOPE" : "CLUSTERED")}', 'www', repeat('c', 64), now() - interval '{days} days',
                      {(inCluster is null ? "NULL" : $"'{inCluster}'")});
            """;
        await ExecAsync($"""
            INSERT INTO tt.agg_clusters (id, site_key, title, status) VALUES ('{cluster}', 'www', 'Cụm {_tag}', 'OPEN'), ('{repCluster}', 'www', 'Cụm 2 {_tag}', 'OPEN');
            {Item(oldLoose, 100, null)} {Item(recentLoose, 10, null)} {Item(oldClustered, 100, cluster)} {Item(oldRepresentative, 100, null)}
            UPDATE tt.agg_clusters SET representative_item_id = '{oldRepresentative}' WHERE id = '{repCluster}';
            """);

        Assert.True(await Operations(new FakeAlertSink()).PurgeUnclusteredItemsAsync(CancellationToken.None) >= 1);
        Assert.Equal(string.Join(',', new[] { recentLoose, oldClustered, oldRepresentative }.Select(g => g.ToString()).Order(StringComparer.Ordinal)),
            await db.ScalarAsync<string>($"SELECT string_agg(id::text, ',' ORDER BY id::text) FROM tt.agg_items WHERE content_source_id = '{source}'"));
    }
}

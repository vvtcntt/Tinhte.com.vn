using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>
/// Radar tin GĐ1 lát 3 trên PostgreSQL: worker gom cụm liên nguồn (MinHash + thực thể chung), bảo trì cụm, API Radar cụm + thao tác cụm, thống kê.
/// Dùng chung hạ tầng của <see cref="AggregationCollectionDbTests"/> (chuyên mục _phones có luật: bao gồm "dòng{tag}", thực thể "Model{tag}").
/// </summary>
public sealed partial class AggregationCollectionDbTests
{
    // Từ riêng theo từng test (tag): tin của các test khác — cùng site www, cùng cửa sổ 48 giờ — không giống tới mức bị gom chung.
    private string W(string word) => word + _tag;

    private string Story(int variant) => variant switch
    {
        0 => $"Model{_tag} ra mắt tại {W("phố")} với {W("giá")} và {W("màu")}",
        1 => $"Model{_tag} ra mắt {W("phố")}, {W("giá")} {W("màu")}",
        2 => $"{W("giá")} của Model{_tag} ra mắt tại {W("phố")} và {W("màu")}",
        _ => $"Model{_tag} bán tại {W("phố")} với {W("giá")}",
    };

    private string StorySummary(int variant) => variant switch
    {
        0 => $"Model{_tag} bán từ tuần sau, {W("pin")} lớn",
        1 => $"Model{_tag} có {W("pin")} lớn, bán từ tuần sau",
        _ => $"Model{_tag} bán từ tuần sau, {W("màu")} và {W("pin")} lớn",
    };

    private sealed record StorySetup(Guid Story, Guid Lite, Guid[] Sources, Guid[] Feeds, FakeFeedHandler Handler, Modules.Aggregation.Application.FeedCollectionService Collector);

    /// <summary>Ba nguồn cùng đưa một sự kiện (+ nguồn đầu có thêm một tin khác) → worker thu thập và gom cụm.</summary>
    private async Task<StorySetup> StoryAsync()
    {
        var handler = new FakeFeedHandler();
        var sources = new Guid[3];
        var feeds = new Guid[3];
        for (var i = 0; i < 3; i++)
        {
            var (source, feed, url) = await EnabledFeedAsync(minutesAfter2000: i);
            (sources[i], feeds[i]) = (source, feed);
            var items = new List<(string, string, string, string, string, DateTimeOffset)>
            {
                ($"s{i}", $"https://news.example.org/{_tag}/story-{i}", Story(i), StorySummary(i), "Mobile", Ago(3 - i)),
            };
            if (i == 0) items.Add(("lite", $"https://news.example.org/{_tag}/lite", $"dòng{_tag} {W("lite")} {W("xả")} {W("kho")}", W("khuyến"), "Mobile", Ago(2)));
            handler.OnFeed(url, FeedFixtures.Rss([.. items]));
        }

        var collector = Collector(handler);
        foreach (var feed in feeds) Assert.True((await RunAsync(collector, feed)).Committed);
        await ParkAsync(feeds);
        var story = (await db.ScalarAsync<Guid?>($"SELECT cluster_id FROM tt.agg_items WHERE content_source_id = '{sources[0]}' AND guid = 's0'"))!.Value;
        var lite = (await db.ScalarAsync<Guid?>($"SELECT cluster_id FROM tt.agg_items WHERE content_source_id = '{sources[0]}' AND guid = 'lite'"))!.Value;
        return new StorySetup(story, lite, sources, feeds, handler, collector);
    }

    // ---------------------------------------------------------------- worker gom cụm

    [DbFact]
    public async Task Worker_clusters_the_same_story_across_independent_sources()   // TC-AGG-10
    {
        var s = await StoryAsync();
        Assert.NotEqual(s.Story, s.Lite);
        Assert.Equal(3L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_items WHERE cluster_id = '{s.Story}' AND status = 'CLUSTERED'"));
        Assert.Equal($"OPEN|3|3|3|{_phones}|true|1|{{}}", await db.ScalarAsync<string>($"""
            SELECT status || '|' || item_count || '|' || feed_count || '|' || source_count || '|' || primary_category_id || '|' || category_needs_review
                   || '|' || row_version || '|' || labels::text
              FROM tt.agg_clusters WHERE id = '{s.Story}'
            """));   // 3 nguồn độc lập; worker không đổi row_version (0343)
        Assert.Equal("{SINGLE_SOURCE}", await db.ScalarAsync<string>($"SELECT labels::text FROM tt.agg_clusters WHERE id = '{s.Lite}'"));
        Assert.True(await db.ScalarAsync<bool>($"SELECT (SELECT score FROM tt.agg_clusters WHERE id = '{s.Story}') > (SELECT score FROM tt.agg_clusters WHERE id = '{s.Lite}')"));
        Assert.Equal(Story(0), await db.ScalarAsync<string>($"SELECT title FROM tt.agg_clusters WHERE id = '{s.Story}'"));   // cùng uy tín → mục sớm nhất

        // Nguồn sửa bài (phiên bản 2) → nhãn SOURCE_UPDATED; mục mới không đổi ETag của BTV.
        s.Handler.OnFeed(await db.ScalarAsync<string>($"SELECT url FROM tt.agg_feeds WHERE id = '{s.Feeds[1]}'") ?? "",
            FeedFixtures.Rss(($"s1", $"https://news.example.org/{_tag}/story-1", Story(1) + " cho bản 256GB", StorySummary(1), "Mobile", Ago(2))));
        await RunAsync(s.Collector, s.Feeds[1]);
        await ParkAsync(s.Feeds);
        Assert.Equal("{SOURCE_UPDATED}|1", await db.ScalarAsync<string>($"SELECT labels::text || '|' || row_version FROM tt.agg_clusters WHERE id = '{s.Story}'"));
    }

    [DbFact]
    public async Task Maintenance_closes_stale_clusters_and_clusters_leftover_items()
    {
        var (source, feed, _) = await EnabledFeedAsync();
        await ParkAsync(feed);
        var (stale, claimed, leftover) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecAsync($"""
            INSERT INTO tt.agg_clusters (id, site_key, title, status, first_seen_at, last_item_at) VALUES
              ('{stale}', 'www', 'Cũ {_tag}', 'OPEN', now() - interval '60 hours', now() - interval '49 hours');
            INSERT INTO tt.agg_clusters (id, site_key, title, status, first_seen_at, last_item_at, assignee_id) VALUES
              ('{claimed}', 'www', 'Đã nhận {_tag}', 'OPEN', now() - interval '60 hours', now() - interval '49 hours', '{_editor}');
            INSERT INTO tt.agg_items (id, content_source_id, feed_id, url, url_hash, title, summary, status, site_key, category_id, category_confidence, content_hash, published_at)
              VALUES ('{leftover}', '{source}', '{feed}', 'https://news.example.org/{_tag}/left', encode(sha256('{leftover}'::bytea), 'hex'), '{Story(3)}',
                      '{StorySummary(2)}', 'CLASSIFIED', 'www', '{_phones}', 0.7, repeat('c', 64), now() - interval '1 hour');
            """);
        await Collector(new FakeFeedHandler()).MaintainAsync(CancellationToken.None);
        Assert.Equal("CLOSED|OPEN", await db.ScalarAsync<string>($"""
            SELECT (SELECT status FROM tt.agg_clusters WHERE id = '{stale}') || '|' || (SELECT status FROM tt.agg_clusters WHERE id = '{claimed}')
            """));   // hết cửa sổ 48 giờ, chưa ai nhận → CLOSED; đã có người nhận thì giữ
        Assert.Equal("CLUSTERED|t", await db.ScalarAsync<string>(
            $"SELECT status || '|' || (cluster_id IS NOT NULL AND minhash IS NOT NULL)::text::char FROM tt.agg_items WHERE id = '{leftover}'"));
    }

    // ---------------------------------------------------------------- API Radar cụm

    private async Task<(JsonElement Body, string ETag)> ClusterCallAsync(HttpClient cms, HttpMethod method, string path, Guid user, string? json = null,
        string? ifMatch = null, HttpStatusCode expected = HttpStatusCode.OK, string? idempotencyKey = null)
    {
        using var response = await cms.SendAsync(NewsHttp.Admin(method, Base + path, user, StaffRoles.Editor, false, json, ifMatch, idempotencyKey));
        var body = await NewsHttp.JsonAsync(response, expected);
        return (body, response.Headers.ETag?.ToString() ?? "");
    }

    private async Task ClusterProblemAsync(HttpClient cms, HttpMethod method, string path, Guid user, HttpStatusCode status, string code, string? json = null,
        string? ifMatch = null, string roles = StaffRoles.Editor)
    {
        var key = path.EndsWith("/split", StringComparison.Ordinal) ? "split-" + Guid.NewGuid() : null;   // split bắt buộc Idempotency-Key
        using var response = await cms.SendAsync(NewsHttp.Admin(method, Base + path, user, roles, false, json, ifMatch, key));
        await NewsHttp.ProblemAsync(response, status, code);
    }

    [DbFact]
    public async Task Radar_lists_clusters_by_score_and_shows_detail()
    {
        var s = await StoryAsync();
        using var cms = Cms();
        // LAST_24H thay vì "hôm nay": tin của test có thể rơi vào hôm qua khi chạy gần nửa đêm giờ tòa soạn.
        const string window = "window=LAST_24H";
        var (list, _) = await ClusterCallAsync(cms, HttpMethod.Get, $"/clusters?siteKey=www&{window}&categoryId={_phones}&limit=100", _editor);
        var ids = list.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.True(ids.IndexOf(s.Story) >= 0 && ids.IndexOf(s.Story) < ids.IndexOf(s.Lite));   // điểm cao trước
        var summary = list.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == s.Story);
        Assert.Equal(("OPEN", 3, 3, 3, "M1", true), (summary.GetProperty("status").GetString(), summary.GetProperty("sourceCount").GetInt32(),
            summary.GetProperty("independentSourceCount").GetInt32(), summary.GetProperty("itemCount").GetInt32(), summary.GetProperty("maxUsageMode").GetString(),
            summary.GetProperty("categoryNeedsReview").GetBoolean()));
        Assert.InRange(summary.GetProperty("score").GetDecimal(), 0m, 100m);

        var (paged, _) = await ClusterCallAsync(cms, HttpMethod.Get, $"/clusters?siteKey=www&{window}&categoryId={_phones}&limit=1", _editor);
        var next = paged.GetProperty("nextCursor").GetString()!;
        var (page2, _) = await ClusterCallAsync(cms, HttpMethod.Get, $"/clusters?siteKey=www&{window}&categoryId={_phones}&limit=100&cursor={Uri.EscapeDataString(next)}", _editor);
        Assert.DoesNotContain(page2.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetGuid() == paged.GetProperty("items")[0].GetProperty("id").GetGuid());

        var storyScore = summary.GetProperty("score").GetDecimal();
        var (high, _) = await ClusterCallAsync(cms, HttpMethod.Get, $"/clusters?siteKey=www&window=LAST_24H&categoryId={_phones}&minScore={storyScore.ToString(CultureInfo.InvariantCulture)}", _editor);
        Assert.DoesNotContain(high.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetGuid() == s.Lite);
        var (closed, _) = await ClusterCallAsync(cms, HttpMethod.Get, $"/clusters?siteKey=www&{window}&categoryId={_phones}&status=CLOSED", _editor);
        Assert.Empty(closed.GetProperty("items").EnumerateArray());

        var (detail, etag) = await ClusterCallAsync(cms, HttpMethod.Get, $"/clusters/{s.Story}", _editor);
        Assert.Equal("W/\"1\"", etag);
        var items = detail.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(Enumerable.Range(0, 3).Select(i => $"https://news.example.org/{_tag}/story-{i}"), items.Select(i => i.GetProperty("url").GetString()));   // theo thời gian
        Assert.Empty(detail.GetProperty("actions").EnumerateArray());

        foreach (var (query, status, code) in new[]
        {
            ("siteKey=cms", HttpStatusCode.NotFound, ErrorCodes.NotFound),
            ("siteKey=www&status=DRAFTING", HttpStatusCode.BadRequest, ErrorCodes.BadRequest),
            ("siteKey=www&minScore=101", HttpStatusCode.BadRequest, ErrorCodes.BadRequest),
            ("siteKey=www&window=WEEK", HttpStatusCode.BadRequest, ErrorCodes.BadRequest),
        })
        {
            await ClusterProblemAsync(cms, HttpMethod.Get, "/clusters?" + query, _editor, status, code);
        }

        await ClusterProblemAsync(cms, HttpMethod.Get, $"/clusters/{Guid.NewGuid()}", _editor, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        await ClusterProblemAsync(cms, HttpMethod.Get, "/clusters?siteKey=www", Guid.NewGuid(), HttpStatusCode.Forbidden, ErrorCodes.Forbidden, roles: StaffRoles.Author);
    }

    [DbFact]
    public async Task Cluster_actions_follow_the_lifecycle_and_keep_history()
    {
        var s = await StoryAsync();
        using var cms = Cms();
        var other = Guid.NewGuid();
        await ExecAsync($"INSERT INTO tt.users (id, display_name) VALUES ('{other}', 'BTV khác')");
        var path = $"/clusters/{s.Story}";

        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/watch", _editor, HttpStatusCode.PreconditionRequired, ErrorCodes.PreconditionRequired);
        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/watch", _editor, HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict, ifMatch: "W/\"9\"");
        await ClusterProblemAsync(cms, HttpMethod.Post, $"/clusters/{Guid.NewGuid()}/watch", _editor, HttpStatusCode.NotFound, ErrorCodes.NotFound, ifMatch: "W/\"1\"");

        var (watched, e2) = await ClusterCallAsync(cms, HttpMethod.Post, path + "/watch", _editor, ifMatch: "W/\"1\"");
        Assert.Equal(("WATCHING", "W/\"2\""), (watched.GetProperty("status").GetString(), e2));
        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/watch", _editor, HttpStatusCode.Conflict, ErrorCodes.StateConflict, ifMatch: e2);
        var (_, e3) = await ClusterCallAsync(cms, HttpMethod.Post, path + "/reopen", _editor, ifMatch: e2);

        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/dismiss", _editor, HttpStatusCode.UnprocessableEntity, ErrorCodes.ReasonRequired, """{"reason":"x"}""", e3);
        var (dismissed, e4) = await ClusterCallAsync(cms, HttpMethod.Post, path + "/dismiss", _editor, """{"reason":"Tin quảng cáo, không đưa"}""", e3);
        Assert.Equal("DISMISSED", dismissed.GetProperty("status").GetString());
        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/dismiss", _editor, HttpStatusCode.Conflict, ErrorCodes.StateConflict, """{"reason":"Lần nữa nhé"}""", e4);
        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/claim", _editor, HttpStatusCode.Conflict, ErrorCodes.StateConflict, ifMatch: e4);
        var (_, e5) = await ClusterCallAsync(cms, HttpMethod.Post, path + "/reopen", _editor, ifMatch: e4);

        // Nhận việc: người khác đã nhận → 409; chính mình nhận lại → không đổi.
        var (claimed, e6) = await ClusterCallAsync(cms, HttpMethod.Post, path + "/claim", _editor, ifMatch: e5);
        Assert.Equal(_editor, claimed.GetProperty("assigneeId").GetGuid());
        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/claim", other, HttpStatusCode.Conflict, ErrorCodes.StateConflict, ifMatch: e6);
        var (_, e6b) = await ClusterCallAsync(cms, HttpMethod.Post, path + "/claim", _editor, ifMatch: e6);
        Assert.Equal(e6, e6b);

        // Đã đưa tin: bài phải tồn tại (422); sau khi gắn, mục mới của sự kiện → NEW_SINCE_COVERED (BR-22).
        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/coverage", _editor, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed,
            $$"""{"articleId":"{{Guid.NewGuid()}}"}""", e6);
        var article = Guid.NewGuid();
        await ExecAsync($"INSERT INTO tt.articles (id, slug) VALUES ('{article}', 'bai-{_tag}')");
        var (covered, e7) = await ClusterCallAsync(cms, HttpMethod.Post, path + "/coverage", _editor, $$"""{"articleId":"{{article}}"}""", e6);
        Assert.Equal(("COVERED", article), (covered.GetProperty("status").GetString(), covered.GetProperty("coveredArticleId").GetGuid()));
        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/coverage", _editor, HttpStatusCode.Conflict, ErrorCodes.StateConflict, $$"""{"articleId":"{{article}}"}""", e7);

        var (fourth, fourthFeed, fourthUrl) = await EnabledFeedAsync();
        s.Handler.OnFeed(fourthUrl, FeedFixtures.Rss(("s3", $"https://news.example.org/{_tag}/story-3", Story(3), StorySummary(0), "Mobile", Ago(0.1))));
        await RunAsync(s.Collector, fourthFeed);
        await ParkAsync(fourthFeed);
        var (afterNew, e7b) = await ClusterCallAsync(cms, HttpMethod.Get, path, _editor);
        Assert.Contains("NEW_SINCE_COVERED", afterNew.GetProperty("labels").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal((4, e7), (afterNew.GetProperty("itemCount").GetInt32(), e7b));   // worker thêm mục không đổi ETag
        var fourthItem = (await db.ScalarAsync<Guid?>($"SELECT id FROM tt.agg_items WHERE content_source_id = '{fourth}'"))!.Value;

        // Tách: mục lạ / tách hết → 422; tách hợp lệ → 201 cụm mới (OPEN), an toàn khi gửi lại.
        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/split", _editor, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed,
            $$"""{"itemIds":["{{Guid.NewGuid()}}"]}""", e7);
        var allIds = afterNew.GetProperty("items").EnumerateArray().Select(i => $"\"{i.GetProperty("id").GetGuid()}\"");
        await ClusterProblemAsync(cms, HttpMethod.Post, path + "/split", _editor, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed,
            $$"""{"itemIds":[{{string.Join(',', allIds)}}]}""", e7);
        var key = "split-" + Guid.NewGuid();
        var json = $$"""{"itemIds":["{{fourthItem}}"],"reason":"Bài đánh giá, không phải tin ra mắt"}""";
        var (split, splitEtag) = await ClusterCallAsync(cms, HttpMethod.Post, path + "/split", _editor, json, e7, HttpStatusCode.Created, key);
        var created = split.GetProperty("id").GetGuid();
        Assert.Equal(("OPEN", 1, "W/\"1\""), (split.GetProperty("status").GetString(), split.GetProperty("itemCount").GetInt32(), splitEtag));
        Assert.Equal(("SPLIT", s.Story), (split.GetProperty("actions")[0].GetProperty("action").GetString(), split.GetProperty("actions")[0].GetProperty("relatedClusterId").GetGuid()));
        var (replay, _) = await ClusterCallAsync(cms, HttpMethod.Post, path + "/split", _editor, json, e7, HttpStatusCode.Created, key);
        Assert.Equal(created, replay.GetProperty("id").GetGuid());
        Assert.Equal(3, (await ClusterCallAsync(cms, HttpMethod.Get, path, _editor)).Body.GetProperty("itemCount").GetInt32());

        // Gộp cụm mới trở lại: vào chính nó / cụm không tồn tại → 422; gộp xong cụm nguồn CLOSED, trả cụm đích.
        var newPath = $"/clusters/{created}";
        await ClusterProblemAsync(cms, HttpMethod.Post, newPath + "/merge", _editor, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed,
            $$"""{"targetClusterId":"{{created}}"}""", splitEtag);
        await ClusterProblemAsync(cms, HttpMethod.Post, newPath + "/merge", _editor, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed,
            $$"""{"targetClusterId":"{{Guid.NewGuid()}}"}""", splitEtag);
        var (merged, mergedEtag) = await ClusterCallAsync(cms, HttpMethod.Post, newPath + "/merge", _editor, $$"""{"targetClusterId":"{{s.Story}}","reason":"Tách nhầm"}""", splitEtag);
        Assert.Equal((s.Story, 4, e7), (merged.GetProperty("id").GetGuid(), merged.GetProperty("itemCount").GetInt32(), mergedEtag));
        var (source, sourceEtag) = await ClusterCallAsync(cms, HttpMethod.Get, newPath, _editor);
        Assert.Equal(("CLOSED", 0), (source.GetProperty("status").GetString(), source.GetProperty("items").GetArrayLength()));
        await ClusterProblemAsync(cms, HttpMethod.Post, newPath + "/merge", _editor, HttpStatusCode.Conflict, ErrorCodes.StateConflict,
            $$"""{"targetClusterId":"{{s.Story}}"}""", sourceEtag);

        Assert.Equal("MERGE,SPLIT,COVER,CLAIM,REOPEN,DISMISS,REOPEN,WATCH", string.Join(',',
            merged.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("action").GetString())));
        Assert.Equal("Tin quảng cáo, không đưa", merged.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("action").GetString() == "DISMISS").GetProperty("reason").GetString());

        await SetFlagAsync(false);
        try
        {
            await ClusterProblemAsync(cms, HttpMethod.Get, path, _editor, HttpStatusCode.Forbidden, ErrorCodes.FeatureDisabled);
        }
        finally
        {
            await SetFlagAsync(true);
        }
    }

    [DbFact]
    public async Task Stats_report_the_day_and_source_health()
    {
        var s = await StoryAsync();
        using var cms = Cms();
        var clock = new NewsroomClock(null);
        var today = clock.Today(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (stats, _) = await ClusterCallAsync(cms, HttpMethod.Get, $"/stats?siteKey=www&date={today}", _editor);
        Assert.Equal(("www", today, "Asia/Ho_Chi_Minh"), (stats.GetProperty("siteKey").GetString(), stats.GetProperty("date").GetString(), stats.GetProperty("timeZone").GetString()));
        Assert.True(stats.GetProperty("itemsToday").GetInt32() >= 4 && stats.GetProperty("clustersToday").GetInt32() >= 2);
        Assert.InRange(stats.GetProperty("fetchSuccessRate24h").GetDouble(), 0, 1);
        Assert.Equal(["DEGRADED", "ENABLED", "PAUSED", "PROPOSED", "RETIRED"], stats.GetProperty("sourcesByStatus").EnumerateObject().Select(p => p.Name).Order());
        var first = stats.GetProperty("sources").EnumerateArray().Single(x => x.GetProperty("sourceId").GetGuid() == s.Sources[0]);
        Assert.Equal(("ENABLED", "OK", true), (first.GetProperty("status").GetString(), first.GetProperty("worstFeedHealth").GetString(),
            first.GetProperty("lastSuccessAt").ValueKind == JsonValueKind.String));
        // Mục của nguồn đầu "hôm nay" (giờ tòa soạn) — 2 mục nếu chúng không rơi vào hôm qua lúc gần nửa đêm.
        var expected = await db.ScalarAsync<int>($"""
            SELECT count(*)::int FROM tt.agg_items i WHERE i.content_source_id = '{s.Sources[0]}'
               AND (CASE WHEN i.published_at <= i.first_seen_at + interval '10 minutes' THEN i.published_at ELSE i.first_seen_at END) >= '{clock.DayRange(clock.Today(DateTimeOffset.UtcNow)).FromUtc:O}'
            """);
        Assert.Equal(expected, first.GetProperty("itemsToday").GetInt32());

        await ClusterProblemAsync(cms, HttpMethod.Get, "/stats?siteKey=www&date=10/11/2026", _editor, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
        await ClusterProblemAsync(cms, HttpMethod.Get, "/stats?siteKey=media", _editor, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }
}

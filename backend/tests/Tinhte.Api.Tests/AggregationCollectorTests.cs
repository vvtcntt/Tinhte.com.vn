using System.Net;
using System.Net.Sockets;
using System.Text;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Modules.Aggregation.Infrastructure.Collection;

namespace Tinhte.Api.Tests;

/// <summary>
/// Radar tin GĐ1 lát 2 — bộ thu thập không cần DB (ADR-0008 §5–§7): parser (feed độc hại, encoding lạ), chuẩn hóa, "hôm nay",
/// khử trùng, robots.txt, lịch/backoff, phân loại, HTTP an toàn (redirect sang IP nội bộ, DNS rebinding, giới hạn). Không gọi mạng thật.
/// </summary>
public sealed class AggregationCollectorTests
{
    private static readonly TimeZoneInfo Vn = new NewsroomClock(null).Zone;
    private static readonly Uri FeedUri = new("https://news.example.org/rss");

    private static ParsedFeed Parse(string xml, string format = "AUTO", string? charset = null) =>
        FeedParser.Parse(Encoding.UTF8.GetBytes(xml), charset, format, Vn, FeedUri);

    // ---------------------------------------------------------------- parser

    [Fact]
    public void Rss2_items_are_read_with_relative_links_permalink_guids_and_ttl()
    {
        var feed = Parse("""
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel><title>Báo &amp; Tin</title><ttl>45</ttl>
                <item><title>Tin một</title><link>/tin-mot?utm_source=rss</link><guid isPermaLink="false">id-1</guid>
                  <pubDate>Tue, 10 Nov 2026 08:15:00 +0700</pubDate><category>Mobile</category><dc:creator>An</dc:creator>
                  <description>&lt;p&gt;Đoạn &lt;b&gt;một&lt;/b&gt;&lt;/p&gt;&lt;script&gt;alert(1)&lt;/script&gt;</description></item>
                <item><title>Tin hai</title><guid>https://news.example.org/tin-hai</guid><dc:date>2026-11-10T02:00:00Z</dc:date></item>
              </channel>
            </rss>
            """);
        Assert.Equal("RSS2", feed.Format);
        Assert.Equal("Báo & Tin", feed.Title);
        Assert.Equal(45, feed.RefreshMinutesHint);
        Assert.Equal(2, feed.Items.Count);
        var first = feed.Items[0];
        Assert.Equal("https://news.example.org/tin-mot?utm_source=rss", first.Link);
        Assert.Equal("id-1", first.Guid);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 1, 15, 0, TimeSpan.Zero), first.Published);
        Assert.Equal(["Mobile"], first.Categories);
        Assert.Equal("https://news.example.org/tin-hai", feed.Items[1].Link);   // guid là permalink → link
    }

    [Fact]
    public void Atom_rss1_and_json_feed_are_supported()
    {
        var atom = Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://cdn.example.org/blog/">
              <title type="html">&lt;b&gt;Blog&lt;/b&gt;</title>
              <entry><id>tag:x,2026:1</id><title>Bài Atom</title><link rel="self" href="https://cdn.example.org/self"/>
                <link rel="alternate" type="text/html" href="bai-atom"/><published>2026-11-10T09:00:00+07:00</published>
                <updated>2026-11-10T10:00:00+07:00</updated><summary>Tóm tắt</summary><author><name>Bình</name></author>
                <category term="phones" label="Điện thoại"/></entry>
            </feed>
            """);
        Assert.Equal("ATOM", atom.Format);
        Assert.Equal("Blog", atom.Title);
        var entry = Assert.Single(atom.Items);
        Assert.Equal("https://cdn.example.org/blog/bai-atom", entry.Link);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero), entry.Published);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 3, 0, 0, TimeSpan.Zero), entry.Updated);
        Assert.Equal(["Điện thoại"], entry.Categories);

        var rss1 = Parse("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/"
                     xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:sy="http://purl.org/rss/1.0/modules/syndication/">
              <channel rdf:about="https://news.example.org/"><title>RDF</title><sy:updatePeriod>hourly</sy:updatePeriod><sy:updateFrequency>2</sy:updateFrequency></channel>
              <item rdf:about="https://news.example.org/r1"><title>R1</title><link>https://news.example.org/r1</link><dc:date>2026-11-10T08:00:00+07:00</dc:date></item>
            </rdf:RDF>
            """);
        Assert.Equal("RSS1", rss1.Format);
        Assert.Equal(30, rss1.RefreshMinutesHint);
        Assert.Equal("https://news.example.org/r1", Assert.Single(rss1.Items).Guid);

        var json = Parse("""
            {"version":"https://jsonfeed.org/version/1.1","title":"JSON","items":[
              42,
              {"id":7,"url":"https://news.example.org/j1","title":"J1","summary":"S","date_published":"2026-11-10T01:00:00Z","tags":["AI"],"authors":[{"name":"Chi"}]}
            ]}
            """);
        Assert.Equal("JSONFEED", json.Format);
        Assert.Equal(1, json.SkippedItems);   // mục hỏng bị bỏ, feed vẫn đọc được
        var item = Assert.Single(json.Items);
        Assert.Equal("7", item.Guid);
        Assert.Equal("Chi", item.Author);
    }

    [Theory]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><rss version="2.0"><channel><item><title>&xxe;</title><link>https://news.example.org/a</link></item></channel></rss>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE rss [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]><rss version="2.0"><channel><item><title>&c;</title></item></channel></rss>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE rss SYSTEM "https://news.example.org/evil.dtd"><rss version="2.0"><channel/></rss>""")]
    public void Xml_with_dtd_or_external_entities_is_rejected(string xml) =>
        Assert.Contains("DTD", Assert.Throws<FeedParseException>(() => Parse(xml)).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("<html><body>Not a feed</body></html>")]
    [InlineData("""{"title":"không phải JSON Feed","items":[]}""")]
    [InlineData("<rss><channel><item>")]
    public void Non_feeds_are_parse_errors(string body) => Assert.Throws<FeedParseException>(() => Parse(body));

    [Fact]
    public void Declared_format_must_match() =>
        Assert.Contains("not the declared ATOM", Assert.Throws<FeedParseException>(() => Parse("""<rss version="2.0"><channel/></rss>""", "ATOM")).Message, StringComparison.Ordinal);

    [Fact]
    public void Wrong_encoding_bom_html_entities_bare_ampersands_and_control_characters_are_tolerated()
    {
        // Khai báo UTF-8 nhưng byte thật là windows-1258 (lỗi hay gặp của site Việt).
        // windows-1258 ghi tiếng Việt bằng chữ gốc + dấu thanh tổ hợp: "Điện thoại mới" = Đi ê+◌̣ n tho a+◌̣ i m ơ+◌́ i.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string decomposed = "Điện thoại mới";
        var xml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><rss version=\"2.0\"><channel><item><title>{decomposed}</title>"
            + "<link>https://news.example.org/a</link></item></channel></rss>";
        var bytes1258 = Encoding.GetEncoding(1258).GetBytes(xml);
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(bytes1258));   // đúng là không phải UTF-8
        var feed = FeedParser.Parse(bytes1258, null, "AUTO", Vn, FeedUri);
        Assert.Equal(decomposed, TextNormalizer.HtmlToText(Assert.Single(feed.Items).Title, 100));

        var messy = "﻿<rss version=\"2.0\"><channel><item><title>AT&T&nbsp;ra mắt\u0001 &copy; 2026</title>"
            + "<link>https://news.example.org/b</link></item></channel></rss>";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(messy[1..])).ToArray();
        var item = Assert.Single(FeedParser.Parse(bytes, "iso-8859-1", "AUTO", Vn, FeedUri).Items);
        Assert.Equal("AT&T ra mắt © 2026", TextNormalizer.HtmlToText(item.Title, 100));
    }

    [Fact]
    public void Item_count_is_capped()
    {
        var items = string.Concat(Enumerable.Range(0, FeedParser.MaxItems + 3).Select(i => $"<item><title>T{i}</title><link>https://news.example.org/{i}</link></item>"));
        var feed = Parse($"<rss version=\"2.0\"><channel>{items}</channel></rss>");
        Assert.Equal(FeedParser.MaxItems, feed.Items.Count);
        Assert.Equal(3, feed.SkippedItems);
    }

    // ---------------------------------------------------------------- thời điểm, chuẩn hóa, "hôm nay"

    [Theory]
    [InlineData("Tue, 10 Nov 2026 08:15:00 +0700", "2026-11-10T01:15:00Z")]
    [InlineData("10 Nov 2026 01:15 GMT", "2026-11-10T01:15:00Z")]
    [InlineData("Tuesday, 10 November 26 08:15:00 ICT", "2026-11-10T01:15:00Z")]
    [InlineData("Tue, 10 Nov 2026 08:15:00", "2026-11-10T01:15:00Z")]           // không múi giờ → giờ tòa soạn
    [InlineData("Nov 10 2026 03:15:00 +02:00", "2026-11-10T01:15:00Z")]
    [InlineData("2026-11-10T08:15:00+07:00", "2026-11-10T01:15:00Z")]
    [InlineData("2026-11-10 08:15:00", "2026-11-10T01:15:00Z")]
    [InlineData("2026-11-10T01:15:00.123456Z", "2026-11-10T01:15:00.123456Z")]
    [InlineData("31 Feb 2026 08:00:00 GMT", null)]
    [InlineData("hôm qua", null)]
    [InlineData("", null)]
    public void Dates_are_parsed_leniently(string raw, string? expected) =>
        Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), FeedDates.Parse(raw, Vn));

    [Fact]
    public void Url_normalization_drops_tracking_fragment_trailing_slash_and_sorts_query()
    {
        var a = new Uri("https://News.Example.org/tin/a/?utm_source=rss&b=2&fbclid=x&a=1#comments");
        var b = new Uri("https://news.example.org/tin/a?a=1&b=2&utm_medium=social&gclid=y");
        Assert.Equal("https://news.example.org/tin/a?a=1&b=2", TextNormalizer.NormalizeUrl(a));
        Assert.Equal(TextNormalizer.NormalizeUrl(a), TextNormalizer.NormalizeUrl(b));
        Assert.NotEqual(TextNormalizer.NormalizeUrl(a), TextNormalizer.NormalizeUrl(new Uri("https://news.example.org/tin/a?a=2&b=2")));
    }

    [Fact]
    public void Same_story_with_different_utm_links_has_one_dedup_key()   // TC-AGG-05
    {
        var now = DateTimeOffset.Parse("2026-11-10T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        RawFeedItem Raw(string link) => new(null, link, "Tin", null, null, [], now, null);
        var one = ItemNormalizer.Normalize(Raw("https://news.example.org/tin?utm_source=fb"), FeedUri, now)!;
        var two = ItemNormalizer.Normalize(Raw("https://news.example.org/tin/?utm_campaign=x#top"), FeedUri, now)!;
        Assert.Equal(one.UrlHash, two.UrlHash);
        Assert.Equal("https://news.example.org/tin?utm_source=fb", one.Url);   // URL lưu giữ nguyên (chỉ bỏ fragment)
        Assert.Equal("https://news.example.org/tin/?utm_campaign=x", two.Url);
    }

    [Fact]
    public void Item_times_follow_br11()
    {
        var now = DateTimeOffset.Parse("2026-11-10T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        NormalizedItem At(DateTimeOffset? published, DateTimeOffset? updated = null) =>
            ItemNormalizer.Normalize(new RawFeedItem("g", "https://news.example.org/x", "Tin", null, null, [], published, updated), FeedUri, now)!;

        var fresh = At(now.AddHours(-2));
        Assert.True(fresh.TimeReliable);
        Assert.Equal(now.AddHours(-2), fresh.EffectiveAt);

        var old = At(new DateTimeOffset(2019, 5, 1, 0, 0, 0, TimeSpan.Zero));   // TC-AGG-06: năm cũ, lần đầu thấy hôm nay
        Assert.False(old.TimeReliable);
        Assert.Equal(2019, old.EffectiveAt.Year);   // không vào "hôm nay"

        var future = At(now.AddDays(3));   // TC-AGG-07
        Assert.False(future.TimeReliable);
        Assert.Equal(now, future.EffectiveAt);

        var missing = At(null);
        Assert.False(missing.TimeReliable);
        Assert.Equal(now, missing.EffectiveAt);

        Assert.Equal(now.AddHours(-1), At(null, now.AddHours(-1)).EffectiveAt);   // updated khi thiếu published
        Assert.True(At(now.AddMinutes(5)).TimeReliable);                          // lệch đồng hồ ≤ 10 phút vẫn tin được
    }

    [Fact]
    public void Text_is_cleaned_to_plain_text_and_capped()
    {
        Assert.Equal("Xin chào thế giới & bạn",
            TextNormalizer.HtmlToText("<p>Xin <b>chào</b></p><script>alert('x')</script><style>p{}</style><iframe src=x></iframe> thế&nbsp;giới &amp; bạn", 100));
        Assert.Equal("Đoạn một", TextNormalizer.HtmlToText("&lt;p&gt;Đoạn &lt;i&gt;một&lt;/i&gt;&lt;/p&gt;", 100));   // HTML đã escape thêm một lần
        Assert.Null(TextNormalizer.HtmlToText("<img src=x onerror=alert(1)>", 100));
        Assert.Equal(10, TextNormalizer.HtmlToText(new string('a', 50), 10)!.Length);
        var item = ItemNormalizer.Normalize(new RawFeedItem(null, "javascript:alert(1)", "T", null, null, [], null, null), FeedUri, DateTimeOffset.UtcNow);
        Assert.Null(item);   // link không phải http(s) → bỏ mục
    }

    [Fact]
    public void Content_hash_changes_only_on_significant_edits()
    {
        Assert.Equal(ItemNormalizer.ContentHash("Apple ra mắt iPhone", "Tóm tắt."), ItemNormalizer.ContentHash("  APPLE ra mắt   iPhone!", "tóm tắt"));
        Assert.NotEqual(ItemNormalizer.ContentHash("Apple ra mắt iPhone", "Tóm tắt."), ItemNormalizer.ContentHash("Apple hoãn ra mắt iPhone", "Tóm tắt."));
    }

    [Fact]
    public void Newsroom_today_and_day_ranges_use_the_newsroom_time_zone()
    {
        var clock = new NewsroomClock("Asia/Ho_Chi_Minh");
        var (from, to) = clock.DayRange(new DateOnly(2026, 11, 10));
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T17:00:00Z", System.Globalization.CultureInfo.InvariantCulture), from);
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T17:00:00Z", System.Globalization.CultureInfo.InvariantCulture), to);
        Assert.Equal(new DateOnly(2026, 11, 11), clock.Today(DateTimeOffset.Parse("2026-11-10T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Throws<InvalidOperationException>(() => new NewsroomClock("Mars/Olympus"));
    }

    // ---------------------------------------------------------------- robots.txt, lịch

    [Fact]
    public void Robots_rules_follow_rfc9309()
    {
        const string robots = """
            User-agent: *
            Disallow: /private
            Allow: /private/rss$

            User-agent: OtherBot
            Disallow: /

            User-agent: tinhtenewsbot   # nhóm riêng của bot
            Disallow: /feeds/*.xml$
            Disallow: /tmp
            Allow: /tmp/ok
            """;
        var rules = RobotsRules.Parse(robots, BotUserAgent.ProductToken);
        Assert.True(rules.IsAllowed("/private/anything"));   // nhóm riêng thay hẳn nhóm *
        Assert.False(rules.IsAllowed("/feeds/tin.xml"));
        Assert.True(rules.IsAllowed("/feeds/tin.xml?x=1"));
        Assert.False(rules.IsAllowed("/tmp/x"));
        Assert.True(rules.IsAllowed("/tmp/ok/1"));         // khớp dài nhất thắng
        Assert.True(rules.IsAllowed("/robots.txt"));

        var star = RobotsRules.Parse("User-agent: *\nDisallow: /private\nAllow: /private/rss$\nDisallow:\n", BotUserAgent.ProductToken);
        Assert.False(star.IsAllowed("/private/x"));
        Assert.True(star.IsAllowed("/private/rss"));
        Assert.False(star.IsAllowed("/private/rss2"));
        Assert.True(RobotsRules.Parse("User-agent: TinhteNewsBot\nAllow: /\n\nUser-agent: *\nDisallow: /\n", BotUserAgent.ProductToken).IsAllowed("/rss"));
        // Các dòng user-agent liền nhau (kể cả cách dòng trống) thuộc cùng một nhóm (RFC 9309 §2.1).
        Assert.False(RobotsRules.Parse("User-agent: TinhteNewsBot\n\nUser-agent: *\nDisallow: /\n", BotUserAgent.ProductToken).IsAllowed("/rss"));
    }

    [Fact]
    public void Schedule_respects_hints_backoff_cap_and_retry_after()
    {
        var now = DateTimeOffset.Parse("2026-11-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(now.AddMinutes(15), FetchSchedule.AfterSuccess(now, 15, null, 0));
        Assert.Equal(now.AddMinutes(60), FetchSchedule.AfterSuccess(now, 15, 60, 0));          // ttl dài hơn chu kỳ
        Assert.Equal(now.AddMinutes(1440), FetchSchedule.AfterSuccess(now, 15, 100_000, 0));   // tối đa 24 giờ
        Assert.InRange(FetchSchedule.AfterSuccess(now, 15, null, 0.999), now.AddMinutes(15), now.AddMinutes(16.5));   // jitter ≤ 10%

        Assert.Equal(now.AddMinutes(15), FetchSchedule.AfterFailure(now, 15, 1, null, 0));
        Assert.Equal(now.AddMinutes(60), FetchSchedule.AfterFailure(now, 15, 3, null, 0));
        Assert.Equal(now.AddHours(6), FetchSchedule.AfterFailure(now, 15, 12, null, 0));       // backoff tối đa 6 giờ
        Assert.True(FetchSchedule.AfterFailure(now, 15, 1, TimeSpan.FromSeconds(3600), 0) >= now.AddHours(1));   // TC-AGG-08
        Assert.Equal(now.AddHours(24), FetchSchedule.AfterFailure(now, 15, 1, TimeSpan.FromDays(30), 0));

        Assert.Equal("DEGRADED", FetchSchedule.HealthAfterFailure(FeedFailures.Timeout, 4));
        Assert.Equal("FAILING", FetchSchedule.HealthAfterFailure(FeedFailures.Timeout, 5));
        Assert.Equal("BLOCKED_BY_ROBOTS", FetchSchedule.HealthAfterFailure(FeedFailures.RobotsDisallowed, 9));
    }

    // ---------------------------------------------------------------- phân loại

    private static readonly Guid Phones = Guid.NewGuid();
    private static readonly Guid Laptops = Guid.NewGuid();
    private static readonly Guid Mapped = Guid.NewGuid();

    private static ItemClassifier Classifier(SensitiveTopics? sensitive = null) => new(
        ["www"],
        new Dictionary<string, (Guid, string)> { ["Mobile"] = (Mapped, "www") },
        [
            new CategoryRuleSet(Phones, "www", ["điện thoại", "smartphone"], ["ốp lưng"], ["iPhone", "Galaxy"]),
            new CategoryRuleSet(Laptops, "www", ["laptop", "máy tính xách tay"], [], ["MacBook"]),
            new CategoryRuleSet(Guid.NewGuid(), "game", ["điện thoại"], [], []),   // site khác của nguồn: bỏ qua
        ],
        sensitive ?? SensitiveTopics.Empty,
        _ => ClassificationThresholds.Default);

    private static NormalizedItem Item(string title, string? summary = null, params string[] categories) =>
        ItemNormalizer.Normalize(new RawFeedItem(null, "https://news.example.org/" + Guid.NewGuid(), title, summary, null, categories, DateTimeOffset.UtcNow, null),
            FeedUri, DateTimeOffset.UtcNow)!;

    [Fact]
    public void Feed_category_mapping_wins_over_keywords()
    {
        var c = Classifier().Classify(Item("Điện thoại iPhone mới", null, "mobile"));
        Assert.Equal((Mapped, 1.0m, false, "CLASSIFIED", "www"), (c.CategoryId!.Value, c.Confidence!.Value, c.NeedsReview, c.Status, c.SiteKey));
    }

    [Fact]
    public void Item_keeps_every_qualifying_category_of_its_site_for_clustering()   // BA-01 P-02: so trùng trong từng chuyên mục của mục
    {
        var both = Classifier().Classify(Item("Điện thoại iPhone và MacBook mới", null, "mobile"));
        Assert.Equal(Mapped, both.CategoryId);
        Assert.Equal([Mapped, Phones, Laptops], both.CategoryIds);   // chính đứng đầu; luật của site "game" không tính

        Assert.Equal([Phones], Classifier().Classify(Item("iPhone 18 là điện thoại mỏng nhất")).CategoryIds);
        Assert.Equal([Phones], Classifier().Classify(Item("iPhone 18 là điện thoại mỏng nhất", "Có nhắc tới laptop.")).CategoryIds.Take(1));
        Assert.Empty(Classifier().Classify(Item("Thị trường tuần này")).CategoryIds);   // OUT_OF_SCOPE
    }

    [Fact]
    public void Keyword_scores_map_to_br31_thresholds()
    {
        var two = Classifier().Classify(Item("iPhone 18 là điện thoại mỏng nhất"));   // 2 từ khóa ở tiêu đề → 0,95 tự gán
        Assert.Equal((Phones, 0.95m, false, "CLASSIFIED"), (two.CategoryId!.Value, two.Confidence!.Value, two.NeedsReview, two.Status));

        var one = Classifier().Classify(Item("Đánh giá MacBook Air"));             // 1 từ khóa ở tiêu đề → 0,70 cần xem
        Assert.Equal((Laptops, 0.70m, true), (one.CategoryId!.Value, one.Confidence!.Value, one.NeedsReview));

        var weak = Classifier().Classify(Item("Thị trường tuần này", "Có nhắc tới laptop."));   // chỉ ở tóm tắt → 0,55 cần xem
        Assert.Equal((0.55m, true), (weak.Confidence!.Value, weak.NeedsReview));

        var none = Classifier().Classify(Item("Giá vàng hôm nay"));
        Assert.Equal(("OUT_OF_SCOPE", null, "www"), (none.Status, none.CategoryId, none.SiteKey));

        var excluded = Classifier().Classify(Item("Ốp lưng cho điện thoại iPhone"));   // từ khóa loại trừ
        Assert.NotEqual(Phones, excluded.CategoryId);

        Assert.Equal("OUT_OF_SCOPE", Classifier().Classify(Item("Smartphonex và Galaxyy")).Status);   // khớp nguyên từ
    }

    [Fact]
    public void Sensitive_topics_match_whole_phrases_keeping_vietnamese_diacritics()
    {
        var topics = SensitiveTopics.Parse("""{"version":1,"sites":{"*":[{"topic":"t","keywords":["đảng phái","bão"]}],"www":[{"topic":"w","keywords":["tin đồn"]}]}}""");
        Assert.True(Classifier(topics).Classify(Item("Tin đồn iPhone gập")).Sensitive);
        Assert.True(Classifier(topics).Classify(Item("Cơn BÃO số 9")).Sensitive);
        Assert.False(Classifier(topics).Classify(Item("Điện thoại đang giảm giá, bao nhiêu tiền?")).Sensitive);   // "đang", "bao" ≠ "đảng", "bão"
    }

    [Fact]
    public void Repository_sensitive_topics_file_is_valid()
    {
        var topics = SensitiveTopics.Load(RepoPaths.File("config", "aggregation", "sensitive-topics.json"));
        Assert.True(topics.KeywordCount > 10);
        Assert.Throws<InvalidOperationException>(() => SensitiveTopics.Parse("""{"version":1,"sites":{"CMS!":[]}}"""));
        Assert.Throws<InvalidOperationException>(() => SensitiveTopics.Parse("""{"version":1,"sites":{"*":[{"topic":"x","keywords":["  "]}]}}"""));
    }

    // ---------------------------------------------------------------- HTTP an toàn (handler giả, không gọi mạng)

    private const string Url = "https://news.example.org/rss";
    private static readonly string OneItem = FeedFixtures.Rss(("g1", "https://news.example.org/a", "Tin A", "Mô tả", "Mobile", DateTimeOffset.UtcNow));

    [Fact]
    public async Task Fetch_sends_bot_user_agent_and_conditional_headers()
    {
        var handler = new FakeFeedHandler().OnFeed(Url, OneItem, etag: "\"v1\"");
        var fetcher = FeedFixtures.Fetcher(handler);
        var ok = await fetcher.FetchAsync(Url, null, null, CancellationToken.None);
        Assert.Equal((FetchOutcomes.Ok, 200, "\"v1\""), (ok.Outcome, ok.HttpStatus, ok.ETag));
        Assert.Equal(OneItem.Length, Encoding.UTF8.GetString(ok.Body!).Length);
        var request = handler.Requests.Last();
        Assert.StartsWith("TinhteNewsBot/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath == "/robots.txt");

        var notModified = await fetcher.FetchAsync(Url, "\"v1\"", "Tue, 10 Nov 2026 01:00:00 GMT", CancellationToken.None);
        Assert.Equal(FetchOutcomes.NotModified, notModified.Outcome);   // TC-AGG-02
        Assert.Equal("Tue, 10 Nov 2026 01:00:00 GMT", handler.Requests.Last().Headers.GetValues("If-Modified-Since").Single());
        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/robots.txt"));   // robots.txt được cache
    }

    [Fact]
    public void Bot_user_agent_is_derived_from_the_site_registry()
    {
        var registry = Tinhte.Platform.Sites.SiteRegistry.Load(RepoPaths.File("config", "sites", "sites.json"), "local");
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var ua = BotUserAgent.FromRegistry(registry, config).Value;
        Assert.Equal($"TinhteNewsBot/1.0 (+{registry.OriginFor(registry.ResolvableSites.First(s => s.Kind == "public-web"))}/bot)", ua);
    }

    [Fact]
    public async Task Robots_disallow_stops_the_fetch()
    {
        var handler = new FakeFeedHandler()
            .On("https://news.example.org/robots.txt", _ => FakeFeedHandler.Text(HttpStatusCode.OK, "User-agent: *\nDisallow: /rss", "text/plain"))
            .OnFeed(Url, OneItem);
        var result = await FeedFixtures.Fetcher(handler).FetchAsync(Url, null, null, CancellationToken.None);
        Assert.Equal((FeedFailures.RobotsDisallowed, false), (result.Failure, result.RobotsAllowed));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath == "/rss");
    }

    [Theory]
    [InlineData("https://10.0.0.5/rss")]              // IP nội bộ
    [InlineData("https://169.254.169.254/latest")]    // metadata đám mây
    [InlineData("https://rebind.example.org/rss")]    // tên phân giải ra 127.0.0.1
    [InlineData("http://news.example.org/rss")]       // hạ xuống http
    [InlineData("https://news.example.org:8443/rss")] // cổng lạ
    public async Task Redirects_to_internal_or_unsafe_targets_are_refused(string target)
    {
        var handler = new FakeFeedHandler().Redirect(Url, target, HttpStatusCode.MovedPermanently);
        var result = await FeedFixtures.Fetcher(handler).FetchAsync(Url, null, null, CancellationToken.None);
        Assert.True(result.Unsafe);
        Assert.Equal((FetchOutcomes.Failed, 301), (result.Outcome, result.HttpStatus));
        Assert.All(handler.Requests, r => Assert.Equal("news.example.org", r.RequestUri!.Host));   // không request nào tới đích nội bộ
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsoluteUri == target);
    }

    [Fact]
    public async Task Redirects_are_followed_at_most_three_times()
    {
        var handler = new FakeFeedHandler()
            .Redirect(Url, "https://news.example.org/r1").Redirect("https://news.example.org/r1", "/r2")
            .Redirect("https://news.example.org/r2", "https://cdn.example.org/r3").OnFeed("https://cdn.example.org/r3", OneItem);
        Assert.Equal(FetchOutcomes.Ok, (await FeedFixtures.Fetcher(handler).FetchAsync(Url, null, null, CancellationToken.None)).Outcome);

        handler.Redirect("https://cdn.example.org/r3", "https://cdn.example.org/r4");
        var tooMany = await FeedFixtures.Fetcher(handler).FetchAsync(Url, null, null, CancellationToken.None);
        Assert.Equal(FeedFailures.TooManyRedirects, tooMany.Failure);
    }

    [Fact]
    public async Task Size_type_status_and_timeout_failures_are_data()
    {
        var limits = new FeedFetcherLimits { MaxBytes = 1024, Timeout = TimeSpan.FromMilliseconds(300) };
        var handler = new FakeFeedHandler()
            .OnFeed("https://news.example.org/big", "<rss>" + new string('x', 2000) + "</rss>")
            .OnFeed("https://news.example.org/html", "<html/>", "text/html")
            .On("https://news.example.org/500", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .On("https://news.example.org/429", _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));
                return r;
            })
            .On("https://news.example.org/slow", _ =>
            {
                Thread.Sleep(1500);
                return FakeFeedHandler.Text(HttpStatusCode.OK, "<rss/>", "application/xml");
            });
        var fetcher = FeedFixtures.Fetcher(handler, limits: limits);
        async Task<FetchResult> Get(string path) => await fetcher.FetchAsync("https://news.example.org/" + path, null, null, CancellationToken.None);

        Assert.Equal(FeedFailures.TooLarge, (await Get("big")).Failure);
        Assert.Equal(FeedFailures.UnsupportedContentType, (await Get("html")).Failure);
        Assert.Equal((FeedFailures.HttpStatus, 500), ((await Get("500")).Failure, (await Get("500")).HttpStatus));
        var limited = await Get("429");
        Assert.Equal((FeedFailures.RateLimitedBySource, TimeSpan.FromHours(1)), (limited.Failure, limited.RetryAfter));
        Assert.Equal(FeedFailures.Timeout, (await Get("slow")).Failure);
        Assert.Equal(FeedFailures.Dns, (await fetcher.FetchAsync("https://chua-co-dns.example.org/rss", null, null, CancellationToken.None)).Failure);
    }

    /// <summary>
    /// DNS rebinding với handler THẬT (SocketsHttpHandler): kiểm tra trước lúc gửi thấy IP công khai, nhưng lúc mở kết nối tên miền trả
    /// 127.0.0.1 → ConnectCallback chặn trước khi mở socket. Không có kết nối mạng nào được mở.
    /// </summary>
    [Fact]
    public async Task Connect_callback_blocks_dns_rebinding_without_opening_a_socket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var resolver = new RebindingResolver();
        using var handler = SafeFeedHttp.CreateHandler(resolver);
        var fetcher = new FeedFetcher(handler, resolver, FeedFixtures.UserAgent, TimeProvider.System);

        var result = await fetcher.FetchAsync("https://rebinding.example.org/rss", null, null, CancellationToken.None);
        Assert.True(result.Unsafe, result.Detail);
        Assert.Contains("127.0.0.1", result.Detail, StringComparison.Ordinal);
        Assert.False(listener.Pending());

        var direct = await Assert.ThrowsAsync<UnsafeAddressException>(async () =>
            await SafeFeedHttp.ConnectAsync(resolver, new DnsEndPoint("rebinding.example.org", ((IPEndPoint)listener.LocalEndpoint).Port), CancellationToken.None));
        Assert.Contains("not opened", direct.Message, StringComparison.Ordinal);
        Assert.False(listener.Pending());
        await Assert.ThrowsAsync<FeedDnsException>(async () =>
            await SafeFeedHttp.ConnectAsync(new FakeFeedHostResolver(new Dictionary<string, string[]>()), new DnsEndPoint("khong-co.example.org", 443), CancellationToken.None));
    }

    /// <summary>Hai lần phân giải đầu (kiểm tra trước) trả IP công khai, các lần sau (lúc kết nối) trả loopback.</summary>
    private sealed class RebindingResolver : IFeedHostResolver
    {
        private int _calls;

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([Interlocked.Increment(ref _calls) <= 2 ? IPAddress.Parse("93.184.215.14") : IPAddress.Loopback]);
    }
}

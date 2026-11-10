namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>Mục tin đã chuẩn hóa, sẵn sàng khử trùng/phân loại/lưu (cột của tt.agg_items).</summary>
public sealed record NormalizedItem(
    string? Guid,
    string Url,
    string UrlHash,
    string Title,
    string? Summary,
    string? Author,
    IReadOnlyList<string> FeedCategories,
    DateTimeOffset? PublishedAt,
    bool TimeReliable,
    DateTimeOffset EffectiveAt,
    string ContentHash,
    DateTimeOffset? SourceUpdatedAt);

/// <summary>
/// Chuẩn hóa mục tin (ADR-0008 §6, BR-10…16):
/// <list type="bullet">
///   <item>văn bản thuần (BR-16); tiêu đề ≤ 1000, tóm tắt ≤ 4000 ký tự (maxLength của AggregationItem.summary), tác giả ≤ 300;</item>
///   <item>khóa khử trùng: <c>guid</c> nếu có; <c>url_hash</c> = SHA-256 của URL chuẩn hóa (BR-10). Mục không có link http(s) dùng được bị bỏ:
///         hợp đồng bắt buộc <c>AggregationItem.url</c> và BTV cần link nguồn để ghi nguồn (tầng 3 "hash tiêu đề + ngày" của BR-10 không dùng);</item>
///   <item>thời điểm (BR-11): published → updated; thiếu, ở tương lai &gt; 10 phút, hoặc cũ hơn 30 ngày lúc mới thấy ⇒ <c>time_reliable = false</c>.
///         Thời điểm hiệu lực (dùng cho "hôm nay"/24 giờ) = published nếu có và không ở tương lai, ngược lại = lúc thấy lần đầu — nên tin
///         ghi ngày cũ không lọt vào "hôm nay" (TC-AGG-06), tin ghi ngày tương lai tính theo lúc thấy (TC-AGG-07);</item>
///   <item><c>content_hash</c> = SHA-256 của tiêu đề + tóm tắt dạng so khớp: đổi đáng kể ⇒ phiên bản mới (BR-13).</item>
/// </list>
/// </summary>
public static class ItemNormalizer
{
    public const int MaxTitle = 1000;
    public const int MaxSummary = 4000;
    public const int MaxAuthor = 300;
    public const int MaxCategory = 200;
    public const int MaxCategories = 50;
    public const int MaxGuid = 1000;
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StaleAge = TimeSpan.FromDays(30);

    /// <summary>Quy tắc cũ (BR-14, khi <c>Aggregation:Intake:TodayOnly = false</c>): lần đầu bật feed chỉ nhận mục trong 24 giờ gần nhất.</summary>
    public static readonly TimeSpan FirstFetchWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Mốc nhận tin: mục có thời điểm hiệu lực trước mốc bị bỏ qua (không lưu, không cập nhật). <paramref name="todayOnly"/> (mặc định, BA-01 P-02):
    /// 00:00 hôm nay theo giờ tòa soạn, ở mọi lần đọc; ngược lại BR-14: 24 giờ trước ở lần đọc đầu, không giới hạn ở các lần sau (null).
    /// </summary>
    public static DateTimeOffset? IntakeCutoff(DateTimeOffset now, bool firstFetch, bool todayOnly, NewsroomClock clock) =>
        todayOnly ? clock.DayRange(clock.Today(now)).FromUtc : firstFetch ? now - FirstFetchWindow : null;

    public static NormalizedItem? Normalize(RawFeedItem raw, Uri feedUri, DateTimeOffset now)
    {
        var link = TextNormalizer.AbsoluteHttpUrl(raw.Link, feedUri);
        if (link is null) return null;

        var summary = TextNormalizer.HtmlToText(raw.Summary, MaxSummary);
        var title = TextNormalizer.HtmlToText(raw.Title, MaxTitle) ?? TextNormalizer.HtmlToText(summary, 160);
        if (title is null) return null;

        var guid = raw.Guid?.Trim();
        if (string.IsNullOrEmpty(guid) || guid.Length > MaxGuid) guid = null;

        var categories = raw.Categories
            .Select(c => TextNormalizer.HtmlToText(c, MaxCategory))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCategories)
            .ToList();

        var published = raw.Published ?? raw.Updated;
        var reliable = published is { } p && p <= now + FutureTolerance && p >= now - StaleAge;
        var effective = published is { } q && q <= now + FutureTolerance ? q : now;

        return new NormalizedItem(
            Guid: guid,
            Url: link.AbsoluteUri,
            UrlHash: TextNormalizer.Sha256Hex(TextNormalizer.NormalizeUrl(link)),
            Title: title,
            Summary: summary,
            Author: TextNormalizer.HtmlToText(raw.Author, MaxAuthor),
            FeedCategories: categories,
            PublishedAt: published,
            TimeReliable: reliable,
            EffectiveAt: effective,
            ContentHash: ContentHash(title, summary),
            SourceUpdatedAt: raw.Updated);
    }

    public static string ContentHash(string title, string? summary) =>
        TextNormalizer.Sha256Hex(TextNormalizer.Fold(title) + "\n" + TextNormalizer.Fold(summary));
}

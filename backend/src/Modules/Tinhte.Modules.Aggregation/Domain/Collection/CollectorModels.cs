namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>AggregationFeedFailure của hợp đồng — dữ liệu sức khỏe feed/kết quả thử feed, KHÔNG phải Problem.code (ADR-0008 §10).</summary>
public static class FeedFailures
{
    public const string Timeout = "TIMEOUT";
    public const string Dns = "DNS";
    public const string Tls = "TLS";
    public const string HttpStatus = "HTTP_STATUS";
    public const string TooLarge = "TOO_LARGE";
    public const string UnsupportedContentType = "UNSUPPORTED_CONTENT_TYPE";
    public const string ParseError = "PARSE_ERROR";
    public const string RobotsDisallowed = "ROBOTS_DISALLOWED";
    public const string TooManyRedirects = "TOO_MANY_REDIRECTS";
    public const string RateLimitedBySource = "RATE_LIMITED_BY_SOURCE";

    /// <summary>Khớp enum của spec và CHECK ck_agg_feeds_last_failure (0341).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Timeout, Dns, Tls, HttpStatus, TooLarge, UnsupportedContentType, ParseError, RobotsDisallowed, TooManyRedirects, RateLimitedBySource,
    };
}

/// <summary>Kết quả một lần tải (CHECK ck_agg_fetch_log_outcome).</summary>
public static class FetchOutcomes
{
    public const string Ok = "OK";
    public const string NotModified = "NOT_MODIFIED";
    public const string Failed = "FAILED";
}

/// <summary>Một mục đọc thô từ feed (chưa làm sạch). Thời điểm đã quy về UTC (giờ không có múi → giờ tòa soạn).</summary>
public sealed record RawFeedItem(
    string? Guid,
    string? Link,
    string? Title,
    string? Summary,
    string? Author,
    IReadOnlyList<string> Categories,
    DateTimeOffset? Published,
    DateTimeOffset? Updated);

/// <summary>Feed đã parse. <c>RefreshMinutesHint</c> = <c>ttl</c> hoặc <c>sy:updatePeriod/updateFrequency</c> (BR-04).</summary>
public sealed record ParsedFeed(string Format, string? Title, int? RefreshMinutesHint, IReadOnlyList<RawFeedItem> Items, int SkippedItems);

/// <summary>Feed không đọc được (XML/JSON hỏng, có DTD, không phải feed, sai định dạng khai báo) → FeedFailure PARSE_ERROR.</summary>
public sealed class FeedParseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Kết nối bị chặn vì địa chỉ đích không công khai (SSRF, kể cả DNS rebinding) — ném từ ConnectCallback.</summary>
public sealed class UnsafeAddressException(string message) : Exception(message);

/// <summary>Tên miền không phân giải được lúc kết nối → FeedFailure DNS.</summary>
public sealed class FeedDnsException(string host) : Exception($"The host '{host}' could not be resolved.");

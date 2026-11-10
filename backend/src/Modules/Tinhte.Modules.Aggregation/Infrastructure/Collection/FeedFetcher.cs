using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Configuration;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Sites;

namespace Tinhte.Modules.Aggregation.Infrastructure.Collection;

/// <summary>Giới hạn của bộ thu thập (ADR-0008 §5, BR-05). Chờ Security duyệt (A-4).</summary>
public sealed record FeedFetcherLimits
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    public int MaxBytes { get; init; } = 5 * 1024 * 1024;

    public int MaxRedirects { get; init; } = 3;

    public int RobotsMaxBytes { get; init; } = 512 * 1024;

    public TimeSpan RobotsTtl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>robots.txt trả 5xx/429: coi như cấm hết (RFC 9309 §2.3.1.4) nhưng chỉ nhớ 1 giờ để thử lại sớm.</summary>
    public TimeSpan RobotsErrorTtl { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>Kết quả một lần tải feed. Lỗi là dữ liệu (<see cref="FeedFailures"/>), không phải Problem.</summary>
public sealed record FetchResult(
    string Outcome,
    string? Failure,
    int? HttpStatus,
    bool RobotsAllowed,
    bool Unsafe,
    string? Detail,
    byte[]? Body,
    string? Charset,
    string? ETag,
    string? LastModified,
    TimeSpan? RetryAfter,
    int BytesRead)
{
    public static FetchResult Failed(string failure, int? status = null, string? detail = null, bool robotsAllowed = true, TimeSpan? retryAfter = null) =>
        new(FetchOutcomes.Failed, failure, status, robotsAllowed, false, detail, null, null, null, null, retryAfter, 0);

    /// <summary>Đích (hoặc một bước redirect) là địa chỉ/URL không an toàn. Thử feed ⇒ 422 UNSAFE_URL; worker ⇒ dữ liệu lỗi.</summary>
    public static FetchResult UnsafeTarget(string detail, int? status = null) =>
        new(FetchOutcomes.Failed, status is null ? FeedFailures.Dns : FeedFailures.HttpStatus, status, true, true, detail, null, null, null, null, null, 0);
}

/// <summary>User-Agent của bot (BR-03): <c>TinhteNewsBot/&lt;phiên bản&gt; (+&lt;trang giới thiệu bot&gt;)</c>, URL suy từ Site Registry.</summary>
public sealed class BotUserAgent(string value)
{
    public const string ProductToken = "TinhteNewsBot";
    public const string DefaultInfoPath = "/bot";

    public string Value { get; } = value;

    /// <summary>
    /// Site: <c>Aggregation:BotSiteKey</c> hoặc site public-web đầu tiên phân giải được trong môi trường; đường dẫn: <c>Aggregation:BotInfoPath</c>.
    /// Không viết cứng host (ADR-0001).
    /// </summary>
    public static BotUserAgent FromRegistry(SiteRegistry registry, IConfiguration configuration)
    {
        var key = configuration["Aggregation:BotSiteKey"];
        var site = key is null
            ? registry.ResolvableSites.FirstOrDefault(s => s.Kind == "public-web")
            : registry.ResolvableSites.FirstOrDefault(s => s.Key == key && s.Kind == "public-web");
        if (site is null) throw new InvalidOperationException($"No public-web site '{key ?? "(first)"}' to host the bot information page.");
        var path = configuration["Aggregation:BotInfoPath"] ?? DefaultInfoPath;
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Aggregation:BotInfoPath must be an absolute path such as /bot.");
        }

        var version = typeof(BotUserAgent).Assembly.GetName().Version ?? new Version(1, 0);
        return new BotUserAgent($"{ProductToken}/{version.Major}.{version.Minor} (+{registry.OriginFor(site)}{path})");
    }
}

/// <summary>
/// Tải feed an toàn (ADR-0008 §5, BR-02/03/05): kiểm URL + DNS mỗi bước (ConnectCallback kiểm lại lúc kết nối), tự theo tối đa 3 redirect,
/// tôn trọng robots.txt (cache 24 giờ theo host), timeout 20 giây cho cả lượt (gồm robots và redirect), đọc tối đa 5 MB SAU giải nén,
/// chỉ nhận content-type XML/JSON, gửi <c>If-None-Match</c>/<c>If-Modified-Since</c>, đọc <c>Retry-After</c> khi 429/503.
/// </summary>
public sealed class FeedFetcher
{
    private static readonly int[] RedirectStatuses = [301, 302, 303, 307, 308];

    private readonly HttpClient _client;
    private readonly IFeedHostResolver _resolver;
    private readonly BotUserAgent _userAgent;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (RobotsRules Rules, DateTimeOffset Expires)> _robots = new(StringComparer.Ordinal);

    public FeedFetcher(HttpMessageHandler handler, IFeedHostResolver resolver, BotUserAgent userAgent, TimeProvider time, FeedFetcherLimits? limits = null)
    {
        _client = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _resolver = resolver;
        _userAgent = userAgent;
        _time = time;
        Limits = limits ?? new FeedFetcherLimits();
    }

    public FeedFetcherLimits Limits { get; }

    public async Task<FetchResult> FetchAsync(string url, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        Uri uri;
        try
        {
            uri = FeedUrlPolicy.Parse("url", url);
        }
        catch (DomainProblemException e)
        {
            return FetchResult.UnsafeTarget(e.Detail ?? "The feed URL is not allowed.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Limits.Timeout);
        var ct = deadline.Token;
        int? lastStatus = null;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                if (await CheckTargetAsync(uri, lastStatus, ct) is { } blocked) return blocked;
                if (!await RobotsAllowedAsync(uri, ct))
                {
                    return FetchResult.Failed(FeedFailures.RobotsDisallowed, null, $"robots.txt of {uri.Host} disallows {uri.AbsolutePath}.", robotsAllowed: false);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(_userAgent.Value);
                request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/feed+json, application/rdf+xml;q=0.9, application/xml;q=0.9, text/xml;q=0.9, application/json;q=0.8");
                if (!string.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                if (!string.IsNullOrEmpty(lastModified)) request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var status = (int)response.StatusCode;
                lastStatus = status;
                if (RedirectStatuses.Contains(status))
                {
                    if (redirects >= Limits.MaxRedirects)
                    {
                        return FetchResult.Failed(FeedFailures.TooManyRedirects, status, $"More than {Limits.MaxRedirects} redirects.");
                    }

                    if (response.Headers.Location is not { } location) return FetchResult.Failed(FeedFailures.HttpStatus, status, "Redirect without Location.");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    try
                    {
                        uri = FeedUrlPolicy.Parse("url", next.AbsoluteUri);
                    }
                    catch (DomainProblemException e)
                    {
                        return FetchResult.UnsafeTarget($"Redirect to {next.Scheme}://{next.Host} refused: {e.Detail}", status);
                    }

                    continue;
                }

                if (status == 304) return new FetchResult(FetchOutcomes.NotModified, null, status, true, false, null, null, null, etag, lastModified, null, 0);
                if (status is 429 or 503)
                {
                    return FetchResult.Failed(FeedFailures.RateLimitedBySource, status, "The source asked us to slow down.", retryAfter: RetryAfter(response));
                }

                if (status is < 200 or > 299) return FetchResult.Failed(FeedFailures.HttpStatus, status, $"HTTP {status}.");

                var contentType = response.Content.Headers.ContentType;
                if (!IsFeedMediaType(contentType?.MediaType))
                {
                    return FetchResult.Failed(FeedFailures.UnsupportedContentType, status, $"Content-Type '{contentType?.MediaType ?? "(none)"}' is not XML or JSON.");
                }

                if (response.Content.Headers.ContentLength > Limits.MaxBytes) return FetchResult.Failed(FeedFailures.TooLarge, status, $"Larger than {Limits.MaxBytes} bytes.");
                var body = await ReadLimitedAsync(response.Content, Limits.MaxBytes, ct);
                if (body is null) return FetchResult.Failed(FeedFailures.TooLarge, status, $"Larger than {Limits.MaxBytes} bytes after decompression.");

                return new FetchResult(FetchOutcomes.Ok, null, status, true, false, null, body, contentType?.CharSet,
                    response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture), null, body.Length);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FetchResult.Failed(FeedFailures.Timeout, lastStatus, $"No complete response within {Limits.Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException e)
        {
            return Classify(e, lastStatus);
        }
        catch (InvalidDataException e)
        {
            return FetchResult.Failed(FeedFailures.ParseError, lastStatus, "Corrupt compressed body: " + e.Message);
        }
        catch (IOException e)
        {
            return FetchResult.Failed(FeedFailures.Timeout, lastStatus, "Connection dropped: " + e.Message);
        }
    }

    /// <summary>Kiểm DNS của đích trước khi gửi (ConnectCallback kiểm lại ngay lúc mở socket).</summary>
    private async Task<FetchResult?> CheckTargetAsync(Uri uri, int? redirectStatus, CancellationToken ct)
    {
        try
        {
            await SafeFeedHttp.ResolveCheckedAsync(_resolver, uri.IdnHost, ct);
            return null;
        }
        catch (UnsafeAddressException e)
        {
            return FetchResult.UnsafeTarget(e.Message, redirectStatus);
        }
        catch (FeedDnsException e)
        {
            return FetchResult.Failed(FeedFailures.Dns, redirectStatus, e.Message);
        }
    }

    // ---------------------------------------------------------------- robots.txt

    private async Task<bool> RobotsAllowedAsync(Uri uri, CancellationToken ct)
    {
        var key = uri.GetLeftPart(UriPartial.Authority);
        var now = _time.GetUtcNow();
        if (_robots.TryGetValue(key, out var cached) && cached.Expires > now) return cached.Rules.IsAllowed(uri.PathAndQuery);

        var loaded = await LoadRobotsAsync(new Uri(key + "/robots.txt"), ct);
        if (loaded is null) return true;   // không tới được robots.txt (lỗi mạng): để chính request feed báo lỗi thật
        if (_robots.Count > 10_000) _robots.Clear();
        _robots[key] = (loaded.Value.Rules, now + loaded.Value.Ttl);
        return loaded.Value.Rules.IsAllowed(uri.PathAndQuery);
    }

    private async Task<(RobotsRules Rules, TimeSpan Ttl)?> LoadRobotsAsync(Uri robotsUri, CancellationToken ct)
    {
        var uri = robotsUri;
        try
        {
            for (var redirects = 0; redirects <= Limits.MaxRedirects; redirects++)
            {
                try
                {
                    await SafeFeedHttp.ResolveCheckedAsync(_resolver, uri.IdnHost, ct);
                }
                catch (UnsafeAddressException)
                {
                    return (RobotsRules.DisallowAll, Limits.RobotsErrorTtl);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(_userAgent.Value);
                request.Headers.Accept.ParseAdd("text/plain");
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var status = (int)response.StatusCode;
                if (RedirectStatuses.Contains(status) && response.Headers.Location is { } location)
                {
                    try
                    {
                        uri = FeedUrlPolicy.Parse("url", (location.IsAbsoluteUri ? location : new Uri(uri, location)).AbsoluteUri);
                    }
                    catch (DomainProblemException)
                    {
                        return (RobotsRules.DisallowAll, Limits.RobotsErrorTtl);
                    }

                    continue;
                }

                if (status is >= 200 and <= 299)
                {
                    var bytes = await ReadLimitedAsync(response.Content, Limits.RobotsMaxBytes, ct, truncate: true) ?? [];
                    return (RobotsRules.Parse(FeedParser.Decode(bytes, response.Content.Headers.ContentType?.CharSet, json: true), BotUserAgent.ProductToken), Limits.RobotsTtl);
                }

                // RFC 9309: 4xx (không có robots.txt) → được phép; 5xx/429 → coi như cấm hết, thử lại sau.
                return status is >= 400 and <= 499 and not 429 ? (RobotsRules.AllowAll, Limits.RobotsTtl) : (RobotsRules.DisallowAll, Limits.RobotsErrorTtl);
            }

            return (RobotsRules.AllowAll, Limits.RobotsTtl);   // quá nhiều redirect cho robots.txt: RFC 9309 coi như không có
        }
        catch (Exception e) when (e is HttpRequestException or FeedDnsException or IOException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- hỗ trợ

    /// <summary>Đọc tối đa <paramref name="max"/> byte (đếm sau giải nén). Vượt → null (hoặc cắt khi <paramref name="truncate"/>).</summary>
    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, int max, CancellationToken ct, bool truncate = false)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct);
                if (read == 0) break;
                if (buffer.Length + read > max)
                {
                    if (!truncate) return null;
                    buffer.Write(chunk, 0, (int)(max - buffer.Length));
                    break;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    internal static bool IsFeedMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return false;
        var m = mediaType.Trim().ToLowerInvariant();
        return m.EndsWith("/xml", StringComparison.Ordinal) || m.EndsWith("+xml", StringComparison.Ordinal)
            || m.EndsWith("/json", StringComparison.Ordinal) || m.EndsWith("+json", StringComparison.Ordinal);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - _time.GetUtcNow();
        return null;
    }

    private static FetchResult Classify(HttpRequestException e, int? lastStatus)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case UnsafeAddressException unsafeAddress:
                    return FetchResult.UnsafeTarget(unsafeAddress.Message, lastStatus);
                case FeedDnsException dns:
                    return FetchResult.Failed(FeedFailures.Dns, lastStatus, dns.Message);
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return FetchResult.Failed(FeedFailures.Dns, lastStatus, inner.Message);
                case AuthenticationException:
                    return FetchResult.Failed(FeedFailures.Tls, lastStatus, inner.Message);
            }
        }

        return e.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => FetchResult.Failed(FeedFailures.Dns, lastStatus, e.Message),
            HttpRequestError.SecureConnectionError => FetchResult.Failed(FeedFailures.Tls, lastStatus, e.Message),
            // Không có giá trị "không kết nối được" trong AggregationFeedFailure: từ chối kết nối/ngắt giữa chừng xếp vào TIMEOUT (nguồn không trả lời).
            _ => FetchResult.Failed(FeedFailures.Timeout, lastStatus, e.Message),
        };
    }
}

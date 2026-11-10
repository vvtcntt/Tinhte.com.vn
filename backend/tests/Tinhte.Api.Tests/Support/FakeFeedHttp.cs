using System.Net;
using System.Text;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Infrastructure.Collection;

namespace Tinhte.Api.Tests.Support;

/// <summary>
/// HTTP giả cho bộ thu thập: không bao giờ gọi mạng thật. Mỗi URL tuyệt đối → hàm trả response; URL không khai báo → 404.
/// Ghi lại mọi request đã nhận (kiểm User-Agent, If-None-Match, "không có kết nối nào tới địa chỉ nội bộ").
/// </summary>
internal sealed class FakeFeedHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeFeedHandler On(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes[url] = respond;
        return this;
    }

    public FakeFeedHandler OnFeed(string url, string body, string contentType = "application/rss+xml; charset=utf-8", string? etag = null) =>
        On(url, request =>
        {
            if (etag is not null && request.Headers.TryGetValues("If-None-Match", out var sent) && sent.Contains(etag))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = Text(HttpStatusCode.OK, body, contentType);
            if (etag is not null) response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
            return response;
        });

    public FakeFeedHandler Redirect(string from, string to, HttpStatusCode status = HttpStatusCode.Found) =>
        On(from, _ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
            return response;
        });

    public static HttpResponseMessage Text(HttpStatusCode status, string body, string contentType)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return _routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var respond)
            ? respond(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
    }
}

/// <summary>Đồng hồ cố định/điều khiển được cho test.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class FeedFixtures
{
    public static readonly BotUserAgent UserAgent = new("TinhteNewsBot/1.0 (+http://www.tinhte.localhost:8080/bot)");

    public static IFeedHostResolver Resolver(params (string Host, string Ip)[] extra) =>
        new FakeFeedHostResolver(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["news.example.org"] = ["93.184.215.14"],
            ["cdn.example.org"] = ["93.184.215.15"],
            ["rebind.example.org"] = ["127.0.0.1"],
        }.Concat(extra.Select(e => new KeyValuePair<string, string[]>(e.Host, [e.Ip]))).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));

    public static FeedFetcher Fetcher(FakeFeedHandler handler, TimeProvider? time = null, FeedFetcherLimits? limits = null, IFeedHostResolver? resolver = null) =>
        new(handler, resolver ?? Resolver(), UserAgent, time ?? TimeProvider.System, limits);

    /// <summary>RSS 2.0 với các mục (guid, link, tiêu đề, mô tả, category, pubDate RFC 822).</summary>
    public static string Rss(params (string Guid, string Link, string Title, string Description, string Category, DateTimeOffset Published)[] items)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="utf-8"?><rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/"><channel><title>Báo Mẫu</title><ttl>30</ttl>""");
        foreach (var i in items)
        {
            sb.Append("<item><guid isPermaLink=\"false\">").Append(i.Guid).Append("</guid><link>").Append(i.Link).Append("</link><title><![CDATA[")
                .Append(i.Title).Append("]]></title><description><![CDATA[").Append(i.Description).Append("]]></description><category>")
                .Append(i.Category).Append("</category><pubDate>").Append(i.Published.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append("</pubDate><dc:creator>Phóng viên</dc:creator></item>");
        }

        return sb.Append("</channel></rss>").ToString();
    }
}

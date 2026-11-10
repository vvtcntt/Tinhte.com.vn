using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>
/// Parser RSS 2.0, RSS 1.0 (RDF), Atom 1.0, JSON Feed 1.x (ADR-0008 §5, BA-01 Phụ lục A) — "khoan dung nhưng an toàn":
/// <list type="bullet">
///   <item>XML: <c>DtdProcessing.Prohibit</c>, <c>XmlResolver = null</c> → có DOCTYPE/entity ngoài là từ chối cả feed (chống XXE, bom entity);</item>
///   <item>chịu được: BOM, khai báo encoding sai (UTF-8 nhưng thực tế windows-1258), ký tự điều khiển, entity HTML chưa khai báo
///         (<c>&amp;nbsp;</c>), dấu &amp; trần, link tương đối;</item>
///   <item>mục hỏng thì bỏ mục (đếm vào <see cref="ParsedFeed.SkippedItems"/>), không bỏ cả feed; tối đa <see cref="MaxItems"/> mục.</item>
/// </list>
/// Kích thước đã được bộ thu thập giới hạn (5 MB sau giải nén) trước khi vào đây.
/// </summary>
public static partial class FeedParser
{
    public const int MaxItems = 500;

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Rss1 = "http://purl.org/rss/1.0/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Sy = "http://purl.org/rss/1.0/modules/syndication/";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static FeedParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="body">Nội dung đã tải (≤ 5 MB).</param>
    /// <param name="httpCharset">charset của Content-Type (nếu có).</param>
    /// <param name="expectedFormat">Định dạng khai báo của feed; <c>AUTO</c> = tự nhận. Khác định dạng thực tế → PARSE_ERROR.</param>
    /// <param name="naiveZone">Múi giờ dùng cho thời điểm không ghi múi (giờ tòa soạn).</param>
    /// <param name="baseUri">URL của feed (giải link tương đối).</param>
    public static ParsedFeed Parse(byte[] body, string? httpCharset, string expectedFormat, TimeZoneInfo naiveZone, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(body);
        var json = FirstSignificantByte(body) == (byte)'{';
        var text = Decode(body, httpCharset, json);
        var feed = json ? ParseJsonFeed(text, naiveZone, baseUri) : ParseXml(text, naiveZone, baseUri);
        if (expectedFormat != FeedFormats.Auto && expectedFormat != feed.Format)
        {
            throw new FeedParseException($"The feed is {feed.Format}, not the declared {expectedFormat}.");
        }

        return feed;
    }

    // ---------------------------------------------------------------- encoding

    /// <summary>
    /// Thứ tự: BOM → khai báo encoding trong XML → charset HTTP → UTF-8. UTF-8 (khai báo hoặc mặc định) mà byte không hợp lệ
    /// → thử windows-1258 (lỗi thường gặp của site Việt, BA-01 Phụ lục A). Charset lạ/không hỗ trợ → UTF-8 thay ký tự hỏng.
    /// </summary>
    internal static string Decode(byte[] body, string? httpCharset, bool json)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF) return Utf8OrFallback(body.AsSpan(3));
        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE) return Encoding.Unicode.GetString(body, 2, body.Length - 2);
        if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(body, 2, body.Length - 2);

        var declared = json ? null : DeclaredXmlEncoding(body);
        var name = declared ?? httpCharset;
        Encoding? encoding = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                encoding = Encoding.GetEncoding(name.Trim().Trim('"', '\''));
            }
            catch (ArgumentException)
            {
                encoding = null;
            }
        }

        if (encoding is null || encoding.CodePage == Encoding.UTF8.CodePage) return Utf8OrFallback(body);
        if (encoding is UnicodeEncoding or UTF32Encoding) return Utf8OrFallback(body);   // không có BOM mà khai UTF-16: gần như chắc là sai
        return encoding.GetString(body);
    }

    private static string Utf8OrFallback(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1258).GetString(bytes);
        }
    }

    private static string? DeclaredXmlEncoding(byte[] body)
    {
        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 256));
        var m = XmlDeclarationEncoding().Match(head);
        return m.Success ? m.Groups["enc"].Value : null;
    }

    private static byte FirstSignificantByte(byte[] body)
    {
        var start = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;
        for (var i = start; i < body.Length; i++)
        {
            if (body[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return body[i];
        }

        return 0;
    }

    // ---------------------------------------------------------------- XML

    private static ParsedFeed ParseXml(string text, TimeZoneInfo zone, Uri baseUri)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,   // DOCTYPE → XmlException: không DTD, không entity tự định nghĩa (XXE, billion laughs)
            XmlResolver = null,                       // không bao giờ đọc tệp/URL ngoài
            MaxCharactersFromEntities = 1024,
            CheckCharacters = false,                  // ký tự điều khiển đã lọc; tham chiếu ký tự lạ không làm hỏng cả feed
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(RepairXml(text)), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException e)
        {
            throw new FeedParseException("The document is not well-formed XML or declares a DTD: " + e.Message, e);
        }

        var root = doc.Root ?? throw new FeedParseException("Empty XML document.");
        return root.Name.LocalName switch
        {
            "rss" => ParseRss2(root, zone, baseUri),
            "RDF" when root.Name.Namespace == Rdf => ParseRss1(root, zone, baseUri),
            "feed" when root.Name.Namespace == Atom => ParseAtom(root, zone, baseUri),
            _ => throw new FeedParseException($"<{root.Name.LocalName}> is not an RSS 2.0, RSS 1.0 or Atom 1.0 root element."),
        };
    }

    /// <summary>Bỏ ký tự XML không hợp lệ, đổi entity HTML chưa khai báo sang tham chiếu số, thoát &amp; trần.</summary>
    internal static string RepairXml(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c != '￾' && c != '￿')) sb.Append(c);
        }

        return Ampersands().Replace(sb.ToString(), m =>
        {
            var reference = m.Groups["ref"];
            if (!reference.Success) return "&amp;";
            var value = reference.Value;
            if (value[0] == '#' || value is "amp;" or "lt;" or "gt;" or "quot;" or "apos;") return m.Value;
            var decoded = WebUtility.HtmlDecode("&" + value);
            if (decoded == "&" + value) return "&amp;" + value;
            var refs = new StringBuilder();
            foreach (var rune in decoded.EnumerateRunes()) refs.Append("&#").Append(rune.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
            return refs.ToString();
        });
    }

    private static ParsedFeed ParseRss2(XElement root, TimeZoneInfo zone, Uri baseUri)
    {
        var channel = root.Element("channel") ?? throw new FeedParseException("RSS 2.0 without <channel>.");
        var ttl = int.TryParse(channel.Element("ttl")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : (int?)null;
        return Collect(FeedFormats.Rss2, channel.Element("title")?.Value, ttl ?? UpdatePeriod(channel), channel.Elements("item"), item =>
        {
            var guidElement = item.Element("guid");
            var guid = guidElement?.Value.Trim();
            var permaLink = !string.Equals(guidElement?.Attribute("isPermaLink")?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            var link = item.Element("link")?.Value.Trim();
            if (string.IsNullOrEmpty(link) && permaLink) link = guid;
            return new RawFeedItem(
                Guid: Blank(guid),
                Link: Blank(link),
                Title: item.Element("title")?.Value,
                Summary: item.Element("description")?.Value,
                Author: Blank(item.Element(Dc + "creator")?.Value) ?? item.Element("author")?.Value,
                Categories: item.Elements("category").Concat(item.Elements(Dc + "subject")).Select(c => c.Value).ToList(),
                Published: FeedDates.Parse(item.Element("pubDate")?.Value ?? item.Element(Dc + "date")?.Value, zone),
                Updated: null);
        }, baseUri);
    }

    private static ParsedFeed ParseRss1(XElement root, TimeZoneInfo zone, Uri baseUri)
    {
        var channel = root.Element(Rss1 + "channel");
        return Collect(FeedFormats.Rss1, channel?.Element(Rss1 + "title")?.Value, channel is null ? null : UpdatePeriod(channel), root.Elements(Rss1 + "item"),
            item => new RawFeedItem(
                Guid: Blank(item.Attribute(Rdf + "about")?.Value),
                Link: Blank(item.Element(Rss1 + "link")?.Value),
                Title: item.Element(Rss1 + "title")?.Value,
                Summary: item.Element(Rss1 + "description")?.Value,
                Author: item.Element(Dc + "creator")?.Value,
                Categories: item.Elements(Dc + "subject").Select(c => c.Value).ToList(),
                Published: FeedDates.Parse(item.Element(Dc + "date")?.Value, zone),
                Updated: null), baseUri);
    }

    private static ParsedFeed ParseAtom(XElement root, TimeZoneInfo zone, Uri baseUri)
    {
        var feedBase = XmlBase(root, baseUri);
        var feedAuthor = root.Element(Atom + "author")?.Element(Atom + "name")?.Value;
        return Collect(FeedFormats.Atom, root.Element(Atom + "title")?.Value, null, root.Elements(Atom + "entry"), entry =>
        {
            var entryBase = XmlBase(entry, feedBase);
            var links = entry.Elements(Atom + "link")
                .Where(l => (l.Attribute("rel")?.Value ?? "alternate") == "alternate")
                .OrderByDescending(l => l.Attribute("type")?.Value is null or "text/html")
                .Select(l => l.Attribute("href")?.Value)
                .FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));
            var resolved = links is null ? null : TextNormalizer.AbsoluteHttpUrl(links, entryBase)?.AbsoluteUri;
            return new RawFeedItem(
                Guid: Blank(entry.Element(Atom + "id")?.Value),
                Link: resolved ?? links,
                Title: entry.Element(Atom + "title")?.Value,
                Summary: entry.Element(Atom + "summary")?.Value,
                Author: entry.Element(Atom + "author")?.Element(Atom + "name")?.Value ?? feedAuthor,
                Categories: entry.Elements(Atom + "category").Select(c => c.Attribute("label")?.Value ?? c.Attribute("term")?.Value ?? "").ToList(),
                Published: FeedDates.Parse(entry.Element(Atom + "published")?.Value, zone) ?? FeedDates.Parse(entry.Element(Atom + "updated")?.Value, zone),
                Updated: FeedDates.Parse(entry.Element(Atom + "updated")?.Value, zone));
        }, baseUri);
    }

    private static Uri XmlBase(XElement element, Uri parent)
    {
        var value = element.Attribute(XNamespace.Xml + "base")?.Value;
        return value is not null && TextNormalizer.AbsoluteHttpUrl(value, parent) is { } resolved ? resolved : parent;
    }

    /// <summary>sy:updatePeriod × sy:updateFrequency → số phút giữa hai lần cập nhật.</summary>
    private static int? UpdatePeriod(XElement channel)
    {
        var minutes = channel.Element(Sy + "updatePeriod")?.Value.Trim().ToLowerInvariant() switch
        {
            "hourly" => 60,
            "daily" => 1440,
            "weekly" => 10080,
            "monthly" => 43200,
            "yearly" => 525600,
            _ => (int?)null,
        };
        if (minutes is null) return null;
        var frequency = int.TryParse(channel.Element(Sy + "updateFrequency")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 1;
        return Math.Max(1, minutes.Value / frequency);
    }

    // ---------------------------------------------------------------- JSON Feed

    private static ParsedFeed ParseJsonFeed(string text, TimeZoneInfo zone, Uri baseUri)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new FeedParseException("The document is not valid JSON: " + e.Message, e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "version") is not { } version
                || !version.StartsWith("https://jsonfeed.org/version/1", StringComparison.Ordinal))
            {
                throw new FeedParseException("JSON document is not a JSON Feed 1.x (missing version).");
            }

            var items = root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToList() : [];
            var feedAuthor = AuthorOf(root);
            return Collect(FeedFormats.JsonFeed, Str(root, "title"), null, items, item =>
            {
                if (item.ValueKind != JsonValueKind.Object) throw new FeedParseException("item is not an object");
                var id = item.TryGetProperty("id", out var idValue) ? idValue.ValueKind switch
                {
                    JsonValueKind.String => idValue.GetString(),
                    JsonValueKind.Number => idValue.GetRawText(),
                    _ => null,
                } : null;
                return new RawFeedItem(
                    Guid: Blank(id),
                    Link: Blank(Str(item, "url")) ?? Blank(Str(item, "external_url")),
                    Title: Str(item, "title"),
                    Summary: Str(item, "summary"),
                    Author: AuthorOf(item) ?? feedAuthor,
                    Categories: item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                        ? tags.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                        : [],
                    Published: FeedDates.Parse(Str(item, "date_published"), zone) ?? FeedDates.Parse(Str(item, "date_modified"), zone),
                    Updated: FeedDates.Parse(Str(item, "date_modified"), zone));
            }, baseUri);
        }
    }

    private static string? AuthorOf(JsonElement element)
    {
        if (element.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
        {
            var first = authors.EnumerateArray().FirstOrDefault(a => a.ValueKind == JsonValueKind.Object);
            if (first.ValueKind == JsonValueKind.Object && Str(first, "name") is { } name) return name;
        }

        return element.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object ? Str(author, "name") : null;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ---------------------------------------------------------------- chung

    private static ParsedFeed Collect<T>(string format, string? title, int? refreshHint, IEnumerable<T> nodes, Func<T, RawFeedItem> read, Uri baseUri)
    {
        var items = new List<RawFeedItem>();
        var skipped = 0;
        foreach (var node in nodes)
        {
            if (items.Count >= MaxItems)
            {
                skipped++;
                continue;
            }

            try
            {
                var item = read(node);
                // Link tương đối → tuyệt đối theo URL feed (Atom đã giải theo xml:base).
                var link = TextNormalizer.AbsoluteHttpUrl(item.Link, baseUri)?.AbsoluteUri;
                items.Add(item with { Link = link });
            }
            catch (Exception e) when (e is FeedParseException or FormatException or InvalidOperationException or RegexMatchTimeoutException or ArgumentException)
            {
                skipped++;   // mục hỏng: bỏ mục, giữ feed
            }
        }

        return new ParsedFeed(format, TextNormalizer.HtmlToText(title, 300), refreshHint, items, skipped);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^\s*<\?xml[^>]*?encoding\s*=\s*[""'](?<enc>[A-Za-z0-9._-]{1,40})[""']", RegexOptions.CultureInvariant)]
    private static partial Regex XmlDeclarationEncoding();

    [GeneratedRegex(@"&(?<ref>#[0-9]{1,7};|#[xX][0-9a-fA-F]{1,6};|[A-Za-z][A-Za-z0-9]{0,31};)?", RegexOptions.CultureInvariant)]
    private static partial Regex Ampersands();
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tinhte.Platform.Content;

/// <summary>
/// Kiểm thân bài/video dạng khối (schema BodyBlock / BodyRichText / BodyTextRun của hợp đồng v0.3, DRAFT CHANGELOG 22) — dùng chung
/// cho News và Video. Thân bài là DỮ LIỆU, không phải HTML: renderer luôn escape chữ; ở đây chỉ chặn dạng sai, link nguy hiểm
/// (<c>javascript:</c>, <c>data:</c>…) và kích thước quá lớn.
/// <list type="bullet">
///   <item>Mọi khối: object có <c>type</c> là chuỗi 1–40 ký tự. Loại lạ được GIỮ nguyên (renderer bỏ qua) — chỉ kiểm <c>type</c>.</item>
///   <item>Loại đã biết: paragraph/heading/quote/list/image/table kiểm đúng hình dạng; thuộc tính thừa được giữ (schema không cấm).</item>
///   <item>Có <c>runs</c> thì <c>text</c> phải bằng nối <c>runs[].text</c>, để client chỉ đọc <c>text</c> vẫn hiện đúng chữ.</item>
/// </list>
/// Vi phạm đầu tiên được trả về kèm đường dẫn trường chính xác, ví dụ <c>bodyBlocks[3].runs[0].href</c>.
/// </summary>
public static partial class BodyBlocks
{
    public const int MaxBlocks = 2000;
    public const int MaxTypeLength = 40;
    public const int MaxTextLength = 20_000;
    public const int MaxRuns = 200;
    public const int MaxListItems = 200;
    public const int MaxRows = 100;
    public const int MaxCells = 20;
    public const int MaxAltLength = 300;
    public const int MaxCaptionLength = 500;
    public const int MaxHrefLength = 2000;

    public const string Paragraph = "paragraph";
    public const string Heading = "heading";
    public const string Quote = "quote";
    public const string List = "list";
    public const string Image = "image";
    public const string Table = "table";

    public static readonly IReadOnlySet<string> KnownTypes = new HashSet<string>(StringComparer.Ordinal) { Paragraph, Heading, Quote, List, Image, Table };

    public static readonly IReadOnlySet<string> Marks = new HashSet<string>(StringComparer.Ordinal) { "bold", "italic", "underline", "strike" };

    public static readonly IReadOnlySet<string> Aligns = new HashSet<string>(StringComparer.Ordinal) { "left", "center", "right" };

    /// <summary>Một vi phạm: <see cref="Path"/> là khóa trong <c>errors</c> của 422 VALIDATION_FAILED.</summary>
    public sealed record Violation(string Path, string Message);

    /// <summary>Kiểm cả mảng khối. null nếu hợp lệ. <paramref name="field"/> = tên trường trong request (bodyBlocks / initialBodyBlocks).</summary>
    public static Violation? Validate(JsonElement value, string field = "bodyBlocks")
    {
        if (value.ValueKind != JsonValueKind.Array) return new(field, "Must be an array of blocks.");
        if (value.GetArrayLength() > MaxBlocks) return new(field, $"At most {MaxBlocks} blocks.");

        var index = 0;
        foreach (var block in value.EnumerateArray())
        {
            if (Block(block, $"{field}[{index}]") is { } violation) return violation;
            index++;
        }

        return null;
    }

    /// <summary>mediaId của mọi khối image (theo thứ tự, kèm vị trí khối) — gọi sau khi <see cref="Validate"/> đã qua.</summary>
    public static IReadOnlyList<(int Index, Guid MediaId)> ImageMediaIds(JsonElement value)
    {
        var result = new List<(int, Guid)>();
        if (value.ValueKind != JsonValueKind.Array) return result;
        var index = 0;
        foreach (var block in value.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == Image
                && block.TryGetProperty("mediaId", out var media) && media.ValueKind == JsonValueKind.String && media.TryGetGuid(out var mediaId))
            {
                result.Add((index, mediaId));
            }

            index++;
        }

        return result;
    }

    private static Violation? Block(JsonElement block, string path)
    {
        if (block.ValueKind != JsonValueKind.Object) return new(path, "Every block must be a JSON object.");
        var type = block.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString()! : null;
        if (type is null || type.Length > MaxTypeLength || string.IsNullOrWhiteSpace(type))
        {
            return new($"{path}.type", $"Required string of 1–{MaxTypeLength} characters.");
        }

        return type switch
        {
            Paragraph => RichText(block, path) ?? Align(block, path),
            Heading => HeadingLevel(block, path) ?? RichText(block, path) ?? Align(block, path),
            Quote => RichText(block, path) ?? Align(block, path),
            List => ListBlock(block, path),
            Image => ImageBlock(block, path),
            Table => TableBlock(block, path),
            _ => null,   // loại chưa biết: giữ nguyên, renderer bỏ qua
        };
    }

    private static Violation? HeadingLevel(JsonElement block, string path) =>
        block.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var n) && n is 2 or 3
            ? null
            : new($"{path}.level", "Required: 2 or 3.");

    private static Violation? Align(JsonElement block, string path) =>
        !block.TryGetProperty("align", out var align) || (align.ValueKind == JsonValueKind.String && Aligns.Contains(align.GetString()!))
            ? null
            : new($"{path}.align", "Must be left, center or right.");

    /// <summary>BodyRichText (và phần text/runs của paragraph/heading/quote): text bắt buộc; runs tùy chọn, nối lại phải bằng text.</summary>
    private static Violation? RichText(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) return new(path, "Must be an object with text.");
        if (!element.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
        {
            return new($"{path}.text", "Required string.");
        }

        var text = textElement.GetString()!;
        if (text.Length > MaxTextLength) return new($"{path}.text", $"At most {MaxTextLength} characters.");
        if (!element.TryGetProperty("runs", out var runs)) return null;
        if (runs.ValueKind != JsonValueKind.Array) return new($"{path}.runs", "Must be an array of runs.");
        if (runs.GetArrayLength() > MaxRuns) return new($"{path}.runs", $"At most {MaxRuns} runs.");

        var joined = new StringBuilder();
        var index = 0;
        foreach (var run in runs.EnumerateArray())
        {
            var runPath = $"{path}.runs[{index}]";
            if (Run(run, runPath) is { } violation) return violation;
            joined.Append(run.GetProperty("text").GetString());
            if (joined.Length > MaxTextLength) break;
            index++;
        }

        return string.Equals(joined.ToString(), text, StringComparison.Ordinal)
            ? null
            : new($"{path}.text", "Must equal the concatenation of runs[].text.");
    }

    private static Violation? Run(JsonElement run, string path)
    {
        if (run.ValueKind != JsonValueKind.Object) return new(path, "Every run must be an object.");
        if (!run.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || text.GetString() is not { Length: > 0 and <= MaxTextLength })
        {
            return new($"{path}.text", $"Required string of 1–{MaxTextLength} characters.");
        }

        if (run.TryGetProperty("marks", out var marks))
        {
            if (marks.ValueKind != JsonValueKind.Array) return new($"{path}.marks", "Must be an array.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mark in marks.EnumerateArray())
            {
                if (mark.ValueKind != JsonValueKind.String || !Marks.Contains(mark.GetString()!)) return new($"{path}.marks", "Allowed marks: bold, italic, underline, strike.");
                if (!seen.Add(mark.GetString()!)) return new($"{path}.marks", "Marks must be unique.");
            }
        }

        if (run.TryGetProperty("href", out var href) && (href.ValueKind != JsonValueKind.String || !IsSafeHref(href.GetString()!)))
        {
            return new($"{path}.href", $"Must be an absolute http, https or mailto URL of at most {MaxHrefLength} characters.");
        }

        foreach (var name in new[] { "color", "highlight" })
        {
            if (run.TryGetProperty(name, out var color) && (color.ValueKind != JsonValueKind.String || !HexColor().IsMatch(color.GetString()!)))
            {
                return new($"{path}.{name}", "Must be a 6-digit hex color (#rrggbb).");
            }
        }

        return null;
    }

    /// <summary>
    /// Link được phép: URI tuyệt đối http/https (có host) hoặc mailto, ≤ 2000 ký tự, không khoảng trắng/ký tự điều khiển
    /// (chặn kiểu " javascript:" hay "java\tscript:" mà trình duyệt vẫn chạy). Mọi scheme khác (javascript, data, vbscript, file…) bị từ chối.
    /// </summary>
    public static bool IsSafeHref(string value)
    {
        if (value.Length is 0 or > MaxHrefLength) return false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        // Scheme lấy từ chính chuỗi (Uri có thể tự hiểu "C:/x" là file:).
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        var scheme = colon > 0 ? value[..colon].ToLowerInvariant() : "";
        return scheme switch
        {
            "http" or "https" => uri.Scheme == scheme && !string.IsNullOrEmpty(uri.Host),
            "mailto" => uri.Scheme == Uri.UriSchemeMailto,
            _ => false,
        };
    }

    private static Violation? ListBlock(JsonElement block, string path)
    {
        if (!block.TryGetProperty("ordered", out var ordered) || ordered.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return new($"{path}.ordered", "Required boolean.");
        }

        if (!block.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return new($"{path}.items", "Required array of items.");
        if (items.GetArrayLength() > MaxListItems) return new($"{path}.items", $"At most {MaxListItems} items.");
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (RichText(item, $"{path}.items[{index}]") is { } violation) return violation;
            index++;
        }

        return null;
    }

    private static Violation? ImageBlock(JsonElement block, string path)
    {
        if (!block.TryGetProperty("mediaId", out var media) || media.ValueKind != JsonValueKind.String || !media.TryGetGuid(out _))
        {
            return new($"{path}.mediaId", "Required uuid of an IMAGE media asset.");
        }

        if (!block.TryGetProperty("alt", out var alt) || alt.ValueKind != JsonValueKind.String || alt.GetString()!.Length > MaxAltLength)
        {
            return new($"{path}.alt", $"Required string of at most {MaxAltLength} characters.");
        }

        return Caption(block, path) ?? Align(block, path);
    }

    private static Violation? TableBlock(JsonElement block, string path)
    {
        if (!block.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return new($"{path}.rows", "Required array of rows.");
        if (rows.GetArrayLength() > MaxRows) return new($"{path}.rows", $"At most {MaxRows} rows.");
        var r = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var rowPath = $"{path}.rows[{r}]";
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() is 0 or > MaxCells)
            {
                return new(rowPath, $"Every row must be an array of 1–{MaxCells} cells.");
            }

            var c = 0;
            foreach (var cell in row.EnumerateArray())
            {
                if (RichText(cell, $"{rowPath}[{c}]") is { } violation) return violation;
                c++;
            }

            r++;
        }

        if (block.TryGetProperty("headerRow", out var header) && header.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return new($"{path}.headerRow", "Must be a boolean.");
        }

        return Caption(block, path);
    }

    private static Violation? Caption(JsonElement block, string path) =>
        !block.TryGetProperty("caption", out var caption) || (caption.ValueKind == JsonValueKind.String && caption.GetString()!.Length <= MaxCaptionLength)
            ? null
            : new($"{path}.caption", $"Must be a string of at most {MaxCaptionLength} characters.");

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();
}

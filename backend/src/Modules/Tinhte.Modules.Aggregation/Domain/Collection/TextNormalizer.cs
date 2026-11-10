using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>
/// Làm sạch và chuẩn hóa văn bản/URL của feed (ADR-0008 §6, BR-10, BR-16). Kết quả luôn là văn bản thuần: CMS không bao giờ nhận HTML của nguồn.
/// </summary>
public static partial class TextNormalizer
{
    /// <summary>Tham số theo dõi bị bỏ khi chuẩn hóa URL (BR-10) — ngoài mọi <c>utm_*</c>.</summary>
    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "gclsrc", "dclid", "msclkid", "mc_cid", "mc_eid", "igshid", "_ga", "yclid",
    };

    /// <summary>
    /// HTML (hoặc HTML đã escape một lần nữa) → văn bản thuần: bỏ script/style/iframe/form…, thẻ, comment; giải mã entity;
    /// bỏ ký tự điều khiển; gộp khoảng trắng; cắt ở <paramref name="maxLength"/> ký tự (không cắt đôi cặp surrogate). Rỗng → null.
    /// </summary>
    public static string? HtmlToText(string? html, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = html;
        for (var pass = 0; pass < 3; pass++)
        {
            text = DangerousBlocks().Replace(text, " ");
            text = Comments().Replace(text, " ");
            text = Tags().Replace(text, " ");
            var decoded = WebUtility.HtmlDecode(text);
            var done = decoded == text || !LooksLikeMarkup().IsMatch(decoded);
            text = decoded;
            if (done) break;
        }

        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 16));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '​' or '﻿')
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
            if (sb.Length >= maxLength) break;
        }

        var result = sb.ToString().TrimEnd();
        if (result.Length > maxLength) result = result[..maxLength];
        if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result = result[..^1];
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Dạng so khớp: NFC (nếu nền tảng hỗ trợ), chữ thường, mọi ký tự không phải chữ/số/dấu thành khoảng trắng, gộp khoảng trắng.
    /// GIỮ dấu tiếng Việt: bỏ dấu sẽ làm "đảng" trùng "đang", "bão" trùng "bao" (từ khóa nhạy cảm/chuyên mục khớp nhầm).
    /// </summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = Nfc(text).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            var category = char.GetUnicodeCategory(c);
            var keep = char.IsLetterOrDigit(c) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            if (!keep)
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// URL tuyệt đối http(s) của mục tin (giải link tương đối theo <paramref name="baseUri"/>), bỏ fragment. Không hợp lệ → null.
    /// Đây là URL lưu và trả cho BTV; khóa khử trùng dùng <see cref="NormalizeUrl"/>.
    /// </summary>
    public static Uri? AbsoluteHttpUrl(string? raw, Uri? baseUri)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 2048) return null;
        Uri? uri;
        if (!Uri.TryCreate(s, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            if (baseUri is null || !Uri.TryCreate(baseUri, s, out uri)) return null;
        }

        if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var builder = new UriBuilder(uri) { Fragment = "" };
        var result = builder.Uri;
        return result.AbsoluteUri.Length > 2048 ? null : result;
    }

    /// <summary>
    /// URL chuẩn hóa cho khóa khử trùng (BR-10): scheme + host chữ thường, bỏ cổng mặc định, fragment, <c>utm_*</c>/fbclid/gclid…,
    /// sắp xếp query còn lại, bỏ dấu "/" cuối đường dẫn.
    /// </summary>
    public static string NormalizeUrl(Uri url)
    {
        var path = url.AbsolutePath;
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path == "/") path = "";
        var query = url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p =>
            {
                var key = Uri.UnescapeDataString(p.Split('=')[0]);
                return !key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !TrackingParameters.Contains(key);
            })
            .Order(StringComparer.Ordinal)
            .ToList();
        var port = url.IsDefaultPort ? "" : ":" + url.Port.ToString(CultureInfo.InvariantCulture);
        return $"{url.Scheme}://{url.IdnHost.ToLowerInvariant()}{port}{path}{(query.Count > 0 ? "?" + string.Join('&', query) : "")}";
    }

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>NFC nếu nền tảng hỗ trợ (chế độ InvariantGlobalization trên một số nền tảng không chuẩn hóa được) — không có thì giữ nguyên.</summary>
    private static string Nfc(string text)
    {
        try
        {
            return text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
        }
        catch (PlatformNotSupportedException)
        {
            return text;
        }
    }

    [GeneratedRegex(@"<(script|style|iframe|form|noscript|template|svg|object|embed|select|textarea)\b[^>]*>[\s\S]*?</\1\s*>|<(script|style|iframe|form|noscript|template|svg|object|embed)\b[^>]*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex DangerousBlocks();

    [GeneratedRegex(@"<!--[\s\S]*?-->", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<[A-Za-z/!?][^<>]*>", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Tags();

    [GeneratedRegex(@"<[A-Za-z/!?][^<>]*>", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex LooksLikeMarkup();
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>
/// Đọc thời điểm của feed một cách khoan dung (BA-01 Phụ lục A): RFC 822/1123 của RSS (thứ thừa/thiếu, tên múi giờ cũ, năm 2 chữ số),
/// RFC 3339/ISO 8601 của Atom/JSON Feed/dc:date. Không có múi giờ → hiểu theo giờ tòa soạn. Không đọc được → null (mục vẫn nhận,
/// thời điểm "không tin được" — BR-11).
/// </summary>
public static partial class FeedDates
{
    private static readonly Dictionary<string, int> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GMT"] = 0, ["UT"] = 0, ["UTC"] = 0, ["Z"] = 0,
        ["EST"] = -5 * 60, ["EDT"] = -4 * 60, ["CST"] = -6 * 60, ["CDT"] = -5 * 60,
        ["MST"] = -7 * 60, ["MDT"] = -6 * 60, ["PST"] = -8 * 60, ["PDT"] = -7 * 60,
        ["ICT"] = 7 * 60, ["WIB"] = 7 * 60, ["SGT"] = 8 * 60, ["HKT"] = 8 * 60, ["JST"] = 9 * 60, ["KST"] = 9 * 60,
        ["CET"] = 60, ["CEST"] = 120, ["BST"] = 60,
    };

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    public static DateTimeOffset? Parse(string? raw, TimeZoneInfo naiveZone)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 100) return null;

        var iso = IsoPattern().Match(s);
        if (iso.Success)
        {
            if (iso.Groups["zone"].Success)
            {
                return DateTimeOffset.TryParse(s.Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var withZone)
                    ? withZone.ToUniversalTime()
                    : null;
            }

            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive) ? FromNaive(naive, naiveZone) : null;
        }

        return ParseRfc822(s, naiveZone);
    }

    private static DateTimeOffset? ParseRfc822(string s, TimeZoneInfo naiveZone)
    {
        // "Tue, 10 Nov 2026 08:15:00 +0700", "10 Nov 2026 8:15 GMT", "Tuesday, 10 November 26 08:15:00", "Nov 10 2026 08:15:00 +07:00"
        var tokens = s.Replace(",", " ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count > 0 && !char.IsDigit(tokens[0][0]) && MonthOf(tokens[0]) is null) tokens.RemoveAt(0);   // thứ trong tuần
        if (tokens.Count < 3) return null;

        int day, month, year;
        if (MonthOf(tokens[0]) is { } m0 && int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d0))
        {
            (month, day) = (m0, d0);   // "Nov 10 2026"
        }
        else if (int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var d1) && MonthOf(tokens[1]) is { } m1)
        {
            (day, month) = (d1, m1);
        }
        else
        {
            return null;
        }

        if (!int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out year)) return null;
        if (tokens[2].Length == 2) year += year < 50 ? 2000 : 1900;

        int hour = 0, minute = 0, second = 0;
        var next = 3;
        if (tokens.Count > 3 && tokens[3].Contains(':', StringComparison.Ordinal))
        {
            var parts = tokens[3].Split(':');
            if (parts.Length is < 2 or > 3
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute)
                || (parts.Length == 3 && !int.TryParse(parts[2].Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out second)))
            {
                return null;
            }

            next = 4;
        }

        if (day is < 1 or > 31 || year is < 1970 or > 9999 || hour > 23 || minute > 59 || second > 60) return null;
        if (second == 60) second = 59;
        DateTime local;
        try
        {
            local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;   // 31/02…
        }

        if (tokens.Count <= next) return FromNaive(local, naiveZone);
        var offset = OffsetOf(tokens[next]);
        return offset is { } minutes ? new DateTimeOffset(local, TimeSpan.FromMinutes(minutes)).ToUniversalTime() : FromNaive(local, naiveZone);
    }

    private static int? MonthOf(string token)
    {
        if (token.Length < 3) return null;
        var index = Array.IndexOf(Months, token[..3].ToLowerInvariant());
        return index < 0 ? null : index + 1;
    }

    private static int? OffsetOf(string token)
    {
        if (Zones.TryGetValue(token, out var named)) return named;
        var m = OffsetPattern().Match(token);
        if (!m.Success) return null;
        var hours = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        if (hours > 14 || minutes > 59) return null;
        var total = (hours * 60) + minutes;
        return m.Groups["sign"].Value == "-" ? -total : total;
    }

    private static DateTimeOffset FromNaive(DateTime naive, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(naive, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(?:[T ]\d{1,2}:\d{2}(?::\d{2}(?:[.,]\d{1,9})?)?)?\s*(?<zone>Z|[+-]\d{2}:?\d{2})?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoPattern();

    [GeneratedRegex(@"^(?<sign>[+-])(?<h>\d{2}):?(?<m>\d{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPattern();
}

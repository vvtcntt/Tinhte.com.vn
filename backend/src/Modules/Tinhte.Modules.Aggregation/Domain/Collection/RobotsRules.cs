namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>
/// robots.txt theo RFC 9309 (BR-03): chọn nhóm có <c>user-agent</c> khớp product token của bot (không phân biệt hoa thường),
/// không có thì nhóm <c>*</c>. Luật khớp dài nhất thắng, bằng nhau thì <c>allow</c> thắng; hỗ trợ <c>*</c> và <c>$</c>.
/// </summary>
public sealed class RobotsRules
{
    private readonly IReadOnlyList<(bool Allow, string Pattern)> _rules;

    private RobotsRules(IReadOnlyList<(bool Allow, string Pattern)> rules) => _rules = rules;

    public static RobotsRules AllowAll { get; } = new([]);

    public static RobotsRules DisallowAll { get; } = new([(false, "/")]);

    public static RobotsRules Parse(string text, string productToken)
    {
        var specific = new List<(bool, string)>();
        var wildcard = new List<(bool, string)>();
        var foundSpecific = false;
        var agents = new List<string>();
        var inRules = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "user-agent":
                    if (inRules)
                    {
                        agents.Clear();
                        inRules = false;
                    }

                    agents.Add(value);
                    break;
                case "allow" or "disallow":
                    inRules = true;
                    if (value.Length == 0) break;   // "Disallow:" rỗng = không chặn gì
                    var rule = (key == "allow", value);
                    if (agents.Any(a => string.Equals(a, productToken, StringComparison.OrdinalIgnoreCase)))
                    {
                        foundSpecific = true;
                        specific.Add(rule);
                    }
                    else if (agents.Contains("*"))
                    {
                        wildcard.Add(rule);
                    }

                    break;
                default:
                    // sitemap, crawl-delay…: bỏ qua, không kết thúc nhóm (RFC 9309 §2.2.4).
                    break;
            }
        }

        // Nhóm user-agent khớp nhưng không có luật nào vẫn là "nhóm của bot" (cho phép tất cả).
        if (!foundSpecific && text.Split('\n').Any(l => IsAgentLine(l, productToken))) return AllowAll;
        return new RobotsRules(foundSpecific ? specific : wildcard);
    }

    /// <param name="pathAndQuery">Đường dẫn + query đã percent-encode (Uri.PathAndQuery).</param>
    public bool IsAllowed(string pathAndQuery)
    {
        if (pathAndQuery.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase)) return true;
        var best = -1;
        var allowed = true;
        foreach (var (allow, pattern) in _rules)
        {
            if (!Matches(pattern, pathAndQuery)) continue;
            var length = pattern.Length;
            if (length > best || (length == best && allow))
            {
                best = length;
                allowed = allow;
            }
        }

        return allowed;
    }

    private static bool IsAgentLine(string line, string productToken)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !line[..colon].Trim().Equals("user-agent", StringComparison.OrdinalIgnoreCase)) return false;
        var value = line[(colon + 1)..];
        var hash = value.IndexOf('#', StringComparison.Ordinal);
        return (hash >= 0 ? value[..hash] : value).Trim().Equals(productToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Khớp tiền tố với ký tự đại diện <c>*</c>; <c>$</c> cuối = neo cuối đường dẫn.</summary>
    internal static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        var p = anchored ? pattern[..^1] : pattern;
        if (!anchored) p += "*";
        int pi = 0, si = 0, star = -1, mark = 0;
        while (si < path.Length)
        {
            if (pi < p.Length && p[pi] != '*' && p[pi] == path[si])
            {
                pi++;
                si++;
            }
            else if (pi < p.Length && p[pi] == '*')
            {
                star = pi++;
                mark = si;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                si = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }
}

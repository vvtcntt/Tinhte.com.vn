using System.Text.Json;

namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>Luật từ khóa của một chuyên mục (tt.agg_category_rules + site của chuyên mục).</summary>
public sealed record CategoryRuleSet(Guid CategoryId, string SiteKey, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, IReadOnlyList<string> Entities);

/// <summary>Ngưỡng BR-31: ≥ AutoAssign → tự gán; [Review, AutoAssign) → gán kèm "cần xem"; &lt; Review → không thuộc chuyên mục nào.</summary>
public sealed record ClassificationThresholds(decimal AutoAssign, decimal Review)
{
    public static ClassificationThresholds Default { get; } = new(0.80m, 0.50m);
}

/// <summary>Kết quả phân loại một mục (cột site_key, category_id, category_confidence, needs_review, sensitive, status của tt.agg_items).</summary>
public sealed record Classification(string SiteKey, Guid? CategoryId, decimal? Confidence, bool NeedsReview, bool Sensitive, string Status);

/// <summary>
/// Phân loại không dùng AI (ADR-0008 §7, BR-30 tầng 1–2, BR-31, BR-34):
/// <list type="number">
///   <item>ánh xạ category của feed → chuyên mục (cấu hình theo feed): khớp ⇒ độ tin 1,0;</item>
///   <item>từ khóa của chuyên mục (bao gồm + thực thể; có từ khóa loại trừ ⇒ bỏ chuyên mục): điểm = 2 × số từ khóa khớp tiêu đề + số từ khóa
///         chỉ khớp tóm tắt; độ tin = min(0,95; 0,4 + 0,15 × điểm) — một từ khóa ở tiêu đề = 0,70 (cần xem), hai = 1,0 → 0,95 (tự gán).</item>
/// </list>
/// Khớp theo từ nguyên vẹn trên dạng <see cref="TextNormalizer.Fold"/> (chữ thường, giữ dấu). Chuyên mục chỉ lấy trong các site của nguồn;
/// mục không đủ ngưỡng ⇒ <c>OUT_OF_SCOPE</c> (vẫn tìm được ở danh sách mục tin), gán site đầu tiên của nguồn.
/// Nhạy cảm: khớp danh sách chủ đề của site (config/aggregation/sensitive-topics.json) ⇒ <c>sensitive = true</c>.
/// </summary>
public sealed class ItemClassifier
{
    public const string Classified = "CLASSIFIED";
    public const string OutOfScope = "OUT_OF_SCOPE";

    private readonly IReadOnlyList<string> _siteKeys;
    private readonly Dictionary<string, (Guid CategoryId, string SiteKey)> _mapping;
    private readonly IReadOnlyList<CategoryRuleSet> _rules;
    private readonly SensitiveTopics _sensitive;
    private readonly Func<string, ClassificationThresholds> _thresholds;

    /// <param name="siteKeys">Site của nguồn (thứ tự khai báo).</param>
    /// <param name="mapping">category của feed (không phân biệt hoa thường) → chuyên mục đang bật thuộc site của nguồn.</param>
    public ItemClassifier(IReadOnlyList<string> siteKeys, IReadOnlyDictionary<string, (Guid CategoryId, string SiteKey)> mapping,
        IReadOnlyList<CategoryRuleSet> rules, SensitiveTopics sensitive, Func<string, ClassificationThresholds> thresholds)
    {
        _siteKeys = siteKeys;
        _mapping = new Dictionary<string, (Guid, string)>(mapping, StringComparer.OrdinalIgnoreCase);
        // Từ khóa đổi sang dạng Fold một lần (mỗi feed có thể tới 500 mục × vài chục chuyên mục × 200 từ khóa).
        _rules = rules.Where(r => siteKeys.Contains(r.SiteKey, StringComparer.Ordinal))
            .Select(r => new CategoryRuleSet(r.CategoryId, r.SiteKey, FoldAll(r.Include), FoldAll(r.Exclude), FoldAll(r.Entities)))
            .ToList();
        _sensitive = sensitive;
        _thresholds = thresholds;
    }

    public Classification Classify(NormalizedItem item)
    {
        var title = " " + TextNormalizer.Fold(item.Title) + " ";
        var all = " " + TextNormalizer.Fold(item.Title + " " + item.Summary) + " ";

        Guid? category = null;
        string? site = null;
        decimal? confidence = null;
        var tie = false;

        foreach (var feedCategory in item.FeedCategories)
        {
            if (_mapping.TryGetValue(feedCategory.Trim(), out var mapped))
            {
                (category, site, confidence) = (mapped.CategoryId, mapped.SiteKey, 1.0m);
                break;
            }
        }

        if (category is null)
        {
            foreach (var rule in _rules)
            {
                if (rule.Exclude.Any(k => HasWord(all, k))) continue;
                var score = 0;
                foreach (var keyword in rule.Include.Concat(rule.Entities).Distinct(StringComparer.Ordinal))
                {
                    if (HasWord(title, keyword)) score += 2;
                    else if (HasWord(all, keyword)) score += 1;
                }

                if (score == 0) continue;
                var c = Math.Min(0.95m, 0.4m + (0.15m * score));
                if (confidence is null || c > confidence)
                {
                    (category, site, confidence, tie) = (rule.CategoryId, rule.SiteKey, c, false);
                }
                else if (c == confidence)
                {
                    tie = true;
                }
            }
        }

        var thresholds = _thresholds(site ?? _siteKeys[0]);
        if (category is null || site is null || confidence < thresholds.Review)
        {
            var fallback = _siteKeys[0];
            return new Classification(fallback, null, confidence, false, _sensitive.Matches(fallback, all), OutOfScope);
        }

        return new Classification(site, category, confidence, tie || confidence < thresholds.AutoAssign, _sensitive.Matches(site, all), Classified);
    }

    /// <summary>Khớp nguyên từ: <paramref name="padded"/> có dạng " a b c ", <paramref name="folded"/> đã qua Fold.</summary>
    internal static bool HasWord(string padded, string folded) => folded.Length > 0 && padded.Contains(" " + folded + " ", StringComparison.Ordinal);

    private static List<string> FoldAll(IEnumerable<string> keywords) =>
        keywords.Select(TextNormalizer.Fold).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Danh sách chủ đề nhạy cảm theo site (BR-34) — <c>config/aggregation/sensitive-topics.json</c>. GĐ1 đổi qua pull request có
/// Legal/Ban biên tập review (lịch sử trong git), không có API sửa. Khóa <c>*</c> áp cho mọi site.
/// </summary>
public sealed class SensitiveTopics
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private readonly Dictionary<string, List<string>> _bySite;

    private SensitiveTopics(Dictionary<string, List<string>> bySite) => _bySite = bySite;

    public static SensitiveTopics Empty { get; } = new([]);

    public static SensitiveTopics Load(string path) => Parse(File.ReadAllText(path), path);

    public static SensitiveTopics Parse(string json, string source = "sensitive-topics.json")
    {
        var document = JsonSerializer.Deserialize<Document>(json, Json) ?? throw new InvalidOperationException($"{source} is empty.");
        if (document.Version != 1) throw new InvalidOperationException($"{source}: version must be 1.");
        var bySite = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (site, topics) in document.Sites ?? [])
        {
            if (site != "*" && !AggregationLimits.IsSiteKey(site)) throw new InvalidOperationException($"{source}: invalid site key '{site}'.");
            var keywords = new List<string>();
            foreach (var topic in topics)
            {
                if (string.IsNullOrWhiteSpace(topic.Topic)) throw new InvalidOperationException($"{source}: every topic of '{site}' needs a name.");
                foreach (var keyword in topic.Keywords ?? [])
                {
                    var folded = TextNormalizer.Fold(keyword);
                    if (folded.Length == 0) throw new InvalidOperationException($"{source}: blank keyword in topic '{topic.Topic}'.");
                    keywords.Add(folded);
                }
            }

            bySite[site] = keywords.Distinct(StringComparer.Ordinal).ToList();
        }

        return new SensitiveTopics(bySite);
    }

    public int KeywordCount => _bySite.Values.Sum(v => v.Count);

    /// <param name="padded">Văn bản dạng Fold, có khoảng trắng hai đầu.</param>
    public bool Matches(string siteKey, string padded) => Keywords("*").Concat(Keywords(siteKey)).Any(k => ItemClassifier.HasWord(padded, k));

    private IEnumerable<string> Keywords(string site) => _bySite.TryGetValue(site, out var list) ? list : [];

    private sealed record Document(int Version, Dictionary<string, List<TopicEntry>>? Sites);

    private sealed record TopicEntry(string? Topic, List<string>? Keywords);
}

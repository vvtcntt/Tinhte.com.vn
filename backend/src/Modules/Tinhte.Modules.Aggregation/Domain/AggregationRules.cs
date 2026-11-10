using System.Text.RegularExpressions;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.Aggregation.Domain;

/// <summary>AggregationSourceStatus — khớp CHECK ck_agg_source_profiles_status (0341).</summary>
public static class SourceStatuses
{
    public const string Proposed = "PROPOSED";
    public const string Enabled = "ENABLED";
    public const string Paused = "PAUSED";
    public const string Degraded = "DEGRADED";
    public const string Retired = "RETIRED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Proposed, Enabled, Paused, Degraded, Retired };

    /// <summary>Đang thu thập (worker chỉ lấy feed của nguồn ở hai trạng thái này).</summary>
    public static readonly IReadOnlySet<string> Collecting = new HashSet<string>(StringComparer.Ordinal) { Enabled, Degraded };

    /// <summary>Tạm dừng được từ mọi trạng thái còn hoạt động; PAUSED/RETIRED → 409.</summary>
    public static readonly IReadOnlySet<string> Pausable = new HashSet<string>(StringComparer.Ordinal) { Proposed, Enabled, Degraded };
}

/// <summary>AggregationUsageMode (BA-01 §5). M2/M3 cần giấy phép đang hiệu lực + tách nhiệm vụ khi duyệt.</summary>
public static class UsageModes
{
    public const string M0 = "M0";
    public const string M1 = "M1";
    public const string M2 = "M2";
    public const string M3 = "M3";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { M0, M1, M2, M3 };

    public static bool NeedsLicence(string mode) => mode is M2 or M3;
}

/// <summary>AggregationFeedFormat.</summary>
public static class FeedFormats
{
    public const string Auto = "AUTO";
    public const string Rss2 = "RSS2";
    public const string Rss1 = "RSS1";
    public const string Atom = "ATOM";
    public const string JsonFeed = "JSONFEED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Auto, Rss2, Rss1, Atom, JsonFeed };
}

/// <summary>AggregationFeedHealth. DISABLED không lưu ở DB: suy ra từ <c>is_enabled = false</c>.</summary>
public static class FeedHealth
{
    public const string NeverFetched = "NEVER_FETCHED";
    public const string Disabled = "DISABLED";
}

/// <summary>Giới hạn và kiểm tra dùng chung (theo components.schemas của CmsAggregation).</summary>
public static partial class AggregationLimits
{
    public const int DefaultTrustWeight = 3;
    public const int DefaultScheduleMinutes = 30;   // BA-01 P-02, chốt 10/11/2026
    public const int MinScheduleMinutes = 5;
    public const int MaxScheduleMinutes = 1440;
    public const int MaxCategoryMappings = 100;
    public const int MaxFeedCategoryLength = 200;
    public const int MaxSiteKeys = 20;
    public const int MaxTermsEvidenceRef = 500;
    public const int MaxNote = 2000;
    public const int MinReason = 5;
    public const int MaxReason = 2000;
    public const string LanguagePattern = "^[a-z]{2}$";
    public const string SiteKeyPattern = "^[a-z][a-z0-9-]{0,30}$";

    public static bool IsSiteKey(string? value) => value is not null && SiteKeyRegex().IsMatch(value);

    public static bool IsLanguage(string? value) => value is not null && LanguageRegex().IsMatch(value);

    /// <summary>ActionReason: thiếu hoặc ngắn hơn 5 ký tự (sau trim) → 422 REASON_REQUIRED; dài quá 2000 → 422 VALIDATION_FAILED.</summary>
    public static string CheckReason(string? reason)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < MinReason)
        {
            throw new DomainProblemException(ErrorCodes.ReasonRequired, "Reason required", $"Give a reason of at least {MinReason} characters.");
        }

        if (text.Length > MaxReason)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", $"The reason must be at most {MaxReason} characters.",
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["reason"] = [$"At most {MaxReason} characters."] });
        }

        return text;
    }

    /// <summary>siteKeys: 1–20 khóa, đúng mẫu, không trùng. Site có tồn tại/là public-web hay không do trigger 0341 kiểm (VALIDATION_FAILED).</summary>
    public static string? SiteKeysError(IReadOnlyList<string>? siteKeys)
    {
        if (siteKeys is null || siteKeys.Count == 0) return "At least one site key is required.";
        if (siteKeys.Count > MaxSiteKeys) return $"At most {MaxSiteKeys} site keys.";
        if (siteKeys.Any(k => !IsSiteKey(k))) return $"Each site key must match {SiteKeyPattern}.";
        return siteKeys.Distinct(StringComparer.Ordinal).Count() != siteKeys.Count ? "Site keys must be unique." : null;
    }

    // \z thay cho $ của mẫu trong hợp đồng: $ của .NET khớp cả trước "\n" cuối chuỗi.
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,30}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SiteKeyRegex();

    [GeneratedRegex(@"^[a-z]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageRegex();
}

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Tinhte.Modules.Aggregation.Domain.Collection;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>
/// Cấu hình mục <c>Aggregation</c> (ADR-0008 §4, §6, §7):
/// <list type="bullet">
///   <item><c>NewsroomTimeZone</c> (mặc định Asia/Ho_Chi_Minh) — "hôm nay";</item>
///   <item><c>Worker:Parallelism</c> (8), <c>Worker:PollSeconds</c> (30), <c>Worker:LeaseSeconds</c> (120) — tiến trình Tinhte.Worker;</item>
///   <item><c>Classification:AutoAssign</c> (0.80), <c>Classification:Review</c> (0.50), ghi đè theo site
///         <c>Classification:Sites:{siteKey}:AutoAssign|Review</c> — BR-31;</item>
///   <item><c>FeedTests:PerMinute</c> (10) — số lần thử feed mỗi phút cho một người (429 RATE_LIMITED);</item>
///   <item><c>SensitiveTopicsPath</c> (config/aggregation/sensitive-topics.json), <c>BotSiteKey</c>, <c>BotInfoPath</c>.</item>
/// </list>
/// </summary>
public sealed class AggregationOptions
{
    public string NewsroomTimeZone { get; init; } = NewsroomClock.DefaultTimeZone;

    public int Parallelism { get; init; } = 8;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(120);

    public int FeedTestsPerMinute { get; init; } = 10;

    public string SensitiveTopicsPath { get; init; } = "config/aggregation/sensitive-topics.json";

    public ClassificationThresholds DefaultThresholds { get; init; } = ClassificationThresholds.Default;

    public IReadOnlyDictionary<string, ClassificationThresholds> SiteThresholds { get; init; } = new Dictionary<string, ClassificationThresholds>();

    public ClassificationThresholds ThresholdsFor(string siteKey) => SiteThresholds.TryGetValue(siteKey, out var t) ? t : DefaultThresholds;

    public static AggregationOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Aggregation");
        var defaults = Thresholds(section.GetSection("Classification"), ClassificationThresholds.Default);
        var sites = section.GetSection("Classification:Sites").GetChildren().ToDictionary(s => s.Key, s => Thresholds(s, defaults), StringComparer.Ordinal);
        var options = new AggregationOptions
        {
            NewsroomTimeZone = section["NewsroomTimeZone"] ?? NewsroomClock.DefaultTimeZone,
            Parallelism = Int(section, "Worker:Parallelism", 8, 1, 64),
            PollInterval = TimeSpan.FromSeconds(Int(section, "Worker:PollSeconds", 30, 1, 3600)),
            LeaseDuration = TimeSpan.FromSeconds(Int(section, "Worker:LeaseSeconds", 120, 30, 3600)),
            FeedTestsPerMinute = Int(section, "FeedTests:PerMinute", 10, 1, 1000),
            SensitiveTopicsPath = section["SensitiveTopicsPath"] ?? "config/aggregation/sensitive-topics.json",
            DefaultThresholds = defaults,
            SiteThresholds = sites,
        };
        return options;
    }

    private static ClassificationThresholds Thresholds(IConfiguration section, ClassificationThresholds fallback)
    {
        var auto = Decimal(section, "AutoAssign", fallback.AutoAssign);
        var review = Decimal(section, "Review", fallback.Review);
        if (review is < 0 or > 1 || auto is < 0 or > 1 || review > auto)
        {
            throw new InvalidOperationException("Aggregation:Classification thresholds must satisfy 0 ≤ Review ≤ AutoAssign ≤ 1.");
        }

        return new ClassificationThresholds(auto, review);
    }

    private static decimal Decimal(IConfiguration section, string key, decimal fallback) =>
        section[key] is { } raw ? decimal.Parse(raw, NumberStyles.Number, CultureInfo.InvariantCulture) : fallback;

    private static int Int(IConfiguration section, string key, int fallback, int min, int max)
    {
        if (section[key] is not { } raw) return fallback;
        var value = int.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (value < min || value > max) throw new InvalidOperationException($"Aggregation:{key} must be between {min} and {max}.");
        return value;
    }
}

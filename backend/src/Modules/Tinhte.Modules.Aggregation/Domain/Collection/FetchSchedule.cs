namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>
/// Lịch thu thập (BR-04, BR-06): chu kỳ cấu hình (tối thiểu 5 phút) hoặc gợi ý <c>ttl</c>/<c>sy:updatePeriod</c> của feed nếu dài hơn
/// (tối đa 24 giờ); lỗi ⇒ lùi theo cấp số, tối đa 6 giờ; nguồn trả <c>Retry-After</c> ⇒ không gọi lại trước thời điểm đó (tối đa 24 giờ).
/// Luôn cộng jitter (≤ 10% chu kỳ, ≤ 2 phút) để các feed không dồn cùng lúc.
/// </summary>
public static class FetchSchedule
{
    public const int DegradedAfterFailures = 5;
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);
    public static readonly TimeSpan AutoPauseAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxJitter = TimeSpan.FromMinutes(2);

    /// <param name="random01">Số ngẫu nhiên trong [0, 1) (tham số để test xác định).</param>
    public static DateTimeOffset AfterSuccess(DateTimeOffset now, int scheduleMinutes, int? hintMinutes, double random01)
    {
        var minutes = Math.Clamp(Math.Max(scheduleMinutes, hintMinutes ?? 0), AggregationLimits.MinScheduleMinutes, AggregationLimits.MaxScheduleMinutes);
        var interval = TimeSpan.FromMinutes(minutes);
        return now + interval + Jitter(interval, random01);
    }

    /// <param name="consecutiveFailures">Số lần lỗi liên tiếp, đã tính lần này (≥ 1).</param>
    public static DateTimeOffset AfterFailure(DateTimeOffset now, int scheduleMinutes, int consecutiveFailures, TimeSpan? retryAfter, double random01)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 10);
        var backoff = TimeSpan.FromMinutes(Math.Max(scheduleMinutes, AggregationLimits.MinScheduleMinutes) * Math.Pow(2, exponent));
        if (backoff > MaxBackoff) backoff = MaxBackoff;
        if (retryAfter is { } wait && wait > TimeSpan.Zero)
        {
            if (wait > MaxRetryAfter) wait = MaxRetryAfter;
            if (wait > backoff) backoff = wait;
        }

        return now + backoff + Jitter(backoff, random01);
    }

    /// <summary>AggregationFeedHealth sau một lần lỗi: robots chặn → BLOCKED_BY_ROBOTS; ≥ 5 lần liên tiếp → FAILING; còn lại DEGRADED.</summary>
    public static string HealthAfterFailure(string failure, int consecutiveFailures) =>
        failure == FeedFailures.RobotsDisallowed ? "BLOCKED_BY_ROBOTS" : consecutiveFailures >= DegradedAfterFailures ? "FAILING" : "DEGRADED";

    private static TimeSpan Jitter(TimeSpan interval, double random01)
    {
        var max = TimeSpan.FromTicks(Math.Min(interval.Ticks / 10, MaxJitter.Ticks));
        return TimeSpan.FromTicks((long)(max.Ticks * Math.Clamp(random01, 0, 1)));
    }
}

/// <summary>
/// "Hôm nay" theo múi giờ tòa soạn (<c>Aggregation:NewsroomTimeZone</c>, mặc định Asia/Ho_Chi_Minh — BR-11/12).
/// </summary>
public sealed class NewsroomClock
{
    public const string DefaultTimeZone = "Asia/Ho_Chi_Minh";

    // InvariantGlobalization trên Windows không đổi được tên IANA sang Windows: dự phòng cho các múi giờ hay dùng.
    private static readonly Dictionary<string, string> WindowsFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Asia/Ho_Chi_Minh"] = "SE Asia Standard Time",
        ["Asia/Saigon"] = "SE Asia Standard Time",
        ["Asia/Bangkok"] = "SE Asia Standard Time",
        ["UTC"] = "UTC",
        ["Etc/UTC"] = "UTC",
    };

    public NewsroomClock(string? timeZoneId)
    {
        Id = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZone : timeZoneId.Trim();
        Zone = Resolve(Id);
    }

    public string Id { get; }

    public TimeZoneInfo Zone { get; }

    public DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);

    /// <summary>[00:00 của ngày, 00:00 ngày kế) theo giờ tòa soạn, quy về UTC.</summary>
    public (DateTimeOffset FromUtc, DateTimeOffset ToUtc) DayRange(DateOnly date) => (StartOf(date), StartOf(date.AddDays(1)));

    private DateTimeOffset StartOf(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeZoneInfo Resolve(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (WindowsFallback.TryGetValue(id, out var windowsId)) return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            throw new InvalidOperationException($"Aggregation:NewsroomTimeZone '{id}' is not a known time zone on this host.", e);
        }
    }
}

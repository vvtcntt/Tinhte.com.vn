using System.Diagnostics;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Modules.Aggregation.Infrastructure.Collection;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>
/// testAdminAggregationFeed (FR-AGG-002): tải + parse một URL feed MỘT lần bằng chính bộ thu thập của worker (cùng SSRF, robots, giới hạn),
/// KHÔNG ghi gì vào DB. Lỗi của nguồn (timeout, HTTP, parse…) là dữ liệu trong kết quả; URL — hoặc một bước redirect, hoặc địa chỉ DNS lúc
/// kết nối — trỏ vào mạng nội bộ ⇒ 422 UNSAFE_URL. Giới hạn số lần thử mỗi phút theo người (429 RATE_LIMITED + Retry-After).
/// </summary>
public sealed class FeedTestService(
    ITinhteDatabase database,
    FeedUrlPolicy urlPolicy,
    FeedFetcher fetcher,
    NewsroomClock clock,
    FeedTestRateLimiter rateLimiter,
    TimeProvider time)
{
    public const int SampleSize = 5;

    public async Task<AggregationFeedTestResult> TestAsync(Guid actorId, AggregationFeedTestRequest request, Action<TimeSpan> onRateLimited, CancellationToken ct)
    {
        var format = request.Format ?? FeedFormats.Auto;
        if (!FeedFormats.All.Contains(format))
        {
            var message = $"One of {string.Join(", ", FeedFormats.All.Order(StringComparer.Ordinal))}.";
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", message,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["format"] = [message] });
        }

        await using (var connection = await database.OpenConnectionAsync(ct))
        {
            await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        }

        if (rateLimiter.TryAcquire(actorId) is { } wait)
        {
            onRateLimited(wait);
            throw new DomainProblemException(ErrorCodes.RateLimited, "Too many feed tests",
                $"At most {rateLimiter.PerMinute} feed tests per minute. Retry in {Math.Ceiling(wait.TotalSeconds):0} seconds.");
        }

        var url = await urlPolicy.CheckAsync("url", request.Url, ct);
        var stopwatch = Stopwatch.StartNew();
        var fetch = await fetcher.FetchAsync(url, null, null, ct);
        if (fetch.Unsafe)
        {
            throw new DomainProblemException(ErrorCodes.UnsafeUrl, "Unsafe URL", fetch.Detail,
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["url"] = [fetch.Detail ?? "The feed leads to a non-public address."] });
        }

        if (fetch.Outcome != FetchOutcomes.Ok)
        {
            // 304 không xảy ra (không gửi ETag); mọi lỗi khác là dữ liệu.
            return new AggregationFeedTestResult(false, fetch.HttpStatus, null, null, fetch.RobotsAllowed, 0, [], fetch.Failure, stopwatch.ElapsedMilliseconds);
        }

        ParsedFeed parsed;
        try
        {
            parsed = FeedParser.Parse(fetch.Body!, fetch.Charset, format, clock.Zone, new Uri(url));
        }
        catch (FeedParseException)
        {
            return new AggregationFeedTestResult(false, fetch.HttpStatus, null, null, true, 0, [], FeedFailures.ParseError, stopwatch.ElapsedMilliseconds);
        }

        var now = time.GetUtcNow();
        var feedUri = new Uri(url);
        var samples = parsed.Items
            .Select(i => ItemNormalizer.Normalize(i, feedUri, now))
            .OfType<NormalizedItem>()
            .Take(SampleSize)
            .Select(i => new AggregationFeedTestItem(i.Title, i.Url, i.PublishedAt, i.TimeReliable))
            .ToList();
        return new AggregationFeedTestResult(true, fetch.HttpStatus, parsed.Format, parsed.Title, true, parsed.Items.Count, samples, null, stopwatch.ElapsedMilliseconds);
    }
}

/// <summary>
/// Giới hạn thử feed theo người, cửa sổ trượt 60 giây, trong bộ nhớ của từng bản API (đủ để chặn lạm dụng bộ thu thập làm công cụ quét;
/// khi chạy nhiều bản API, giới hạn thực tế = N × số bản — chờ Security, A-4).
/// </summary>
public sealed class FeedTestRateLimiter(AggregationOptions options, TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _calls = [];
    private readonly Lock _lock = new();

    public int PerMinute => options.FeedTestsPerMinute;

    /// <summary>null = được phép (đã ghi nhận); ngược lại = thời gian phải chờ.</summary>
    public TimeSpan? TryAcquire(Guid actorId)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_calls.Count > 10_000)
            {
                foreach (var key in _calls.Where(kv => kv.Value.All(t => t <= now - Window)).Select(kv => kv.Key).ToList()) _calls.Remove(key);
            }

            if (!_calls.TryGetValue(actorId, out var queue)) _calls[actorId] = queue = new Queue<DateTimeOffset>();
            while (queue.Count > 0 && queue.Peek() <= now - Window) queue.Dequeue();
            if (queue.Count >= PerMinute) return queue.Peek() + Window - now;
            queue.Enqueue(now);
            return null;
        }
    }
}

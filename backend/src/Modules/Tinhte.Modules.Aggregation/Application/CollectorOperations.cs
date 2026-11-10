using System.Diagnostics.Metrics;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Platform.Data;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>Cảnh báo nguồn chờ gửi ra kênh ngoài (một dòng của tt.agg_alerts).</summary>
public sealed record PendingAlert(long Id, string Kind, Guid SourceId, string SourceName, string Message, DateTimeOffset CreatedAt);

/// <summary>Kênh gửi cảnh báo ra ngoài (BR-06 "báo Quản lý nguồn"). Ném lỗi = gửi thất bại, sẽ thử lại ở vòng bảo trì sau.</summary>
public interface IAlertSink
{
    /// <summary>false = chưa cấu hình kênh nào (cảnh báo chỉ hiện trên CMS, không đánh dấu đã gửi).</summary>
    bool IsConfigured { get; }

    Task SendAsync(PendingAlert alert, CancellationToken ct);
}

/// <summary>Không có kênh ngoài: cảnh báo vẫn nằm trong DB và hiện ở CMS (getAdminAggregationStats.alerts).</summary>
public sealed class NoAlertSink : IAlertSink
{
    public bool IsConfigured => false;

    public Task SendAsync(PendingAlert alert, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Webhook JSON (<c>Aggregation:Alerts:WebhookUrl</c>, chỉ https; URL là bí mật vận hành — đặt bằng biến môi trường/secret). Thân
/// <c>{"text": "...", "kind", "sourceId", "sourceName", "message", "createdAt"}</c>: trường <c>text</c> hợp với incoming webhook kiểu Slack/Teams/Google Chat.
/// URL do vận hành cấu hình (tin cậy) nên không đi qua bộ thu thập chống SSRF; timeout 10 giây.
/// </summary>
public sealed class WebhookAlertSink : IAlertSink, IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _url;

    public WebhookAlertSink(Uri url, HttpMessageHandler? handler = null)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Aggregation:Alerts:WebhookUrl must use https.");
        _url = url;
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = TimeSpan.FromSeconds(10);
    }

    public bool IsConfigured => true;

    public static IAlertSink FromConfiguration(IConfiguration configuration) =>
        configuration["Aggregation:Alerts:WebhookUrl"] is { Length: > 0 } raw ? new WebhookAlertSink(new Uri(raw, UriKind.Absolute)) : new NoAlertSink();

    public async Task SendAsync(PendingAlert alert, CancellationToken ct)
    {
        var label = alert.Kind == "SOURCE_AUTO_PAUSED" ? "Nguồn tự tạm dừng" : "Nguồn suy giảm";
        using var response = await _client.PostAsJsonAsync(_url, new
        {
            text = $"[Radar tin] {label}: {alert.SourceName} — {alert.Message}",
            kind = alert.Kind,
            sourceId = alert.SourceId,
            sourceName = alert.SourceName,
            message = alert.Message,
            createdAt = alert.CreatedAt,
        }, ct);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Số đo của worker (System.Diagnostics.Metrics, meter <c>Tinhte.Aggregation</c>): đọc bằng <c>dotnet-counters monitor -n Tinhte.Worker
/// --counters Tinhte.Aggregation</c> hoặc exporter OpenTelemetry khi WBS-021 chọn nền tảng giám sát.
/// </summary>
public static class CollectorMetrics
{
    public const string MeterName = "Tinhte.Aggregation";

    private static readonly Meter Meter = new(MeterName, "1.0");

    public static readonly Counter<long> Fetches = Meter.CreateCounter<long>("aggregation.feed.fetches", description: "Feed fetches by outcome/failure");
    public static readonly Counter<long> ItemsNew = Meter.CreateCounter<long>("aggregation.items.new", description: "New items stored");
    public static readonly Counter<long> ItemsUpdated = Meter.CreateCounter<long>("aggregation.items.updated", description: "Items updated by their source");
    public static readonly Histogram<double> FetchDuration = Meter.CreateHistogram<double>("aggregation.feed.fetch.duration", "ms", "Fetch + store time per feed");
    public static readonly Counter<long> Alerts = Meter.CreateCounter<long>("aggregation.alerts", description: "Source alerts raised by kind");
    public static readonly Counter<long> FetchLogPurged = Meter.CreateCounter<long>("aggregation.fetch_log.purged", description: "Fetch log rows deleted by retention");

    private static long _lastCycleUnix;
    private static int _radarEnabled;
    private static int _lastLeased;

    /// <summary>Thời điểm vòng quét gần nhất không lỗi hạ tầng (giây Unix) — luật cảnh báo "worker không chạy" dựa vào đây.</summary>
    public static readonly ObservableGauge<long> LastCycle = Meter.CreateObservableGauge("aggregation.worker.last_cycle.time",
        () => Interlocked.Read(ref _lastCycleUnix), "s", "Unix time of the last successful collection cycle");

    public static readonly ObservableGauge<int> RadarEnabled = Meter.CreateObservableGauge("aggregation.worker.radar_enabled",
        () => Volatile.Read(ref _radarEnabled), description: "1 when the aggregation.radar flag is ON");

    public static readonly ObservableGauge<int> LastLeased = Meter.CreateObservableGauge("aggregation.worker.leased",
        () => Volatile.Read(ref _lastLeased), description: "Feeds leased in the last cycle");

    public static void RecordCycle(DateTimeOffset at, bool radarEnabled, int leased)
    {
        Interlocked.Exchange(ref _lastCycleUnix, at.ToUnixTimeSeconds());
        Volatile.Write(ref _radarEnabled, radarEnabled ? 1 : 0);
        Volatile.Write(ref _lastLeased, leased);
    }
}

/// <summary>
/// Việc vận hành của worker (ADR-0008 §4, §9; NFR-AGG-06): dọn <c>agg_fetch_log</c> quá hạn giữ (mặc định 30 ngày, theo lô), gửi cảnh báo nguồn
/// chờ gửi ra kênh ngoài (tối đa 10 lần thử mỗi cảnh báo), ghi nhịp tim tiến trình.
/// </summary>
public sealed partial class CollectorOperations(ITinhteDatabase database, IAlertSink sink, AggregationOptions options, ILogger<CollectorOperations> logger)
{
    public const int PurgeBatch = 5000;
    public const int MaxAlertAttempts = 10;

    /// <summary>Xóa nhật ký thu thập cũ hơn <c>Aggregation:FetchLogRetentionDays</c> theo lô 5000 dòng (mỗi lô một câu lệnh tự commit). Trả số dòng đã xóa.</summary>
    public async Task<long> PurgeFetchLogAsync(CancellationToken ct, int maxBatches = 20)
    {
        long total = 0;
        await using var connection = await database.OpenConnectionAsync(ct);
        for (var batch = 0; batch < maxBatches; batch++)
        {
            await using var command = AggregationSql.Command(connection, null, """
                DELETE FROM tt.agg_fetch_log WHERE id IN (
                  SELECT id FROM tt.agg_fetch_log WHERE started_at < now() - @days * interval '1 day' ORDER BY started_at LIMIT @batch)
                """).WithInt("days", options.FetchLogRetentionDays).WithInt("batch", PurgeBatch);
            var deleted = await command.ExecuteNonQueryAsync(ct);
            total += deleted;
            if (deleted < PurgeBatch) break;
        }

        if (total > 0)
        {
            CollectorMetrics.FetchLogPurged.Add(total);
            LogPurged(logger, total, options.FetchLogRetentionDays);
        }

        return total;
    }

    /// <summary>Gửi cảnh báo chưa gửi (cũ trước). Chưa cấu hình kênh ⇒ không làm gì (cảnh báo vẫn hiện trên CMS). Trả số cảnh báo đã gửi.</summary>
    public async Task<int> DispatchAlertsAsync(CancellationToken ct)
    {
        if (!sink.IsConfigured) return 0;
        var pending = new List<PendingAlert>();
        await using var connection = await database.OpenConnectionAsync(ct);
        await using (var command = AggregationSql.Command(connection, null, """
            SELECT a.id, a.kind, a.content_source_id, cs.name, a.message, a.created_at
              FROM tt.agg_alerts a JOIN tt.content_sources cs ON cs.id = a.content_source_id
             WHERE a.delivered_at IS NULL AND a.attempts < @max ORDER BY a.created_at, a.id LIMIT 50
            """).WithInt("max", MaxAlertAttempts))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) pending.Add(new PendingAlert(r.GetInt64(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)));
        }

        var sent = 0;
        foreach (var alert in pending)
        {
            string? error = null;
            try
            {
                await sink.SendAsync(alert, ct);
                sent++;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                error = e.Message.Length > 500 ? e.Message[..500] : e.Message;
                LogAlertFailed(logger, alert.Id, error);
            }

            // Hai worker cùng gửi một cảnh báo: chỉ một lần đánh dấu thành công (delivered_at IS NULL); kênh ngoài có thể nhận trùng — chấp nhận.
            await using var update = AggregationSql.Command(connection, null, """
                UPDATE tt.agg_alerts SET attempts = attempts + 1, last_error = @error,
                       delivered_at = CASE WHEN @error IS NULL THEN now() ELSE NULL END
                 WHERE id = @id AND delivered_at IS NULL
                """).WithText("error", error).WithLong("id", alert.Id);
            await update.ExecuteNonQueryAsync(ct);
        }

        return sent;
    }

    /// <summary>Nhịp tim của tiến trình worker (upsert theo workerId).</summary>
    public async Task HeartbeatAsync(string workerId, string host, string version, DateTimeOffset startedAt, int leased, bool radarEnabled, string? lastError,
        CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = AggregationSql.Command(connection, null, """
            INSERT INTO tt.agg_worker_heartbeats (worker_id, host, version, started_at, last_seen_at, last_cycle_feeds, radar_enabled, last_error)
            VALUES (@id, @host, @version, @started, now(), @feeds, @enabled, @error)
            ON CONFLICT (worker_id) DO UPDATE
               SET last_seen_at = now(), last_cycle_feeds = EXCLUDED.last_cycle_feeds, radar_enabled = EXCLUDED.radar_enabled,
                   last_error = EXCLUDED.last_error, host = EXCLUDED.host, version = EXCLUDED.version
            """)
            .WithText("id", Cap(workerId, 100)).WithText("host", Cap(host, 200)).WithText("version", Cap(version, 50)).WithTime("started", startedAt)
            .WithInt("feeds", Math.Max(0, leased)).WithBool("enabled", radarEnabled).WithText("error", lastError is null ? null : Cap(lastError, 500));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ghi một cảnh báo trong transaction đổi trạng thái nguồn (gọi từ worker).</summary>
    internal static async Task RaiseAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid sourceId, string kind, string message, CancellationToken ct)
    {
        await using var insert = AggregationSql.Command(connection, tx, "INSERT INTO tt.agg_alerts (content_source_id, kind, message) VALUES (@source, @kind, @message)")
            .WithUuid("source", sourceId).WithText("kind", kind).WithText("message", Cap(message, 2000));
        await insert.ExecuteNonQueryAsync(ct);
        CollectorMetrics.Alerts.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} fetch log rows older than {Days} days")]
    private static partial void LogPurged(ILogger logger, long count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert {AlertId} could not be delivered: {Error}")]
    private static partial void LogAlertFailed(ILogger logger, long alertId, string error);
}

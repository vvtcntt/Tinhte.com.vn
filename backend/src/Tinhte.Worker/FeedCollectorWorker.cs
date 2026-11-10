using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tinhte.Modules.Aggregation.Application;

namespace Tinhte.Worker;

/// <summary>
/// Vòng quét của worker (ADR-0008 §3–§4): mỗi vòng kiểm cờ aggregation.radar, thuê tối đa <c>Aggregation:Worker:Parallelism</c> feed đến hạn
/// và xử lý song song (timeout từng request nên một feed treo không chặn feed khác); mục mới được gom cụm ngay trong transaction ghi mục, và mỗi
/// <c>Aggregation:Worker:MaintenanceMinutes</c> phút chạy bảo trì cụm (đóng cụm hết cửa sổ 48 giờ, gom bù, tính lại điểm). Thuê đủ một lô ⇒ quét tiếp ngay; ít hơn ⇒ chờ
/// <c>Aggregation:Worker:PollSeconds</c>. Lỗi hạ tầng (DB mất kết nối…) được ghi log rồi thử lại ở vòng sau — worker không sập theo.
/// </summary>
public sealed partial class FeedCollectorWorker(
    FeedCollectionService collector,
    CollectorOperations operations,
    AggregationOptions options,
    IConfiguration configuration,
    ILogger<FeedCollectorWorker> logger)
    : BackgroundService
{
    /// <summary>Định danh tiến trình trong tt.agg_worker_heartbeats: máy + pid + hậu tố ngẫu nhiên (pid có thể lặp sau khi khởi động lại container).</summary>
    public static readonly string WorkerId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}";

    private static readonly string Version = typeof(FeedCollectorWorker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool? wasEnabled = null;
        var startedAt = DateTimeOffset.UtcNow;
        var nextHousekeeping = DateTimeOffset.MinValue;
        var livenessFile = configuration["Aggregation:Worker:LivenessFile"];
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.PollInterval;
            int? leased = null;
            string? error = null;
            try
            {
                leased = await collector.RunOnceAsync(stoppingToken);
                var enabled = leased is not null;
                if (enabled != wasEnabled)
                {
                    if (enabled) LogEnabled(logger);
                    else LogDisabled(logger);
                    wasEnabled = enabled;
                }

                if (leased >= options.Parallelism) delay = TimeSpan.Zero;

                // Gửi cảnh báo mỗi vòng (rẻ); dọn nhật ký thu thập theo chu kỳ bảo trì. Chạy cả khi cờ OFF: vẫn là dữ liệu đã có.
                await operations.DispatchAlertsAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= nextHousekeeping)
                {
                    await operations.PurgeFetchLogAsync(stoppingToken);
                    nextHousekeeping = DateTimeOffset.UtcNow + options.MaintenanceInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException or IOException)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                LogCycleFailed(logger, e);
            }

            await BeatAsync(startedAt, leased, wasEnabled == true, error, livenessFile, stoppingToken);

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Nhịp tim: dòng tt.agg_worker_heartbeats (CMS thống kê) + chạm tệp <c>Aggregation:Worker:LivenessFile</c> (HEALTHCHECK của container). Tệp chỉ được
    /// chạm khi vòng quét không lỗi hạ tầng — DB mất kết nối lâu thì container bị đánh dấu unhealthy.
    /// </summary>
    private async Task BeatAsync(DateTimeOffset startedAt, int? leased, bool enabled, string? error, string? livenessFile, CancellationToken ct)
    {
        try
        {
            await operations.HeartbeatAsync(WorkerId, Environment.MachineName, Version, startedAt, leased ?? 0, enabled, error, ct);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException or IOException)
        {
            error ??= e.Message;
            LogHeartbeatFailed(logger, e);
        }

        if (error is null && !string.IsNullOrWhiteSpace(livenessFile))
        {
            try
            {
                await File.WriteAllTextAsync(livenessFile, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogHeartbeatFailed(logger, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat could not be written")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "aggregation.radar is ON: collecting feeds")]
    private static partial void LogEnabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "aggregation.radar is OFF: not collecting (checked every cycle)")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collection cycle failed; retrying next cycle")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}

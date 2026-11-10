using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tinhte.Modules.Aggregation.Application;

namespace Tinhte.Worker;

/// <summary>
/// Vòng quét của worker (ADR-0008 §3–§4): mỗi vòng kiểm cờ aggregation.radar, thuê tối đa <c>Aggregation:Worker:Parallelism</c> feed đến hạn
/// và xử lý song song (timeout từng request nên một feed treo không chặn feed khác). Thuê đủ một lô ⇒ quét tiếp ngay; ít hơn ⇒ chờ
/// <c>Aggregation:Worker:PollSeconds</c>. Lỗi hạ tầng (DB mất kết nối…) được ghi log rồi thử lại ở vòng sau — worker không sập theo.
/// </summary>
public sealed partial class FeedCollectorWorker(FeedCollectionService collector, AggregationOptions options, ILogger<FeedCollectorWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool? wasEnabled = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.PollInterval;
            try
            {
                var leased = await collector.RunOnceAsync(stoppingToken);
                var enabled = leased is not null;
                if (enabled != wasEnabled)
                {
                    if (enabled) LogEnabled(logger);
                    else LogDisabled(logger);
                    wasEnabled = enabled;
                }

                if (leased >= options.Parallelism) delay = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException or IOException)
            {
                LogCycleFailed(logger, e);
            }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "aggregation.radar is ON: collecting feeds")]
    private static partial void LogEnabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "aggregation.radar is OFF: not collecting (checked every cycle)")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collection cycle failed; retrying next cycle")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}

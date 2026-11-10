using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tinhte.Modules.Ai.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Flags;

namespace Tinhte.Modules.Ai.Application;

/// <summary>
/// Đường đi chung của MỌI lần gửi nội dung ra nhà cung cấp AI (gợi ý metadata, viết lại đoạn văn — FR-AI-001) kèm nhật ký (FR-AI-004).
/// <list type="number">
///   <item>Transaction 1 (actor = người gọi): cờ <c>ai.assistance</c> (FEATURE_DISABLED) → nạp nội dung (NOT_FOUND) → hạn mức theo người
///         (RATE_LIMITED + Retry-After) → nhà cung cấp đã cấu hình (DEPENDENCY_UNAVAILABLE) → dựng nội dung gửi đi → ghi dòng PENDING
///         (trigger 0353 chặn lại nếu cờ OFF). Không có gì rời hệ thống trước khi dòng nhật ký được commit.</item>
///   <item>Gọi nhà cung cấp NGOÀI transaction (không giữ khóa DB trong lúc chờ mạng).</item>
///   <item>Transaction 2: chốt dòng SUCCEEDED/FAILED + token + thời gian. Lỗi nhà cung cấp hoặc câu trả lời không dùng được → FAILED +
///         503 DEPENDENCY_UNAVAILABLE.</item>
/// </list>
/// Không lưu nội dung gửi đi hay câu trả lời; chỉ độ dài + sha256 của nội dung gửi đi. Kết quả không ghi vào bài/video.
/// </summary>
public sealed partial class AiRequestPipeline(
    ITinhteDatabase database,
    IAiChatClient ai,
    IOptionsMonitor<AiOptions> options,
    ILogger<AiRequestPipeline> logger)
{
    /// <summary>Một loại yêu cầu AI.</summary>
    /// <param name="Action">Giá trị <c>action</c> trong nhật ký (khớp CHECK ck_ai_requests_action).</param>
    /// <param name="BuildInput">Nội dung gửi đi (đã cắt theo MaxInputChars) từ nội dung đã nạp. Rỗng → 422 trên <paramref name="EmptyInputField"/>.</param>
    /// <param name="BuildPrompt">Prompt từ nội dung gửi đi.</param>
    /// <param name="Parse">Đọc câu trả lời; null = không dùng được (FAILED BAD_RESPONSE + 503).</param>
    public sealed record Job<T>(
        string Action,
        Func<AiContentSource, int, string> BuildInput,
        Func<string, AiChatPrompt> BuildPrompt,
        Func<string, T?> Parse,
        string EmptyInputMessage = "The content has no title, summary or body text.",
        string EmptyInputField = "contentId") where T : class;

    /// <param name="onRateLimited">Nhận thời gian chờ trước khi ném 429 RATE_LIMITED (controller đặt header Retry-After).</param>
    public async Task<T> RunAsync<T>(Guid actorId, Guid contentId, Job<T> job, Action<TimeSpan> onRateLimited, CancellationToken ct) where T : class
    {
        var settings = options.CurrentValue;

        var (requestId, prompt) = await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            if (!await AiSql.FlagEnabledForWriteAsync(connection, tx, FeatureFlagKeys.AiAssistance, ct))
            {
                throw new DomainProblemException(ErrorCodes.FeatureDisabled, "Feature disabled", $"{FeatureFlagKeys.AiAssistance} is OFF.");
            }

            var source = await AiSql.LoadContentAsync(connection, tx, contentId, ct)
                ?? throw new DomainProblemException(ErrorCodes.NotFound, "Not found", "No article or video has this id.");

            if (settings.QuotaPerHour > 0)
            {
                var (count, retryAfterSeconds) = await AiSql.RecentUsageAsync(connection, tx, actorId, ct);
                if (count >= settings.QuotaPerHour)
                {
                    onRateLimited(TimeSpan.FromSeconds(Math.Max(1, retryAfterSeconds)));
                    throw new DomainProblemException(ErrorCodes.RateLimited, "Too many AI requests",
                        $"You can make at most {settings.QuotaPerHour} AI requests per hour.");
                }
            }

            if (!ai.IsConfigured)
            {
                throw Unavailable("No AI provider is configured.");
            }

            var input = job.BuildInput(source, Math.Max(500, settings.MaxInputChars));
            if (input.Length == 0)
            {
                throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "The content has no text to analyse yet.",
                    new Dictionary<string, string[]> { [job.EmptyInputField] = [job.EmptyInputMessage] });
            }

            var id = Guid.CreateVersion7();
            await AiSql.InsertPendingAsync(connection, tx, id, actorId, job.Action, source.ObjectType, contentId,
                ai.ProviderKey, ai.Model, input.Length, Sha256(input), ct);
            return (id, job.BuildPrompt(input));
        }, ct);

        var stopwatch = Stopwatch.StartNew();
        AiChatResult result;
        T? parsed;
        try
        {
            result = await ai.CompleteAsync(prompt, ct);
            parsed = job.Parse(result.Content)
                ?? throw new AiProviderException(AiProviderException.BadResponse, "The AI answer contained nothing usable.");
        }
        catch (AiProviderException e)
        {
            LogProviderFailure(logger, requestId, e.Reason, e.Message);
            await CompleteAsync(actorId, requestId, e.Reason, null, null, stopwatch);
            throw Unavailable(e.Reason == AiProviderException.BadResponse
                ? "The AI provider returned an answer that could not be used. Try again."
                : "The AI provider is unavailable. Try again later.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CompleteAsync(actorId, requestId, "CANCELLED", null, null, stopwatch);
            throw;
        }

        await CompleteAsync(actorId, requestId, null, result.PromptTokens, result.CompletionTokens, stopwatch);
        return parsed;
    }

    public static string Sha256(string input) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static DomainProblemException Unavailable(string detail) =>
        new(ErrorCodes.DependencyUnavailable, "AI provider unavailable", detail);

    /// <summary>
    /// Chốt dòng nhật ký. Không để lỗi ở đây che mất kết quả/lỗi chính: dòng PENDING còn lại vẫn chứng minh nội dung đã được gửi đi.
    /// </summary>
    private async Task CompleteAsync(Guid actorId, Guid requestId, string? errorCode, int? promptTokens, int? completionTokens, Stopwatch stopwatch)
    {
        try
        {
            var latency = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
            await database.InTransactionAsync<bool>(actorId, async (connection, tx) =>
            {
                await AiSql.CompleteAsync(connection, tx, requestId, errorCode, promptTokens, completionTokens, latency, CancellationToken.None);
                return true;
            }, CancellationToken.None);
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or DomainProblemException or InvalidOperationException)
        {
            LogAuditCompletionFailed(logger, e, requestId);
        }
    }

    [LoggerMessage(EventId = 7111, Level = LogLevel.Warning, Message = "AI request {RequestId} failed: {Reason} — {Detail}")]
    private static partial void LogProviderFailure(ILogger logger, Guid requestId, string reason, string detail);

    [LoggerMessage(EventId = 7112, Level = LogLevel.Error, Message = "AI request {RequestId}: could not record the outcome in tt.ai_requests (row stays PENDING)")]
    private static partial void LogAuditCompletionFailed(ILogger logger, Exception exception, Guid requestId);
}

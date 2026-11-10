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
/// Gợi ý tag/tóm tắt cho bài hoặc video (postAdminAiMetadataSuggestions, FR-AI-001) và ghi nhật ký gọi AI (FR-AI-004).
/// <list type="number">
///   <item>Transaction 1 (actor = người gọi): cờ <c>ai.assistance</c> (FEATURE_DISABLED) → nạp nội dung (NOT_FOUND) → hạn mức theo người
///         (RATE_LIMITED + Retry-After) → nhà cung cấp đã cấu hình (DEPENDENCY_UNAVAILABLE) → ghi dòng PENDING (trigger 0353 chặn lại nếu cờ OFF).
///         Không có gì rời hệ thống trước khi dòng nhật ký được commit.</item>
///   <item>Gọi nhà cung cấp NGOÀI transaction (không giữ khóa DB trong lúc chờ mạng).</item>
///   <item>Transaction 2: chốt dòng SUCCEEDED/FAILED + token + thời gian. Lỗi nhà cung cấp → FAILED + 503 DEPENDENCY_UNAVAILABLE.</item>
/// </list>
/// Không lưu nội dung gửi đi hay câu trả lời; chỉ độ dài + sha256. Kết quả không ghi vào bài: requiresHumanApproval luôn true.
/// </summary>
public sealed partial class MetadataSuggestionService(
    ITinhteDatabase database,
    IAiChatClient ai,
    IOptionsMonitor<AiOptions> options,
    ILogger<MetadataSuggestionService> logger)
{
    /// <param name="onRateLimited">Nhận thời gian chờ trước khi ném 429 RATE_LIMITED (controller đặt header Retry-After).</param>
    public async Task<AiSuggestion> SuggestAsync(Guid actorId, AiSuggestionRequest request, Action<TimeSpan> onRateLimited, CancellationToken ct)
    {
        var task = request.Task!;
        var contentId = request.ContentId!.Value;
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
                        $"You can request at most {settings.QuotaPerHour} AI suggestions per hour.");
                }
            }

            if (!ai.IsConfigured)
            {
                throw Unavailable("No AI provider is configured.");
            }

            var input = MetadataPrompts.BuildInput(source, Math.Max(500, settings.MaxInputChars));
            if (input.Length == 0)
            {
                throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "The content has no text to analyse yet.",
                    new Dictionary<string, string[]> { ["contentId"] = ["The content has no title, summary or body text."] });
            }

            var id = Guid.CreateVersion7();
            await AiSql.InsertPendingAsync(connection, tx, id, actorId, AiTasks.AuditAction(task), source.ObjectType, contentId,
                ai.ProviderKey, ai.Model, input.Length, Sha256(input), ct);
            return (id, MetadataPrompts.For(task, input));
        }, ct);

        var stopwatch = Stopwatch.StartNew();
        AiChatResult result;
        IReadOnlyList<string> suggestions;
        try
        {
            result = await ai.CompleteAsync(prompt, ct);
            suggestions = MetadataPrompts.ParseSuggestions(task, result.Content);
            if (suggestions.Count == 0)
            {
                throw new AiProviderException(AiProviderException.BadResponse, "The AI answer contained no usable suggestion.");
            }
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
        return new AiSuggestion(suggestions, RequiresHumanApproval: true);
    }

    internal static string Sha256(string input) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

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

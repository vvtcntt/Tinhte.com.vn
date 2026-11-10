using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tinhte.Modules.Ai.Application;

namespace Tinhte.Modules.Ai.Infrastructure;

/// <summary>Một lượt hỏi model: system prompt (chỉ dẫn) + user (dữ liệu).</summary>
public sealed record AiChatPrompt(string System, string User);

public sealed record AiChatResult(string Content, int? PromptTokens, int? CompletionTokens, string? FinishReason);

/// <summary>Cổng gọi model chat. Tách interface để test thay bằng bản giả, không gọi mạng.</summary>
public interface IAiChatClient
{
    /// <summary>Mã nhà cung cấp và model ghi vào nhật ký.</summary>
    string ProviderKey { get; }

    string Model { get; }

    bool IsConfigured { get; }

    /// <exception cref="AiProviderException">Không gọi được hoặc câu trả lời không dùng được.</exception>
    Task<AiChatResult> CompleteAsync(AiChatPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>Lỗi phía nhà cung cấp AI. <see cref="Reason"/> ghi vào tt.ai_requests.error_code; không trả nguyên văn cho client.</summary>
public sealed class AiProviderException(string reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public const string NotConfigured = "NOT_CONFIGURED";
    public const string Timeout = "PROVIDER_TIMEOUT";
    public const string Network = "PROVIDER_NETWORK";
    public const string RateLimited = "PROVIDER_RATE_LIMITED";
    public const string Rejected = "PROVIDER_REJECTED";
    public const string ServerError = "PROVIDER_ERROR";
    public const string BadResponse = "BAD_RESPONSE";

    public string Reason { get; } = reason;
}

/// <summary>
/// Client chat-completions kiểu OpenAI, dùng được với mọi cổng tương thích (YesScale, OpenAI, Azure OpenAI qua proxy...):
/// <c>POST {BaseUrl}/chat/completions</c>, header <c>Authorization: Bearer {ApiKey}</c>, body {model, messages, max_tokens?, temperature?}.
/// Thử lại khi 429 (theo Retry-After, tối đa 5 giây), 5xx, lỗi mạng, quá thời gian; không thử lại 4xx khác (sai khóa, sai model, bị từ chối).
/// Không ghi nội dung gửi đi vào log; thân lỗi của nhà cung cấp chỉ ghi log (cắt ngắn), không trả cho client.
/// </summary>
public sealed partial class OpenAiCompatibleChatClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<AiOptions> options,
    ILogger<OpenAiCompatibleChatClient> logger) : IAiChatClient
{
    public const string HttpClientName = "tinhte-ai-provider";

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(5);

    private AiProviderOptions Provider => options.CurrentValue.Provider;

    public string ProviderKey => Provider.Key;

    public string Model => Provider.Model?.Trim() ?? "";

    public bool IsConfigured => Provider.IsConfigured;

    public async Task<AiChatResult> CompleteAsync(AiChatPrompt prompt, CancellationToken cancellationToken)
    {
        var provider = Provider;
        if (!provider.IsConfigured || provider.Endpoint is not { } endpoint)
        {
            throw new AiProviderException(AiProviderException.NotConfigured, "AI provider is not configured.");
        }

        var body = BuildBody(provider, prompt);
        var attempts = Math.Max(1, provider.MaxAttempts);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, provider.TimeoutSeconds));
        var http = httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 1; ; attempt++)
        {
            var last = attempt >= attempts;
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey!.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            TimeSpan? retryAfter = null;
            AiProviderException failure;
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, attemptCts.Token);
                var text = await response.Content.ReadAsStringAsync(attemptCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return Parse(text);
                }

                var status = (int)response.StatusCode;
                LogProviderStatus(logger, provider.Key, status, attempt, Truncate(text, 300));
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    retryAfter = RetryAfterOf(response);
                    failure = new AiProviderException(AiProviderException.RateLimited, "AI provider is rate limiting requests.");
                }
                else if (status >= 500)
                {
                    failure = new AiProviderException(AiProviderException.ServerError, $"AI provider returned HTTP {status}.");
                }
                else
                {
                    // 400/401/403/404...: sai cấu hình hoặc bị từ chối — thử lại không giúp gì.
                    throw new AiProviderException(AiProviderException.Rejected, $"AI provider rejected the request with HTTP {status}.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException e)
            {
                failure = new AiProviderException(AiProviderException.Timeout, $"AI provider did not answer within {timeout.TotalSeconds:0}s.", e);
            }
            catch (HttpRequestException e)
            {
                LogNetworkError(logger, provider.Key, attempt, e.Message);
                failure = new AiProviderException(AiProviderException.Network, "AI provider could not be reached.", e);
            }

            if (last) throw failure;
            var delay = retryAfter ?? TimeSpan.FromMilliseconds(Math.Max(0, provider.RetryDelayMs) * attempt);
            await Task.Delay(delay < MaxRetryAfter ? delay : MaxRetryAfter, cancellationToken);
        }
    }

    internal static string BuildBody(AiProviderOptions provider, AiChatPrompt prompt)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = provider.Model!.Trim(),
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = prompt.System },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = prompt.User },
            },
        };
        if (provider.MaxOutputTokens > 0) body["max_tokens"] = provider.MaxOutputTokens;
        if (provider.Temperature is { } temperature) body["temperature"] = temperature;
        return JsonSerializer.Serialize(body);
    }

    internal static AiChatResult Parse(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            int? promptTokens = null, completionTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = IntOf(usage, "prompt_tokens");
                completionTokens = IntOf(usage, "completion_tokens");
            }

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString()))
                {
                    return new AiChatResult(content.GetString()!.Trim(), promptTokens, completionTokens, finish);
                }
            }
        }
        catch (JsonException e)
        {
            throw new AiProviderException(AiProviderException.BadResponse, "AI provider returned a response that is not JSON.", e);
        }

        throw new AiProviderException(AiProviderException.BadResponse, "AI provider response has no message content.");
    }

    private static int? IntOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 0 ? n : null;

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } header) return null;
        if (header.Delta is { } delta) return delta;
        return header.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning, Message = "AI provider {Provider} returned HTTP {Status} (attempt {Attempt}): {Body}")]
    private static partial void LogProviderStatus(ILogger logger, string provider, int status, int attempt, string body);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Warning, Message = "AI provider {Provider} unreachable (attempt {Attempt}): {Error}")]
    private static partial void LogNetworkError(ILogger logger, string provider, int attempt, string error);
}

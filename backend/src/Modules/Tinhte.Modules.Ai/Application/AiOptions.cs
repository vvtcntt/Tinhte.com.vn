namespace Tinhte.Modules.Ai.Application;

/// <summary>
/// Cấu hình mục <c>Ai</c>. Khóa API KHÔNG ghi vào appsettings hay DB: đặt qua biến môi trường <c>Ai__Provider__ApiKey</c> hoặc secret store.
/// Thiếu BaseUrl/ApiKey/Model → gọi AI trả 503 DEPENDENCY_UNAVAILABLE (không gửi gì ra ngoài, không ghi nhật ký).
/// </summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    public AiProviderOptions Provider { get; set; } = new();

    /// <summary>Số ký tự tối đa của nội dung gửi đi (tiêu đề + tóm tắt + thân bài), cắt ở phía server.</summary>
    public int MaxInputChars { get; set; } = 12_000;

    /// <summary>Số lần gọi AI tối đa của một người trong 60 phút trượt (429 RATE_LIMITED + Retry-After). 0 = không giới hạn.</summary>
    public int QuotaPerHour { get; set; } = 60;
}

/// <summary>Nhà cung cấp tương thích OpenAI (<c>POST {BaseUrl}/chat/completions</c>, Bearer token). Mặc định: cổng YesScale.</summary>
public sealed class AiProviderOptions
{
    /// <summary>Mã nhà cung cấp ghi vào nhật ký (tt.ai_requests.provider_key): chữ thường, số, '-', '_'.</summary>
    public string Key { get; set; } = "yescale";

    /// <summary>Gốc API, ví dụ <c>https://.../v1/</c>. Chỉ https (http chỉ cho máy local/loopback khi dev).</summary>
    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Mã model gửi trong trường <c>model</c>, ví dụ <c>gpt-5.4-mini</c>.</summary>
    public string? Model { get; set; }

    /// <summary>Thời gian chờ MỖI lần thử.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Số lần thử tối đa (thử lại khi 429, 5xx, lỗi mạng, quá thời gian). Người dùng đang chờ nên giữ thấp.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Chờ giữa hai lần thử = RetryDelayMs × số lần đã thử (hoặc Retry-After của nhà cung cấp, tối đa 5 giây).</summary>
    public int RetryDelayMs { get; set; } = 500;

    /// <summary>Gửi <c>max_tokens</c> khi &gt; 0.</summary>
    public int MaxOutputTokens { get; set; } = 800;

    /// <summary>Gửi <c>temperature</c> khi có giá trị. Để trống với model không nhận tham số này.</summary>
    public double? Temperature { get; set; }

    public Uri? Endpoint => Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var uri) && IsAllowedScheme(uri)
        ? new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions")
        : null;

    public bool IsConfigured => Endpoint is not null && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model)
        && System.Text.RegularExpressions.Regex.IsMatch(Key, "^[a-z0-9][a-z0-9_-]{0,39}$");

    private static bool IsAllowedScheme(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
}

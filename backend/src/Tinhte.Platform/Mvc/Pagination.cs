using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tinhte.Platform.Errors;

namespace Tinhte.Platform.Mvc;

/// <summary>Tham số phân trang chung (components.parameters Cursor/Limit). Dùng <c>[FromQuery] PageRequest page</c>.</summary>
public sealed class PageRequest
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    /// <summary>Hợp đồng ghi "Example bounds; final limit TBD performance/PO".</summary>
    [FromQuery(Name = "limit")]
    [Range(1, MaxLimit)]
    public int Limit { get; init; } = DefaultLimit;

    [FromQuery(Name = "cursor")]
    [StringLength(512)]
    public string? Cursor { get; init; }
}

/// <summary>Trang kết quả: JSON <c>{ "items": [...], "nextCursor": "..." | null }</c> như ArticleList/VideoList của hợp đồng.</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// Cursor mờ, ký HMAC-SHA256 (components.parameters.Cursor): thứ tự ổn định theo (khóa sắp xếp, id).
/// Cursor gắn với <c>scope</c> = site + tên danh sách (+ bộ lọc), nên không dùng chéo giữa danh sách hay subdomain.
/// Sửa, cắt, đổi scope hoặc ký bằng khóa khác → 400 INVALID_CURSOR.
/// </summary>
public interface ICursorCodec
{
    string Encode<TPosition>(string scope, TPosition position);

    TPosition Decode<TPosition>(string scope, string cursor);
}

public sealed partial class HmacCursorCodec : ICursorCodec
{
    public const string SigningKeyConfig = "Pagination:CursorSigningKey";
    private const int MinKeyBytes = 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly byte[] _key;

    public HmacCursorCodec(IConfiguration configuration, IHostEnvironment environment, ILogger<HmacCursorCodec> logger)
    {
        var configured = configuration[SigningKeyConfig];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = Convert.FromBase64String(configured);
            if (_key.Length < MinKeyBytes) throw new InvalidOperationException($"{SigningKeyConfig} must be at least {MinKeyBytes} bytes (base64).");
        }
        else if (environment.IsDevelopment())
        {
            // Dev: khóa ngẫu nhiên theo tiến trình → cursor mất hiệu lực khi khởi động lại (chấp nhận được khi dev).
            _key = RandomNumberGenerator.GetBytes(MinKeyBytes);
            LogEphemeralKey(logger, SigningKeyConfig);
        }
        else
        {
            throw new InvalidOperationException($"{SigningKeyConfig} is required outside Development (secret manager; same key on every API instance).");
        }
    }

    public string Encode<TPosition>(string scope, TPosition position)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Envelope<TPosition>(1, scope, position), Json);
        return Base64Url(payload) + "." + Base64Url(Sign(payload));
    }

    public TPosition Decode<TPosition>(string scope, string cursor)
    {
        var dot = cursor.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == cursor.Length - 1) throw Invalid();
        byte[] payload, signature;
        try
        {
            payload = FromBase64Url(cursor[..dot]);
            signature = FromBase64Url(cursor[(dot + 1)..]);
        }
        catch (FormatException)
        {
            throw Invalid();
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(payload))) throw Invalid();
        Envelope<TPosition>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope<TPosition>>(payload, Json);
        }
        catch (JsonException)
        {
            throw Invalid();
        }

        if (envelope is not { V: 1 } || envelope.S != scope || envelope.P is null) throw Invalid();
        return envelope.P;
    }

    private byte[] Sign(byte[] payload) => HMACSHA256.HashData(_key, payload);

    private static DomainProblemException Invalid() =>
        new(ErrorCodes.InvalidCursor, "Invalid cursor", "The cursor is not valid for this list. Restart from the first page.");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    private sealed record Envelope<TPosition>(int V, string S, TPosition P);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning, Message = "{Key} is not configured: using an ephemeral key (Development only)")]
    private static partial void LogEphemeralKey(ILogger logger, string key);
}

public static class CursorPaging
{
    /// <summary>
    /// Truy vấn lấy <c>limit + 1</c> dòng; hàm này cắt về <c>limit</c> và tạo nextCursor từ phần tử cuối nếu còn trang sau.
    /// </summary>
    public static CursorPage<T> ToPage<T, TPosition>(this IReadOnlyList<T> rowsPlusOne, int limit, ICursorCodec codec, string scope, Func<T, TPosition> positionOf)
    {
        if (rowsPlusOne.Count <= limit) return new CursorPage<T>(rowsPlusOne, null);
        var items = rowsPlusOne.Take(limit).ToList();
        return new CursorPage<T>(items, codec.Encode(scope, positionOf(items[^1])));
    }

    /// <summary>Scope chuẩn: <c>{siteKey}:{list}</c> (+ bộ lọc nếu danh sách có lọc, ví dụ category).</summary>
    public static string Scope(string siteKey, string list, string? filter = null) =>
        filter is null ? $"{siteKey}:{list}" : $"{siteKey}:{list}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(filter)))[..16]}";
}

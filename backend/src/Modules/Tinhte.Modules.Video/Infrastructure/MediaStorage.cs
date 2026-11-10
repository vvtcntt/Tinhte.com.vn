using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.Video.Infrastructure;

/// <summary>Object cần tải lên theo một upload intent: URL chỉ được ghi đúng key + đúng kích thước, trước hạn.</summary>
public sealed record MediaUploadTarget(Guid MediaId, string StorageKey, long SizeBytes, string ContentType, DateTimeOffset ExpiresAt);

/// <summary>Key object do server sinh: <c>media/yyyy/MM/{mediaId}</c> — không chứa tên tệp của người dùng.</summary>
public static class MediaStorageKeys
{
    public static string For(Guid mediaId, DateTimeOffset at) =>
        string.Create(CultureInfo.InvariantCulture, $"media/{at.UtcDateTime:yyyy}/{at.UtcDateTime:MM}/{mediaId}");
}

/// <summary>Kết quả HEAD object: kích thước + sha256 (hex thường) do kho tính/xác nhận.</summary>
public sealed record StoredObject(long SizeBytes, string ChecksumSha256);

/// <summary>Cách trả nội dung object cho getAdminMediaContent.</summary>
public abstract record MediaContent
{
    /// <summary>Kho thật: chuyển hướng 302 tới URL đọc có chữ ký, ngắn hạn (<see cref="MediaContentRedirect.ExpiresAt"/>).</summary>
    public static MediaContent Redirect(Uri url, DateTimeOffset expiresAt) => new MediaContentRedirect(url, expiresAt);

    /// <summary>Kho Local (dev): API tự trả byte.</summary>
    public static MediaContent Stream(Stream content, long length) => new MediaContentStream(content, length);
}

public sealed record MediaContentRedirect(Uri Url, DateTimeOffset ExpiresAt) : MediaContent;

public sealed record MediaContentStream(Stream Content, long Length) : MediaContent;

/// <summary>
/// Kho lưu trữ media (ADR-0006). Bản production (object storage + URL ký sẵn, WBS-039) chưa có vì chưa chọn vendor (WBS-035);
/// bản dev là <see cref="LocalMediaStorage"/>. Object luôn PRIVATE: phục vụ công khai đi qua site <c>media</c> + grant (WBS-039).
/// </summary>
public interface IMediaStorage
{
    /// <summary>false → các thao tác tải lên trả 503 DEPENDENCY_UNAVAILABLE.</summary>
    bool IsConfigured { get; }

    /// <summary>URL ngắn hạn để client PUT nội dung tệp. <paramref name="requestOrigin"/> = origin của site đang gọi (không viết cứng host).</summary>
    Uri CreateUploadUrl(MediaUploadTarget target, Uri requestOrigin);

    Task<StoredObject?> HeadAsync(string storageKey, CancellationToken ct);

    /// <summary>Đọc <paramref name="length"/> byte đầu (nhận dạng định dạng). null nếu object không tồn tại.</summary>
    Task<byte[]?> ReadHeaderAsync(string storageKey, int length, CancellationToken ct);

    Task DeleteAsync(string storageKey, CancellationToken ct);

    /// <summary>
    /// Đọc object để xem trong CMS (getAdminMediaContent). Adapter object storage trả <see cref="MediaContentRedirect"/> với URL đọc ký sẵn
    /// sống ngắn (vài phút, chỉ đúng key này); kho Local trả luồng tệp. null nếu object không tồn tại.
    /// </summary>
    Task<MediaContent?> OpenReadAsync(string storageKey, CancellationToken ct);
}

/// <summary>Chưa cấu hình kho (mặc định ngoài Development): không nhận tải lên, fail-closed.</summary>
public sealed class UnconfiguredMediaStorage : IMediaStorage
{
    public bool IsConfigured => false;

    public Uri CreateUploadUrl(MediaUploadTarget target, Uri requestOrigin) => throw Unavailable();

    public Task<StoredObject?> HeadAsync(string storageKey, CancellationToken ct) => throw Unavailable();

    public Task<byte[]?> ReadHeaderAsync(string storageKey, int length, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    public Task DeleteAsync(string storageKey, CancellationToken ct) => Task.CompletedTask;

    public Task<MediaContent?> OpenReadAsync(string storageKey, CancellationToken ct) => throw Unavailable();

    public static DomainProblemException Unavailable() =>
        new(ErrorCodes.DependencyUnavailable, "Media storage unavailable", "Media storage is not configured for this environment.");
}

/// <summary>
/// DEV-ONLY (ADR-0006): giả lập object storage bằng thư mục cục bộ. URL tải lên trỏ về chính API
/// (<c>PUT /api/v1/admin/media/{id}/dev-upload?expires=&amp;signature=</c>, cùng origin site cms), ký HMAC theo media + kích thước + hạn
/// như URL ký sẵn của S3. Khóa ký ngẫu nhiên theo tiến trình: khởi động lại API thì URL cũ hết hiệu lực (client tạo intent mới).
/// Không bao giờ bật ngoài Development (VideoModule kiểm lúc khởi động).
/// </summary>
public sealed partial class LocalMediaStorage : IMediaStorage
{
    public const string UploadPathSuffix = "dev-upload";

    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);

    public LocalMediaStorage(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public bool IsConfigured => true;

    public Uri CreateUploadUrl(MediaUploadTarget target, Uri requestOrigin)
    {
        var expires = target.ExpiresAt.ToUnixTimeSeconds();
        var path = $"/api/v1/admin/media/{target.MediaId}/{UploadPathSuffix}?expires={expires.ToString(CultureInfo.InvariantCulture)}&signature={Sign(target.MediaId, target.SizeBytes, expires)}";
        return new Uri(requestOrigin, path);
    }

    public string Sign(Guid mediaId, long sizeBytes, long expiresUnix)
    {
        var payload = Encoding.UTF8.GetBytes($"{mediaId:N}|{sizeBytes.ToString(CultureInfo.InvariantCulture)}|{expiresUnix.ToString(CultureInfo.InvariantCulture)}");
        return Base64Url(HMACSHA256.HashData(_signingKey, payload));
    }

    public bool Verify(Guid mediaId, long sizeBytes, long expiresUnix, string? signature) =>
        signature is { Length: > 0 and < 100 } && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(mediaId, sizeBytes, expiresUnix)), Encoding.ASCII.GetBytes(signature));

    /// <summary>
    /// Ghi nội dung tải lên: ghi ra tệp tạm rồi đổi tên (không để lại tệp dở). Vượt <paramref name="maxBytes"/> → xóa tệp tạm,
    /// 413 PAYLOAD_TOO_LARGE. PUT lại ghi đè (thử lại sau lỗi mạng/checksum).
    /// </summary>
    public async Task<long> WriteAsync(string storageKey, Stream body, long maxBytes, CancellationToken ct)
    {
        var target = PathOf(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{Guid.NewGuid():N}.part";
        long written = 0;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > maxBytes)
                    {
                        throw new DomainProblemException(ErrorCodes.PayloadTooLarge, "Payload too large",
                            $"The upload exceeds the {maxBytes} bytes declared in the upload intent.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            File.Move(temp, target, overwrite: true);
            return written;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<StoredObject?> HeadAsync(string storageKey, CancellationToken ct)
    {
        var path = PathOf(storageKey);
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(file, ct);
        return new StoredObject(file.Length, Convert.ToHexStringLower(hash));
    }

    public async Task<byte[]?> ReadHeaderAsync(string storageKey, int length, CancellationToken ct)
    {
        var path = PathOf(storageKey);
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var buffer = new byte[length];
        var total = 0;
        int read;
        while (total < length && (read = await file.ReadAsync(buffer.AsMemory(total), ct)) > 0) total += read;
        return buffer[..total];
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        var path = PathOf(storageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>Mở tệp để API trả thẳng byte (không có URL ký cho kho Local). Người gọi đóng luồng.</summary>
    public Task<MediaContent?> OpenReadAsync(string storageKey, CancellationToken ct)
    {
        var path = PathOf(storageKey);
        if (!File.Exists(path)) return Task.FromResult<MediaContent?>(null);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<MediaContent?>(MediaContent.Stream(file, file.Length));
    }

    /// <summary>Key do server sinh (<c>media/yyyy/MM/{uuid}</c>); vẫn kiểm định dạng để không bao giờ ghi ra ngoài thư mục gốc.</summary>
    private string PathOf(string storageKey)
    {
        if (!StorageKeyRegex().IsMatch(storageKey)) throw new InvalidOperationException("Invalid storage key.");
        var full = Path.GetFullPath(Path.Combine(Root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid storage key.");
        return full;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("^media/[0-9]{4}/[0-9]{2}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageKeyRegex();
}

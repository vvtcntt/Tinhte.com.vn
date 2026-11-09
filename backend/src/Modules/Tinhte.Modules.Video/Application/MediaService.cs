using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;
using Tinhte.Modules.Video.Domain;
using Tinhte.Modules.Video.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.Video.Application;

/// <summary>
/// Tải lên media qua upload intent (WBS-046/036 tối thiểu; FR-VID-005/006/007, FR-CMS-009; ADR-0006):
/// <list type="number">
///   <item>createUploadIntent: kiểm loại/kích thước, tạo dòng UPLOADING + URL tải lên ngắn hạn + token finalize (DB chỉ giữ sha256).</item>
///   <item>Client PUT tệp lên URL (kho lưu trữ; dev: API tự nhận).</item>
///   <item>finalizeUpload: HEAD object, so kích thước + sha256 với intent → UPLOADED, đưa vào pipeline.</item>
///   <item>Pipeline (worker thật WBS-038; dev: giả lập) → READY / REJECTED / FAILED. CMS đọc trạng thái theo chu kỳ.</item>
/// </list>
/// Tải lên không bao giờ đổi quyền/hiển thị: media mới luôn PRIVATE + UNVERIFIED (0305).
/// </summary>
public sealed class MediaService(ITinhteDatabase database, IMediaStorage storage, IMediaProcessor processor, IOptions<MediaOptions> options, TimeProvider clock)
{
    private readonly MediaOptions _options = options.Value;

    // ---------------------------------------------------------------- createUploadIntent

    public async Task<UploadIntent> CreateIntentAsync(Guid actorId, CreateUploadIntentRequest request, Uri requestOrigin, CancellationToken ct)
    {
        var fileName = CleanFileName(request.FileName) ?? throw Invalid("fileName", "A file name without path or control characters is required.");
        var contentType = MediaFormats.Normalize(request.ContentType)
            ?? throw Invalid("contentType", $"Unsupported content type. Accepted: {string.Join(", ", MediaFormats.ContentTypes.Order(StringComparer.Ordinal))}.");
        var mediaType = MediaFormats.MediaTypeOf(contentType);
        var size = request.SizeBytes!.Value;
        var max = _options.MaxBytesFor(mediaType);
        if (size > max)
        {
            throw new DomainProblemException(ErrorCodes.PayloadTooLarge, "Upload too large",
                $"{mediaType} uploads are limited to {max} bytes; this file is {size} bytes.");
        }

        if (!storage.IsConfigured) throw UnconfiguredMediaStorage.Unavailable();

        var now = clock.GetUtcNow();
        var mediaId = Guid.CreateVersion7(now);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = TruncateToSeconds(now + _options.UploadIntentTtl);
        var storageKey = MediaStorageKeys.For(mediaId, now);

        await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await using var insert = VideoSql.Command(connection, tx, """
                INSERT INTO tt.media_assets (id, status, media_type, file_name, content_type, size_bytes, checksum_sha256, storage_key,
                                             upload_token_hash, upload_expires_at, created_by)
                VALUES (@id, 'UPLOADING', @type, @name, @content_type, @size, @checksum, @key, @token_hash, @expires, @actor)
                """)
                .WithUuid("id", mediaId).WithText("type", mediaType).WithText("name", fileName).WithText("content_type", contentType)
                .WithLong("size", size).WithText("checksum", request.ChecksumSha256.ToLowerInvariant()).WithText("key", storageKey)
                .WithText("token_hash", TokenHash(token)).WithTime("expires", expiresAt).WithUuid("actor", actorId);
            return await insert.ExecuteNonQueryAsync(ct);
        }, ct);

        var uploadUrl = storage.CreateUploadUrl(new MediaUploadTarget(mediaId, storageKey, size, contentType, expiresAt), requestOrigin);
        return new UploadIntent(mediaId, uploadUrl, expiresAt, token);
    }

    // ---------------------------------------------------------------- finalizeUpload

    public async Task<MediaJobAccepted> FinalizeAsync(Guid actorId, Guid mediaId, string uploadToken, CancellationToken ct)
    {
        var media = await LoadUploadAsync(mediaId, ct) ?? throw NotFound();

        // Gọi lại sau khi đã finalize (khóa idempotency khác, mất response…): trả trạng thái hiện tại, không làm lại.
        if (MediaStatuses.InPipeline.Contains(media.Status)) return new MediaJobAccepted(mediaId, media.Status);
        if (media.Status != MediaStatuses.Uploading)
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict", $"Media is {media.Status}; only an UPLOADING media can be finalized.");
        }

        if (media.TokenHash is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(TokenHash(uploadToken)), Encoding.ASCII.GetBytes(media.TokenHash)))
        {
            throw Invalid("uploadToken", "The upload token does not match this media.");
        }

        if (media.ExpiresAt is { } expires && expires <= clock.GetUtcNow())
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "Upload intent expired", "The upload intent has expired. Create a new upload intent and upload the file again.");
        }

        if (!storage.IsConfigured) throw UnconfiguredMediaStorage.Unavailable();

        // Kiểm object NGOÀI transaction (băm cả tệp có thể lâu); transaction chỉ đổi trạng thái nếu chưa ai đổi.
        var stored = await storage.HeadAsync(media.StorageKey, ct)
            ?? throw new DomainProblemException(ErrorCodes.MediaNotReady, "Media not uploaded", "No uploaded file was found for this media. Upload the file to uploadUrl, then finalize.");
        if (stored.SizeBytes != media.SizeBytes || stored.ChecksumSha256 != media.ChecksumSha256)
        {
            // Tệp hỏng/khác tệp đã khai báo: xóa để client tải lại lên cùng URL (intent còn hạn) rồi finalize lại.
            await storage.DeleteAsync(media.StorageKey, ct);
            throw stored.SizeBytes != media.SizeBytes
                ? Invalid("sizeBytes", $"The uploaded file has {stored.SizeBytes} bytes but the upload intent declared {media.SizeBytes}. Upload it again.")
                : Invalid("checksumSha256", "The uploaded file does not match the declared SHA-256 checksum. Upload it again.");
        }

        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await using var update = VideoSql.Command(connection, tx, """
                UPDATE tt.media_assets
                   SET status = 'UPLOADED', uploaded_at = now(), upload_token_hash = NULL, upload_expires_at = NULL, error_code = NULL
                 WHERE id = @id AND status = 'UPLOADING' AND upload_token_hash = @token_hash
                """).WithUuid("id", mediaId).WithText("token_hash", media.TokenHash);
            if (await update.ExecuteNonQueryAsync(ct) == 1) return new MediaJobAccepted(mediaId, MediaStatuses.Uploaded);

            // Request song song đã finalize trước.
            var current = await StatusAsync(connection, tx, mediaId, ct);
            return current is not null && MediaStatuses.InPipeline.Contains(current)
                ? new MediaJobAccepted(mediaId, current)
                : throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict", $"Media is {current}; only an UPLOADING media can be finalized.");
        }, ct);
    }

    // ---------------------------------------------------------------- dev upload (LocalMediaStorage)

    /// <summary>
    /// Nhận nội dung tệp cho kho Local (ADR-0006, chỉ Development): URL phải đúng chữ ký + còn hạn, media còn UPLOADING,
    /// không nhận quá kích thước đã khai báo.
    /// </summary>
    public async Task ReceiveDevUploadAsync(LocalMediaStorage local, Guid mediaId, long expiresUnix, string? signature, long? contentLength, Stream body, CancellationToken ct)
    {
        var media = await LoadUploadAsync(mediaId, ct) ?? throw NotFound();
        if (media.Status != MediaStatuses.Uploading)
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict", $"Media is {media.Status}; the upload is already finalized.");
        }

        if (expiresUnix <= clock.GetUtcNow().ToUnixTimeSeconds() || !local.Verify(mediaId, media.SizeBytes, expiresUnix, signature))
        {
            throw new DomainProblemException(ErrorCodes.Forbidden, "Upload URL invalid", "The upload URL is invalid or expired. Create a new upload intent.");
        }

        if (contentLength > media.SizeBytes)
        {
            throw new DomainProblemException(ErrorCodes.PayloadTooLarge, "Payload too large", $"The upload intent allows {media.SizeBytes} bytes.");
        }

        await local.WriteAsync(media.StorageKey, body, media.SizeBytes, ct);
    }

    // ---------------------------------------------------------------- đọc

    public async Task<MediaStatus> GetAsync(Guid mediaId, CancellationToken ct)
    {
        await processor.AdvanceAsync(ct);
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = VideoSql.Command(connection, null, VideoSql.MediaSelect + " WHERE m.id = @id").WithUuid("id", mediaId);
        await using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? VideoSql.ReadMedia(r) : throw NotFound();
    }

    public async Task<IReadOnlyList<MediaStatus>> ListAsync(MediaFilter filter, MediaPosition? after, int limitPlusOne, CancellationToken ct)
    {
        await processor.AdvanceAsync(ct);
        var sql = new StringBuilder(VideoSql.MediaSelect).Append(" WHERE TRUE");
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = VideoSql.Command(connection, null, "");
        if (filter.MediaType is { } type) { sql.Append(" AND m.media_type = @type"); command.WithText("type", type); }
        if (filter.RightsStatus is { } rights) { sql.Append(" AND m.rights_status = @rights"); command.WithText("rights", rights); }
        if (filter.Visibility is { } visibility) { sql.Append(" AND m.visibility = @visibility"); command.WithText("visibility", visibility); }
        if (filter.Q is { } q) { sql.Append(@" AND m.file_name ILIKE @q ESCAPE '\'"); command.WithText("q", VideoSql.LikePattern(q)); }
        if (after is not null)
        {
            sql.Append(" AND (m.created_at, m.id) < (@after_at, @after_id)");
            command.WithTime("after_at", after.At).WithUuid("after_id", after.Id);
        }

        sql.Append(" ORDER BY m.created_at DESC, m.id DESC LIMIT @limit");
        command.Parameters.AddWithValue("limit", limitPlusOne);
        command.CommandText = sql.ToString();

        var items = new List<MediaStatus>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(VideoSql.ReadMedia(r));
        return items;
    }

    // ---------------------------------------------------------------- nội bộ

    private sealed record UploadRow(string Status, string StorageKey, long SizeBytes, string ChecksumSha256, string? TokenHash, DateTimeOffset? ExpiresAt);

    /// <summary>Media tạo qua upload intent. Dòng cũ của schema 04 (không có storage_key) coi như không tìm thấy.</summary>
    private async Task<UploadRow?> LoadUploadAsync(Guid mediaId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = VideoSql.Command(connection, null, """
            SELECT status, storage_key, size_bytes, checksum_sha256, upload_token_hash, upload_expires_at
              FROM tt.media_assets WHERE id = @id AND storage_key IS NOT NULL AND size_bytes IS NOT NULL AND checksum_sha256 IS NOT NULL
            """).WithUuid("id", mediaId);
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new UploadRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), VideoSql.NullableTime(r, 5));
    }

    private static async Task<string?> StatusAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid mediaId, CancellationToken ct)
    {
        await using var command = VideoSql.Command(connection, tx, "SELECT status FROM tt.media_assets WHERE id = @id").WithUuid("id", mediaId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Chỉ giữ tên tệp (bỏ thư mục do trình duyệt cũ gửi kèm), không ký tự điều khiển. null nếu không còn gì.</summary>
    public static string? CleanFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = value[(value.LastIndexOfAny(['/', '\\']) + 1)..].Trim();
        if (name.Length is 0 or > 255 || name is "." or ".." || name.Any(char.IsControl)) return null;
        return name;
    }

    public static string TokenHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset t) => DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds());

    private static DomainProblemException NotFound() => new(ErrorCodes.NotFound, "Media not found", "No media with this id.");

    private static DomainProblemException Invalid(string field, string message) =>
        new(ErrorCodes.ValidationFailed, "Validation failed", message, new Dictionary<string, string[]> { [field] = [message] });
}

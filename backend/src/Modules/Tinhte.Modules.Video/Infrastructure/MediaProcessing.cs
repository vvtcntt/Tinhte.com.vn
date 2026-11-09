using Microsoft.Extensions.Logging;
using Npgsql;
using Tinhte.Modules.Video.Domain;
using Tinhte.Platform.Data;

namespace Tinhte.Modules.Video.Infrastructure;

/// <summary>
/// Pipeline xử lý media sau finalize (quét, kiểm định dạng, transcode — WBS-036…038). Production: worker hàng đợi riêng
/// (chưa có, chờ chọn vendor WBS-035) → <see cref="NoMediaProcessor"/>. Dev: <see cref="SimulatedMediaProcessor"/>.
/// </summary>
public interface IMediaProcessor
{
    /// <summary>Cho pipeline tiến các bước đã đến hạn. API gọi trước khi đọc trạng thái media.</summary>
    Task AdvanceAsync(CancellationToken ct);
}

/// <summary>Không có pipeline trong tiến trình API: media đứng ở UPLOADED cho tới khi worker thật xử lý.</summary>
public sealed class NoMediaProcessor : IMediaProcessor
{
    public Task AdvanceAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// DEV-ONLY (ADR-0006): giả lập pipeline ngay trong API, không gọi dịch vụ ngoài, không chạy nền.
/// Mỗi lần CMS đọc trạng thái, media đã đứng yên đủ <c>stepDelay</c> tiến MỘT bước:
/// UPLOADED → VALIDATING → (đầu tệp đúng định dạng khai báo? VIDEO: PROCESSING, khác: READY : REJECTED/UNSUPPORTED_FORMAT)
/// → PROCESSING → READY. Không transcode, không tạo poster/rendition, không quét virus: READY ở đây KHÔNG có nghĩa phát được.
/// </summary>
public sealed class SimulatedMediaProcessor(ITinhteDatabase database, IMediaStorage storage, TimeSpan stepDelay, ILogger<SimulatedMediaProcessor> logger) : IMediaProcessor
{
    public const string UnsupportedFormat = "UNSUPPORTED_FORMAT";
    public const string ObjectMissing = "OBJECT_MISSING";

    private sealed record Pending(Guid Id, string Status, string? ContentType, string? StorageKey);

    public async Task AdvanceAsync(CancellationToken ct)
    {
        // Delay 0: đi hết pipeline trong một lần (mỗi vòng tiến một bước, tối đa 3 bước).
        for (var pass = 0; pass < 3; pass++)
        {
            if (await AdvanceOnceAsync(ct) == 0 || stepDelay > TimeSpan.Zero) return;
        }
    }

    private async Task<int> AdvanceOnceAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        var due = new List<Pending>();
        await using (var select = VideoSql.Command(connection, null, """
            SELECT id, status, content_type, storage_key FROM tt.media_assets
             WHERE status IN ('UPLOADED', 'VALIDATING', 'PROCESSING') AND updated_at <= now() - @delay
             ORDER BY updated_at LIMIT 50
            """).WithInterval("delay", stepDelay))
        await using (var r = await select.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                due.Add(new Pending(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
            }
        }

        var advanced = 0;
        foreach (var media in due)
        {
            var (next, error) = media.Status switch
            {
                MediaStatuses.Uploaded => (MediaStatuses.Validating, null),
                MediaStatuses.Validating => await ValidateAsync(media, ct),
                _ => (MediaStatuses.Ready, (string?)null),
            };

            // Chỉ đổi nếu chưa ai đổi trước (tránh hai request cùng tiến một bước).
            await using var update = VideoSql.Command(connection, null,
                "UPDATE tt.media_assets SET status = @next, error_code = @error WHERE id = @id AND status = @current")
                .WithText("next", next).WithText("error", error).WithUuid("id", media.Id).WithText("current", media.Status);
            advanced += await update.ExecuteNonQueryAsync(ct);
            logger.LogInformation("Simulated media pipeline: {MediaId} {From} → {To} {Error}", media.Id, media.Status, next, error);
        }

        return advanced;
    }

    private async Task<(string Status, string? Error)> ValidateAsync(Pending media, CancellationToken ct)
    {
        if (media.StorageKey is null || media.ContentType is null) return (MediaStatuses.Failed, ObjectMissing);
        var header = await storage.ReadHeaderAsync(media.StorageKey, MediaFormats.HeaderLength, ct);
        if (header is null) return (MediaStatuses.Failed, ObjectMissing);
        if (MediaFormats.Normalize(media.ContentType) is not { } type || !MediaFormats.HeaderMatches(type, header))
        {
            return (MediaStatuses.Rejected, UnsupportedFormat);
        }

        return (MediaFormats.MediaTypeOf(type) == MediaTypes.Video ? MediaStatuses.Processing : MediaStatuses.Ready, null);
    }
}

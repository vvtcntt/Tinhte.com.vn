namespace Tinhte.Modules.Video.Application;

/// <summary>
/// Cấu hình <c>Media:*</c> (ADR-0006). Giới hạn kích thước là DRAFT — chờ chốt cùng vendor/profile (WBS-035/037) và PO/Finance.
/// </summary>
public sealed class MediaOptions
{
    public const string Section = "Media";

    /// <summary>Kho lưu trữ: <c>Local</c> (chỉ Development) hoặc <c>None</c>. Mặc định: Local ở Development, None ở môi trường khác.</summary>
    public string? StorageProvider { get; set; }

    /// <summary>Thư mục gốc của kho Local. Mặc định: <c>%TEMP%/tinhte-media</c>.</summary>
    public string? LocalRoot { get; set; }

    /// <summary>Hạn của upload intent (URL tải lên + token finalize).</summary>
    public TimeSpan UploadIntentTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Hạn mức theo người tải (DRAFT, chờ PO): số upload intent tối đa trong <see cref="UploadQuotaWindow"/>.</summary>
    public int MaxIntentsPerWindow { get; set; } = 30;

    public TimeSpan UploadQuotaWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Tổng dung lượng các intent còn hạn chưa finalize của một người (DRAFT, chờ PO).</summary>
    public long MaxPendingUploadBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Intent hết hạn chưa finalize được dọn (FAILED/UPLOAD_EXPIRED + xóa object dở) sau hạn + khoảng này — đủ để một PUT đang chạy
    /// lúc hết hạn kết thúc trước khi object bị xóa.
    /// </summary>
    public TimeSpan ExpiredUploadGrace { get; set; } = TimeSpan.FromHours(1);

    public long MaxVideoBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public long MaxImageBytes { get; set; } = 20L * 1024 * 1024;

    public long MaxSubtitleBytes { get; set; } = 1L * 1024 * 1024;

    /// <summary>
    /// Bộ giả lập xử lý (chỉ chạy cùng kho Local): mỗi bước UPLOADED → VALIDATING → PROCESSING → READY cách nhau ít nhất
    /// khoảng này. 0 = đi hết pipeline trong một lần đọc (dùng cho test).
    /// </summary>
    public TimeSpan SimulatedStepDelay { get; set; } = TimeSpan.FromSeconds(2);

    public long MaxBytesFor(string mediaType) => mediaType switch
    {
        Domain.MediaTypes.Video => MaxVideoBytes,
        Domain.MediaTypes.Image => MaxImageBytes,
        Domain.MediaTypes.Subtitle => MaxSubtitleBytes,
        _ => 0,
    };
}

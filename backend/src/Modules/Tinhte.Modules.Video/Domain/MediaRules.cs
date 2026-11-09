namespace Tinhte.Modules.Video.Domain;

/// <summary>MediaStatus.status của hợp đồng v0.3 — khớp CHECK ck_media_assets_status (0330).</summary>
public static class MediaStatuses
{
    public const string Uploading = "UPLOADING";
    public const string Uploaded = "UPLOADED";
    public const string Validating = "VALIDATING";
    public const string Processing = "PROCESSING";
    public const string Ready = "READY";
    public const string Failed = "FAILED";
    public const string Quarantined = "QUARANTINED";
    public const string Rejected = "REJECTED";
    public const string Restricted = "RESTRICTED";

    /// <summary>Đang trong pipeline (finalize xong, chưa có kết quả) — MediaJobAccepted.status.</summary>
    public static readonly IReadOnlySet<string> InPipeline = new HashSet<string>(StringComparer.Ordinal) { Uploaded, Validating, Processing };

    /// <summary>Kết cục xấu: không gắn được vào video.</summary>
    public static readonly IReadOnlySet<string> Unusable = new HashSet<string>(StringComparer.Ordinal) { Failed, Quarantined, Rejected, Restricted };
}

public static class MediaTypes
{
    public const string Video = "VIDEO";
    public const string Image = "IMAGE";
    public const string Subtitle = "SUBTITLE";
    public const string Other = "OTHER";
    public const string Pattern = "^(VIDEO|IMAGE|SUBTITLE|OTHER)$";
}

public static class MediaRights
{
    public const string RightsPattern = "^(UNVERIFIED|CLEARED|RESTRICTED|EXPIRED|REVOKED)$";
    public const string VisibilityPattern = "^(PRIVATE|PUBLIC)$";

    /// <summary>Quyền đã bị hạn chế/thu hồi/hết hạn: không gắn vào nội dung (FR-CMS-009). UNVERIFIED vẫn gắn được vào bản nháp.</summary>
    public static readonly IReadOnlySet<string> Blocked = new HashSet<string>(StringComparer.Ordinal) { "RESTRICTED", "EXPIRED", "REVOKED" };
}

/// <summary>
/// Loại tệp nhận tải lên (DRAFT — chờ chốt profile với WBS-037 khi chọn vendor): MIME → mediaType + nhận dạng chữ ký tệp
/// (magic bytes) mà bước kiểm của pipeline dùng để từ chối tệp đổi đuôi.
/// </summary>
public static class MediaFormats
{
    private sealed record Format(string MediaType, Func<ReadOnlyMemory<byte>, bool> Matches);

    private static readonly Dictionary<string, Format> Known = new(StringComparer.Ordinal)
    {
        ["video/mp4"] = new(MediaTypes.Video, IsIsoMedia),
        ["video/quicktime"] = new(MediaTypes.Video, IsIsoMedia),
        ["video/webm"] = new(MediaTypes.Video, h => StartsWith(h, 0x1A, 0x45, 0xDF, 0xA3)),
        ["image/jpeg"] = new(MediaTypes.Image, h => StartsWith(h, 0xFF, 0xD8, 0xFF)),
        ["image/png"] = new(MediaTypes.Image, h => StartsWith(h, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)),
        ["image/webp"] = new(MediaTypes.Image, h => StartsWith(h, 0x52, 0x49, 0x46, 0x46) && h.Length >= 12 && h.Span[8..12].SequenceEqual("WEBP"u8)),
        ["text/vtt"] = new(MediaTypes.Subtitle, IsWebVtt),
    };

    /// <summary>Số byte đầu tệp cần đọc để nhận dạng.</summary>
    public const int HeaderLength = 16;

    public static IReadOnlyCollection<string> ContentTypes => Known.Keys;

    /// <summary>Chuẩn hóa MIME (chữ thường, bỏ tham số <c>; charset=…</c>). null nếu không nhận.</summary>
    public static string? Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var mime = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return Known.ContainsKey(mime) ? mime : null;
    }

    public static string MediaTypeOf(string normalizedContentType) => Known[normalizedContentType].MediaType;

    /// <summary>Đầu tệp có đúng định dạng đã khai báo không.</summary>
    public static bool HeaderMatches(string normalizedContentType, ReadOnlyMemory<byte> header) =>
        Known.TryGetValue(normalizedContentType, out var f) && f.Matches(header);

    private static bool StartsWith(ReadOnlyMemory<byte> h, params byte[] prefix) => h.Length >= prefix.Length && h.Span[..prefix.Length].SequenceEqual(prefix);

    // ISO BMFF (MP4/MOV): byte 4..8 là "ftyp" (hoặc box khác hay gặp ở đầu tệp MOV).
    private static bool IsIsoMedia(ReadOnlyMemory<byte> h) =>
        h.Length >= 8 && (h.Span[4..8].SequenceEqual("ftyp"u8) || h.Span[4..8].SequenceEqual("moov"u8)
            || h.Span[4..8].SequenceEqual("mdat"u8) || h.Span[4..8].SequenceEqual("wide"u8) || h.Span[4..8].SequenceEqual("free"u8));

    private static bool IsWebVtt(ReadOnlyMemory<byte> h)
    {
        var s = h.Span;
        if (s.Length >= 3 && s[0] == 0xEF && s[1] == 0xBB && s[2] == 0xBF) s = s[3..];
        return s.Length >= 6 && s[..6].SequenceEqual("WEBVTT"u8);
    }
}

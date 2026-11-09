using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Tinhte.Modules.Video.Application;

// DTO theo components.schemas của hợp đồng v0.3 (Media). Attribute validation đặt trên tham số record (ADR-0002).

/// <summary>Body của createUploadIntent.</summary>
public sealed record CreateUploadIntentRequest(
    [Required, StringLength(255, MinimumLength = 1)] string FileName,
    [Required, StringLength(150, MinimumLength = 1)] string ContentType,
    [Required, Range(1, long.MaxValue)] long? SizeBytes,
    [Required, RegularExpression("^[a-fA-F0-9]{64}$")] string ChecksumSha256);

/// <summary>UploadIntent: uploadToken là bí mật một lần, chỉ trả trong response này (DB chỉ giữ sha256).</summary>
public sealed record UploadIntent(Guid MediaId, Uri UploadUrl, DateTimeOffset ExpiresAt, string UploadToken);

/// <summary>Body của finalizeUpload.</summary>
public sealed record FinalizeUploadRequest([Required, MinLength(1), MaxLength(200)] string UploadToken);

/// <summary>MediaJobAccepted (202).</summary>
public sealed record MediaJobAccepted(Guid MediaId, string Status);

/// <summary>MediaStatus (+ trường tệp bổ sung ở CHANGELOG mục 12).</summary>
public sealed record MediaStatus(
    Guid MediaId,
    string Status,
    int Generation,
    string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MediaType,
    string Visibility,
    string RightsStatus,
    Guid? LicenseId,
    DateTimeOffset? RightsExpiresAt,
    DateTimeOffset CreatedAt,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>Bộ lọc listAdminMediaLibrary (sau khi đã kiểm tham số query).</summary>
public sealed record MediaFilter(string? MediaType, string? RightsStatus, string? Visibility, string? Q)
{
    public string ScopeKey() => string.Join('|', MediaType, RightsStatus, Visibility, Q);
}

/// <summary>Vị trí cursor của thư viện media: mới nhất trước (created_at, id).</summary>
public sealed record MediaPosition(DateTimeOffset At, Guid Id);

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Tinhte.Modules.Aggregation.Application;

// DTO theo components.schemas của hợp đồng v0.3 (tag CmsAggregation, FR-AGG-001/002). Request có additionalProperties: false
// → JsonUnmappedMemberHandling.Disallow (trường lạ = body sai → 400 BAD_REQUEST). PATCH đọc thủ công (AggregationPatches.cs) để
// phân biệt trường vắng mặt và trả 422 rõ ràng khi gửi usageMode/licenseId.

/// <summary>AggregationCategoryMapping: category của feed → chuyên mục (phải đang bật và thuộc một site của nguồn).</summary>
public sealed record AggregationCategoryMapping(
    [Required, StringLength(200, MinimumLength = 1)] string FeedCategory,
    [Required] Guid? CategoryId);

/// <summary>AggregationFeed. <c>health = DISABLED</c> khi feed bị tắt.</summary>
public sealed record AggregationFeed(
    Guid Id,
    Guid SourceId,
    string Url,
    string Format,
    bool IsEnabled,
    int ScheduleMinutes,
    IReadOnlyList<AggregationCategoryMapping> CategoryMapping,
    string Health,
    DateTimeOffset? LastFetchedAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextFetchAt,
    int ConsecutiveFailures,
    string? LastFailure,
    long RowVersion);

/// <summary>AggregationSourceApprovalRecord (lịch sử chỉ thêm).</summary>
public sealed record AggregationSourceApprovalRecord(
    string UsageMode,
    Guid? LicenseId,
    string TermsEvidenceRef,
    string? Note,
    Guid ApprovedBy,
    DateTimeOffset ApprovedAt);

/// <summary>AggregationSourceSummary: <c>id</c> = id nguồn trong danh mục tt.content_sources (ADR-0007).</summary>
public sealed record AggregationSourceSummary(
    Guid Id,
    string Name,
    string Status,
    string UsageMode,
    Guid? LicenseId,
    int TrustWeight,
    string Language,
    IReadOnlyList<string> SiteKeys,
    int FeedCount,
    int ItemsLast24h,
    string? WorstFeedHealth,
    long RowVersion);

/// <summary>AggregationSource = AggregationSourceSummary + hồ sơ duyệt, feeds, lịch sử duyệt (mới nhất trước).</summary>
public sealed record AggregationSource(
    Guid Id,
    string Name,
    string Status,
    string UsageMode,
    Guid? LicenseId,
    int TrustWeight,
    string Language,
    IReadOnlyList<string> SiteKeys,
    int FeedCount,
    int ItemsLast24h,
    string? WorstFeedHealth,
    long RowVersion,
    string? TermsEvidenceRef,
    Guid ProposedBy,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    IReadOnlyList<AggregationFeed> Feeds,
    IReadOnlyList<AggregationSourceApprovalRecord> Approvals)
{
    public static AggregationSource From(AggregationSourceSummary s, SourceApprovalState a, IReadOnlyList<AggregationFeed> feeds,
        IReadOnlyList<AggregationSourceApprovalRecord> approvals) =>
        new(s.Id, s.Name, s.Status, s.UsageMode, s.LicenseId, s.TrustWeight, s.Language, s.SiteKeys, s.FeedCount, s.ItemsLast24h, s.WorstFeedHealth,
            s.RowVersion, a.TermsEvidenceRef, a.ProposedBy, a.ApprovedBy, a.ApprovedAt, feeds, approvals);
}

/// <summary>Phần hồ sơ duyệt của một nguồn (đọc cùng summary).</summary>
public sealed record SourceApprovalState(string? TermsEvidenceRef, Guid ProposedBy, Guid? ApprovedBy, DateTimeOffset? ApprovedAt);

/// <summary>AggregationSourceCreate. Hồ sơ mới luôn PROPOSED + M0, chưa thu thập gì cho tới khi được duyệt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AggregationSourceCreate(
    [Required] Guid? ContentSourceId,
    [Range(1, 5)] int? TrustWeight,
    [Required, RegularExpression(Domain.AggregationLimits.LanguagePattern)] string? Language,
    [Required] IReadOnlyList<string>? SiteKeys);

/// <summary>AggregationSourceApprovalRequest. M2/M3 bắt buộc licenseId (if/then của schema) → 422 VALIDATION_FAILED.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AggregationSourceApprovalRequest(
    [Required, RegularExpression("^M[0-3]$")] string? UsageMode,
    Guid? LicenseId,
    [Required, StringLength(500, MinimumLength = 1)] string? TermsEvidenceRef,
    [StringLength(2000)] string? Note);

/// <summary>ActionReason (pause/retire). Kiểm ở service: thiếu hoặc &lt; 5 ký tự → 422 REASON_REQUIRED.</summary>
public sealed record AggregationActionReason(string? Reason);

/// <summary>AggregationFeedCreate.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AggregationFeedCreate(
    [Required] string? Url,
    string? Format,
    [Range(5, 1440)] int? ScheduleMinutes,
    [MaxLength(100)] IReadOnlyList<AggregationCategoryMapping>? CategoryMapping);

/// <summary>Bộ lọc listAdminAggregationSources.</summary>
public sealed record AggregationSourceFilter(IReadOnlyList<string> Statuses, string? Q)
{
    /// <summary>Chuỗi ổn định đưa vào scope cursor: đổi bộ lọc thì cursor cũ thành INVALID_CURSOR.</summary>
    public string ScopeKey() => string.Join('|', string.Join(',', Statuses), Q);
}

/// <summary>Vị trí cursor: sắp theo tên nguồn rồi id.</summary>
public sealed record AggregationSourcePosition(string Name, Guid Id);

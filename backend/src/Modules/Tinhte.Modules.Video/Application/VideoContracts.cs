using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tinhte.Modules.Video.Domain;

namespace Tinhte.Modules.Video.Application;

// DTO theo components.schemas của hợp đồng v0.3 (CmsVideo). Tên kiểu có tiền tố Video để không trùng schema của module News
// trong tài liệu OpenAPI sinh từ code; JSON giống hệt schema Sponsorship của hợp đồng.

/// <summary>Sponsorship (ghi): approvedBy/approvedAt là readOnly — do workflow duyệt tài trợ đặt, không nhận từ client.</summary>
public sealed record VideoSponsorshipInput(
    [Required, StringLength(180, MinimumLength = 1)] string SponsorName,
    [Required, StringLength(120, MinimumLength = 1)] string SponsorReference,
    [Required, StringLength(300, MinimumLength = 1)] string EvidenceRef,
    [Required] DateTimeOffset? ValidFrom,
    [Required] DateTimeOffset? ValidTo);

/// <summary>Sponsorship (đọc).</summary>
public sealed record VideoSponsorship(
    string SponsorName,
    string SponsorReference,
    string EvidenceRef,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ApprovedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ApprovedAt);

/// <summary>CreateVideoRequest. <c>rightsSource</c> thuộc taxonomy nguồn/giấy phép (WBS-023): chưa nhận.</summary>
public sealed record CreateVideoRequest(
    [Required, StringLength(300, MinimumLength = 1)] string Title,
    string? Summary,
    Guid? MediaId,
    string? RightsSource,
    [RegularExpression(VideoContentKinds.Pattern)] string? ContentKind,
    VideoSponsorshipInput? Sponsorship);

/// <summary>
/// AdminVideo (+ summary, CHANGELOG mục 12; + revisionStatus, hồ sơ duyệt, lần gỡ gần nhất — mục 13; + bodyBlocks — mục 22).
/// <c>bodyBlocks</c> = thân của cùng revision với <c>summary</c> (mảng rỗng khi chưa có); danh sách (getAdminVideos) không kèm, giống bài viết.
/// <c>mediaId</c> = media của working revision (bản đang soạn/duyệt); media đang phát công khai là media của public revision.
/// </summary>
public sealed record AdminVideo(
    Guid Id,
    string Title,
    string Status,
    string ContentKind,
    VideoSponsorship? Sponsorship,
    Guid? WorkingRevisionId,
    Guid? PublicRevisionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OwnerId,
    Guid? AssigneeId,
    string RightsStatus,
    DateTimeOffset? RightsExpiresAt,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset UpdatedAt,
    long RowVersion,
    Guid? MediaId,
    string? Summary,
    string? RevisionStatus,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote,
    DateTimeOffset? UnpublishedAt,
    string? UnpublishReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? BodyBlocks);

/// <summary>ApproveRequest: duyệt đúng revision đang chờ (tránh duyệt nhầm bản đã bị thay trong lúc xem).</summary>
public sealed record VideoApproveRequest([Required] Guid? RevisionId, [StringLength(1000)] string? Note);

/// <summary>PublishRequest.</summary>
public sealed record VideoPublishRequest([Required] Guid? RevisionId);

/// <summary>ActionReason (request-changes/reject/unpublish). Kiểm ở service: thiếu hoặc &lt; 5 ký tự → 422 REASON_REQUIRED.</summary>
public sealed record VideoActionReason(string? Reason);

/// <summary>Bộ lọc getAdminVideos (sau khi đã kiểm tham số query).</summary>
public sealed record AdminVideoFilter(
    IReadOnlyList<string> Statuses,
    Guid? OwnerId,
    Guid? AssigneeId,
    DateTimeOffset? UpdatedFrom,
    DateTimeOffset? UpdatedTo,
    string? Q,
    string Sort)
{
    public const string DefaultSort = "updatedAt_desc";
    public const string SortPattern = "^(updatedAt_desc|updatedAt_asc|createdAt_desc|title_asc)$";

    /// <summary>Chuỗi ổn định đưa vào scope cursor: đổi bộ lọc/sort thì cursor cũ thành INVALID_CURSOR.</summary>
    public string ScopeKey() => string.Join('|', string.Join(',', Statuses), OwnerId, AssigneeId,
        UpdatedFrom?.UtcTicks, UpdatedTo?.UtcTicks, Q, Sort);
}

/// <summary>Vị trí cursor của danh sách admin: khóa sắp xếp (thời gian hoặc tiêu đề) + id.</summary>
public sealed record AdminVideoPosition(DateTimeOffset? At, string? Title, Guid Id);

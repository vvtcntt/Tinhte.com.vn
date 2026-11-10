using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tinhte.Modules.News.Domain;

namespace Tinhte.Modules.News.Application;

// DTO theo components.schemas của hợp đồng v0.3. Attribute validation đặt trên tham số record (ADR-0002).

/// <summary>Sponsorship (ghi): approvedBy/approvedAt là readOnly — do workflow duyệt tài trợ đặt (WBS-132), không nhận từ client.</summary>
public sealed record SponsorshipInput(
    [Required, StringLength(180, MinimumLength = 1)] string SponsorName,
    [Required, StringLength(120, MinimumLength = 1)] string SponsorReference,
    [Required, StringLength(300, MinimumLength = 1)] string EvidenceRef,
    [Required] DateTimeOffset? ValidFrom,
    [Required] DateTimeOffset? ValidTo);

/// <summary>Sponsorship (đọc).</summary>
public sealed record Sponsorship(
    string SponsorName,
    string SponsorReference,
    string EvidenceRef,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ApprovedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ApprovedAt);

/// <summary>CreateArticleRequest. Slug sinh từ tiêu đề (đổi bằng PATCH).</summary>
public sealed record CreateArticleRequest(
    [Required, StringLength(300, MinimumLength = 1)] string Title,
    string? Summary,
    [Required] Guid? CategoryId,
    JsonElement? InitialBodyBlocks,
    [RegularExpression(ContentKinds.Pattern)] string? ContentKind,
    SponsorshipInput? Sponsorship);

/// <summary>ApproveRequest: duyệt đúng revision đang chờ (tránh duyệt nhầm bản đã bị thay trong lúc xem).</summary>
public sealed record ApproveRequest([Required] Guid? RevisionId, [StringLength(1000)] string? Note);

/// <summary>ActionReason (request-changes/reject). Kiểm ở service: thiếu hoặc &lt; 5 ký tự → 422 REASON_REQUIRED.</summary>
public sealed record ActionReason(string? Reason);

/// <summary>PublishRequest.</summary>
public sealed record PublishRequest([Required] Guid? RevisionId);

/// <summary>AdminArticle.</summary>
public sealed record AdminArticle(
    Guid Id,
    string Title,
    string Status,
    string ContentKind,
    Sponsorship? Sponsorship,
    Guid? WorkingRevisionId,
    Guid? PublicRevisionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OwnerId,
    Guid? AssigneeId,
    string RightsStatus,
    DateTimeOffset? RightsExpiresAt,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset UpdatedAt,
    long RowVersion,
    string Slug);

/// <summary>ArticleRevision / RevisionDetail (diff chỉ có khi gọi kèm compareTo). Danh sách không kèm bodyBlocks.</summary>
public sealed record ArticleRevision(
    Guid RevisionId,
    Guid ArticleId,
    int Number,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AuthorId,
    string Status,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? BodyBlocks,
    string Title,
    string ContentKind,
    Sponsorship? Sponsorship,
    string? ChangeReason,
    string? Summary,
    Guid? CategoryId,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    Guid? PublishedBy,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RevisionDiff? Diff { get; init; }
}

public sealed record ArticleRevisionList(IReadOnlyList<ArticleRevision> Items);

public sealed record RevisionDiff(Guid CompareTo, IReadOnlyList<RevisionChange> Changes);

/// <summary>Một thay đổi: path dạng JSON Pointer (<c>/title</c>, <c>/bodyBlocks/3</c>); null = không có giá trị.</summary>
public sealed record RevisionChange(string Path, JsonElement? Before, JsonElement? After);

/// <summary>Article (công khai): chỉ nội dung của public revision.</summary>
public sealed record PublicArticle(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    DateTimeOffset PublishedAt,
    string? ThumbnailUrl,
    string ContentKind,
    string? SponsorLabel,
    Guid RevisionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AuthorDisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CategorySlug,
    JsonElement BodyBlocks,
    IReadOnlyList<Guid> RelatedVideoIds)
{
    /// <summary>
    /// Article kế thừa ArticleSummary.category (DRAFT v0.3 mục 17): chuyên mục theo <c>tt.articles.category_id</c>, chỉ khi đang bật và cùng
    /// site với bài (<see cref="Infrastructure.PublicArticleCategorySql"/>); null = không hiện. <c>categorySlug</c> giữ để client cũ không vỡ.
    /// </summary>
    public PublicCategoryRef? Category { get; init; }
}

/// <summary>Bộ lọc của getAdminArticles (sau khi đã kiểm tham số query).</summary>
public sealed record AdminArticleFilter(
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
public sealed record AdminArticlePosition(DateTimeOffset? At, string? Title, Guid Id);

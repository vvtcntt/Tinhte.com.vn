using System.ComponentModel.DataAnnotations;

namespace Tinhte.Modules.Ads.Application;

/// <summary>Mẫu của hợp đồng v0.3 dùng chung cho vị trí/đặt chỗ.</summary>
public static class AdPatterns
{
    /// <summary>Site Registry key (AdPlacement.siteKey, SiteKeyFilter).</summary>
    public const string SiteKey = "^[a-z][a-z0-9-]{0,30}$";

    /// <summary>AdPlacement.code — unique trong site.</summary>
    public const string Code = "^[a-z0-9_-]{2,80}$";

    public const int MaxSizes = 12;
    public const int MaxCreatives = 3;
}

/// <summary>
/// Schema AdStatus: vòng đời chung của vị trí và đặt chỗ (putAdminAdBooking, putAdminAdPlacementsPlacementid; DRAFT v0.3 mục 16/19).
/// DRAFT → APPROVED, APPROVED → ACTIVE, ACTIVE ↔ PAUSED, APPROVED/PAUSED → DRAFT, mọi trạng thái → EXPIRED (cuối); giữ nguyên luôn được.
/// </summary>
public static class AdStatuses
{
    public const string Draft = "DRAFT";
    public const string Approved = "APPROVED";
    public const string Active = "ACTIVE";
    public const string Paused = "PAUSED";
    public const string Expired = "EXPIRED";

    public const string Pattern = "^(DRAFT|APPROVED|ACTIVE|PAUSED|EXPIRED)$";

    public static readonly IReadOnlyList<string> All = [Draft, Approved, Active, Paused, Expired];

    public static bool CanTransition(string from, string to) => from == to || (from, to) switch
    {
        (Draft, Approved) or (Approved, Active) or (Active, Paused) or (Paused, Active) or (Approved, Draft) or (Paused, Draft) => true,
        (_, Expired) => true,
        _ => false,
    };

    /// <summary>
    /// Vào APPROVED hoặc ACTIVE (kể cả PAUSED → ACTIVE) cần quyền duyệt <c>ads.approve</c> (MFA); PAUSED/DRAFT/EXPIRED chỉ cần
    /// <c>ads.manage</c> — dừng quảng cáo không phải chờ người duyệt. Giữ nguyên trạng thái không cần quyền duyệt.
    /// </summary>
    public static bool NeedsApproval(string from, string to) => from != to && to is Approved or Active;
}

/// <summary>Schema AdSize: kích thước banner được phép của vị trí ở một breakpoint.</summary>
public sealed record AdSize(
    [Required, RegularExpression(AdBreakpoints.Pattern)] string? Breakpoint,
    [Range(1, 4000)] int Width,
    [Range(1, 4000)] int Height);

/// <summary>Schema AdCreative. <c>creativeUrl</c> chỉ đọc (client gửi lên bị bỏ qua), null khi media chưa phục vụ được.</summary>
public sealed record AdCreative(
    [Required, RegularExpression(AdBreakpoints.Pattern)] string? Breakpoint,
    [Range(1, 4000)] int Width,
    [Range(1, 4000)] int Height,
    [Required] Guid? MediaAssetId,
    [Required, StringLength(250, MinimumLength = 1)] string? AltText,
    string? CreativeUrl = null);

/// <summary>Schema AdPlacement: vị trí trên trang của một site public-web.</summary>
public sealed record AdPlacement(
    Guid Id,
    string SiteKey,
    string Code,
    string Label,
    string Status,
    IReadOnlyList<AdSize> Sizes,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    long RowVersion);

/// <summary>Schema AdPlacementList.</summary>
public sealed record AdPlacementList(IReadOnlyList<AdPlacement> Items);

/// <summary>Schema AdPlacementCreate — tạo ở trạng thái DRAFT.</summary>
public sealed record AdPlacementCreate(
    [Required, RegularExpression(AdPatterns.SiteKey)] string? SiteKey,
    [Required, RegularExpression(AdPatterns.Code)] string? Code,
    [Required, StringLength(150, MinimumLength = 1)] string? Label,
    [Required, MinLength(1), MaxLength(AdPatterns.MaxSizes)] IReadOnlyList<AdSize>? Sizes);

/// <summary>Schema AdPlacementUpdate — thay toàn bộ (siteKey không đổi được).</summary>
public sealed record AdPlacementUpdate(
    [Required, RegularExpression(AdPatterns.Code)] string? Code,
    [Required, StringLength(150, MinimumLength = 1)] string? Label,
    [Required, RegularExpression(AdStatuses.Pattern)] string? Status,
    [Required, MaxLength(AdPatterns.MaxSizes)] IReadOnlyList<AdSize>? Sizes);

/// <summary>Schema AdBooking: banner theo breakpoint + link + lịch + ưu tiên + duyệt.</summary>
public sealed record AdBooking(
    Guid Id,
    Guid PlacementId,
    string Status,
    int Priority,
    string? AdvertiserName,
    string? TargetUrl,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<AdCreative> Creatives,
    Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    long RowVersion);

/// <summary>Schema AdBookingList (mới nhất trước, mọi trạng thái).</summary>
public sealed record AdBookingList(IReadOnlyList<AdBooking> Items);

/// <summary>Schema AdBookingCreate — tạo ở trạng thái DRAFT. targetUrl không https → UNSAFE_URL (service), không phải VALIDATION_FAILED.</summary>
public sealed record AdBookingCreate(
    [Range(0, 100)] int? Priority,
    [StringLength(180)] string? AdvertiserName,
    [StringLength(2000)] string? TargetUrl,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    [Required, MinLength(1), MaxLength(AdPatterns.MaxCreatives)] IReadOnlyList<AdCreative>? Creatives);

/// <summary>Schema AdBookingUpdate — thay toàn bộ.</summary>
public sealed record AdBookingUpdate(
    [Required, RegularExpression(AdStatuses.Pattern)] string? Status,
    [Required, Range(0, 100)] int? Priority,
    [StringLength(180)] string? AdvertiserName,
    [StringLength(2000)] string? TargetUrl,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    [Required, MinLength(1), MaxLength(AdPatterns.MaxCreatives)] IReadOnlyList<AdCreative>? Creatives);

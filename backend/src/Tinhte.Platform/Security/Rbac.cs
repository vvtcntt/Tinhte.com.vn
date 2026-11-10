using System.Security.Claims;
using System.Text.Json;
using Tinhte.Platform.Errors;

namespace Tinhte.Platform.Security;

/// <summary>Claim do handler phiên (staff/member) đặt vào principal.</summary>
public static class TinhteClaims
{
    /// <summary>Id user (uuid) — cũng là actor cho idempotency và tt.actor_id.</summary>
    public const string Subject = "sub";
    public const string Role = "tt:role";
    public const string MfaSatisfied = "tt:mfa";
    public const string SessionKind = "tt:session";
    public const string SessionId = "tt:sid";
    /// <summary>sha256 hex của token CSRF gắn với phiên (double-submit, ADR-0001 D4).</summary>
    public const string CsrfHash = "tt:csrf";

    public const string StaffSession = "staff";
}

/// <summary>
/// Vai trò staff — ĐỀ XUẤT theo FR-ID-002 ("author/editor/approver/moderator/admin"). Danh sách phải khớp CHECK
/// <c>ck_user_roles_role</c> của migration mới nhất định nghĩa nó (0313; 0351 thêm AD_OPS) — test giữ khớp. BA + TL + PO duyệt trước sign-off.
/// </summary>
public static class StaffRoles
{
    public const string Author = "AUTHOR";
    public const string Editor = "EDITOR";
    public const string Approver = "APPROVER";
    public const string Moderator = "MODERATOR";
    public const string Admin = "ADMIN";

    /// <summary>Vận hành quảng cáo (P0-ADS) — ĐỀ XUẤT 10/11/2026, chờ BA/PO duyệt (config/rbac/rbac.json, migration 0351).</summary>
    public const string AdOps = "AD_OPS";

    public static readonly IReadOnlyList<string> All = [Author, Editor, Approver, Moderator, Admin, AdOps];
}

/// <summary>Quyền theo hành động của API admin. Controller/service kiểm quyền, không kiểm tên vai trò.</summary>
public static class Permissions
{
    public const string ContentRead = "content.read";
    public const string ContentWriteOwn = "content.write_own";
    public const string ContentWriteAny = "content.write_any";
    public const string ContentReview = "content.review";
    public const string ContentApprove = "content.approve";
    public const string ContentPublish = "content.publish";
    public const string ContentTakedown = "content.takedown";
    public const string MediaUpload = "media.upload";
    public const string TaxonomyManage = "taxonomy.manage";
    public const string ModerationAct = "moderation.act";
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string AuditRead = "audit.read";

    /// <summary>Tạo/sửa vị trí và đặt chỗ quảng cáo, tạm dừng/đưa về nháp/kết thúc (P0-ADS) — ĐỀ XUẤT, chờ BA/PO duyệt.</summary>
    public const string AdsManage = "ads.manage";

    /// <summary>Duyệt và mở (APPROVED/ACTIVE) vị trí, đặt chỗ quảng cáo; cần MFA — ĐỀ XUẤT, chờ BA/PO duyệt.</summary>
    public const string AdsApprove = "ads.approve";
}

/// <summary>
/// Ma trận vai trò → quyền (DRAFT, chờ BA/TL/PO duyệt — "role-policy matrix" của 07_API_IMPLEMENTATION_GUIDE).
/// Nguồn sự thật: <c>config/rbac/rbac.json</c>, nhúng vào assembly lúc build và dùng chung với CMS (ẩn/hiện giao diện).
/// Test RbacMatrixTests giữ khớp hằng số <see cref="Permissions"/>/<see cref="StaffRoles"/> với file.
/// Quyền "cao" chỉ hiệu lực khi phiên đã qua MFA (FR-ID-006): thiếu MFA → 403 FORBIDDEN dù có vai trò.
/// Tách nhiệm vụ (SoD: không tự duyệt bài của mình) kiểm ở domain service, không ở ma trận.
/// </summary>
public static class PermissionMatrix
{
    public const string ResourceName = "Tinhte.Platform.rbac.json";

    public static RbacDocument Document { get; } = Load();

    private static readonly Dictionary<string, string[]> Grants =
        Document.Roles.ToDictionary(r => r.Key, r => r.Value.Permissions.ToArray(), StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> MfaRequired =
        Document.Permissions.Where(p => p.Value.Mfa).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> AllPermissions { get; } = Document.Permissions.Keys.ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> PermissionsOf(string role) => Grants.TryGetValue(role, out var p) ? p : [];

    /// <summary>Vai trò "đặc quyền": có ít nhất một quyền cần MFA.</summary>
    public static bool IsPrivileged(string role) => PermissionsOf(role).Any(MfaRequired.Contains);

    public static AccessDecision Evaluate(IEnumerable<string> roles, bool mfaSatisfied, string permission)
    {
        if (!AllPermissions.Contains(permission)) throw new ArgumentException($"Unknown permission '{permission}'", nameof(permission));
        if (!roles.Any(r => PermissionsOf(r).Contains(permission))) return AccessDecision.MissingPermission;
        return MfaRequired.Contains(permission) && !mfaSatisfied ? AccessDecision.MfaRequired : AccessDecision.Allowed;
    }

    private static RbacDocument Load()
    {
        using var stream = typeof(PermissionMatrix).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} (config/rbac/rbac.json) is missing.");
        var document = JsonSerializer.Deserialize<RbacDocument>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("config/rbac/rbac.json is empty.");
        var unknown = document.Roles.SelectMany(r => r.Value.Permissions.Where(p => !document.Permissions.ContainsKey(p)).Select(p => $"{r.Key}:{p}")).ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"config/rbac/rbac.json grants unknown permissions: {string.Join(", ", unknown)}");
        return document;
    }
}

public sealed record RbacDocument(int Version, IReadOnlyDictionary<string, RbacPermission> Permissions, IReadOnlyDictionary<string, RbacRole> Roles);

public sealed record RbacPermission(string Label, bool Mfa);

public sealed record RbacRole(string Label, IReadOnlyList<string> Permissions);

public enum AccessDecision
{
    Allowed,
    MissingPermission,
    MfaRequired,
}

public static class StaffPrincipalExtensions
{
    public static bool IsStaff(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && user.FindFirst(TinhteClaims.SessionKind)?.Value == TinhteClaims.StaffSession;

    public static Guid? GetUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(TinhteClaims.Subject)?.Value, out var id) ? id : null;

    public static IReadOnlyList<string> GetRoles(this ClaimsPrincipal user) =>
        user.FindAll(TinhteClaims.Role).Select(c => c.Value).ToList();

    public static bool IsMfaSatisfied(this ClaimsPrincipal user) => user.FindFirst(TinhteClaims.MfaSatisfied)?.Value == "true";

    public static AccessDecision Evaluate(this ClaimsPrincipal user, string permission) =>
        user.IsStaff() ? PermissionMatrix.Evaluate(user.GetRoles(), user.IsMfaSatisfied(), permission) : AccessDecision.MissingPermission;

    /// <summary>
    /// Kiểm quyền ở domain/service ("RBAC enforced in the domain layer"): chưa đăng nhập → 401, thiếu quyền/MFA → 403.
    /// Trả về id user để làm actor.
    /// </summary>
    public static Guid RequirePermission(this ClaimsPrincipal user, string permission)
    {
        if (!user.IsStaff() || user.GetUserId() is not { } userId)
        {
            throw new DomainProblemException(ErrorCodes.Unauthenticated, "Authentication required", "Sign in to the CMS.");
        }

        return user.Evaluate(permission) switch
        {
            AccessDecision.Allowed => userId,
            AccessDecision.MfaRequired => throw new DomainProblemException(ErrorCodes.Forbidden, "MFA required",
                $"'{permission}' requires a session with multi-factor authentication. Sign in again with MFA."),
            _ => throw new DomainProblemException(ErrorCodes.Forbidden, "Forbidden", $"Your roles do not grant '{permission}'."),
        };
    }
}

using Tinhte.Modules.Identity.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Identity.Application;

/// <summary>
/// Quản trị tài khoản (WBS-115, FR-ID-004). Mọi thay đổi chạy trong một transaction có khóa dòng user, đặt
/// <c>tt.actor_id</c> + <c>tt.reason</c> để trigger ghi lịch sử (0313 vai trò, 0314 trạng thái — FR-ID-007).
/// Quy tắc nghiệp vụ:
/// <list type="bullet">
///   <item>Không tự đình chỉ mình, không tự bỏ vai trò ADMIN của mình (chống tự khóa) → 409 STATE_CONFLICT.</item>
///   <item>Đình chỉ: chỉ từ ACTIVE; thu hồi mọi phiên staff ngay. Kích hoạt lại: chỉ từ SUSPENDED.</item>
///   <item>Đổi vai trò: If-Match theo row_version của user; có thay đổi thì tăng row_version (ETag mới).</item>
/// </list>
/// </summary>
public sealed class AdminUserService(ITinhteDatabase database)
{
    public const int ReasonMin = 5;
    public const int ReasonMax = 2000;

    public static readonly IReadOnlyList<string> Statuses = ["ACTIVE", "SUSPENDED"];

    /// <summary>ActionReason của hợp đồng: thiếu/trống → 422 REASON_REQUIRED; ngoài 5–2000 ký tự → 422 VALIDATION_FAILED.</summary>
    public static string RequireReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainProblemException(ErrorCodes.ReasonRequired, "Reason required", "Give a reason for this action; it is kept in the audit log.");
        }

        if (trimmed.Length is < ReasonMin or > ReasonMax)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["reason"] = [$"Reason must be {ReasonMin}–{ReasonMax} characters."] });
        }

        return trimmed;
    }

    public Task<AdminUserRow> SuspendAsync(Guid actorId, Guid userId, string reason, CancellationToken cancellationToken) =>
        ChangeStatusAsync(actorId, userId, reason, from: "ACTIVE", to: "SUSPENDED", cancellationToken);

    public Task<AdminUserRow> ReactivateAsync(Guid actorId, Guid userId, string reason, CancellationToken cancellationToken) =>
        ChangeStatusAsync(actorId, userId, reason, from: "SUSPENDED", to: "ACTIVE", cancellationToken);

    public Task<AdminUserRow> SetRolesAsync(Guid actorId, Guid userId, IReadOnlyList<string> roles, string reason, long expectedRowVersion, CancellationToken cancellationToken)
    {
        var unknown = roles.Where(r => !StaffRoles.All.Contains(r)).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "Unknown role.",
                new Dictionary<string, string[]> { ["roles"] = [$"Unknown role(s): {string.Join(", ", unknown)}. Allowed: {string.Join(", ", StaffRoles.All)}."] });
        }

        var desired = roles.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (actorId == userId && !desired.Contains(StaffRoles.Admin))
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "Cannot remove own admin role",
                "You cannot remove the ADMIN role from your own account. Ask another administrator.");
        }

        return database.InTransactionAsync(actorId, async (c, tx) =>
        {
            var locked = await AdminUserStore.LockAsync(c, tx, userId, cancellationToken) ?? throw NotFound();
            ETags.EnsureMatches(locked.RowVersion, expectedRowVersion);
            await AdminUserStore.SetReasonAsync(c, tx, reason, cancellationToken);

            var current = (await AdminUserStore.GetAsync(c, tx, userId, cancellationToken))!.Roles.ToHashSet(StringComparer.Ordinal);
            var removed = current.Except(desired).ToList();
            var added = desired.Except(current).ToList();
            foreach (var role in removed)
            {
                await AdminUserStore.ExecuteAsync(c, tx, "DELETE FROM tt.user_roles WHERE user_id = @u AND role = @r", cancellationToken,
                    ("u", userId), ("r", role));
            }

            foreach (var role in added)
            {
                await AdminUserStore.ExecuteAsync(c, tx,
                    "INSERT INTO tt.user_roles (user_id, role, granted_by, reason) VALUES (@u, @r, @actor, @reason)", cancellationToken,
                    ("u", userId), ("r", role), ("actor", actorId), ("reason", reason));
            }

            if (removed.Count + added.Count > 0)
            {
                // Vai trò là một phần của aggregate AdminUser → tăng row_version (trigger 0309) để ETag đổi.
                await AdminUserStore.ExecuteAsync(c, tx, "UPDATE tt.users SET row_version = row_version WHERE id = @u", cancellationToken, ("u", userId));
            }

            return (await AdminUserStore.GetAsync(c, tx, userId, cancellationToken))!;
        }, cancellationToken);
    }

    private Task<AdminUserRow> ChangeStatusAsync(Guid actorId, Guid userId, string reason, string from, string to, CancellationToken cancellationToken)
    {
        if (actorId == userId)
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "Cannot change own account status",
                "You cannot suspend or reactivate your own account.");
        }

        return database.InTransactionAsync(actorId, async (c, tx) =>
        {
            var locked = await AdminUserStore.LockAsync(c, tx, userId, cancellationToken) ?? throw NotFound();
            if (locked.Status != from)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict", $"Account is {locked.Status}; expected {from}.");
            }

            await AdminUserStore.SetReasonAsync(c, tx, reason, cancellationToken);
            await AdminUserStore.ExecuteAsync(c, tx, "UPDATE tt.users SET status = @s WHERE id = @u", cancellationToken, ("s", to), ("u", userId));
            if (to == "SUSPENDED")
            {
                // FR-ID-004: đình chỉ thu hồi phiên ngay (phiên cũng bị chặn vì status ≠ ACTIVE, đây là dấu vết rõ ràng).
                await AdminUserStore.ExecuteAsync(c, tx,
                    "UPDATE tt.staff_sessions SET revoked_at = now(), revoked_reason = 'SUSPENDED' WHERE user_id = @u AND revoked_at IS NULL",
                    cancellationToken, ("u", userId));
            }

            return (await AdminUserStore.GetAsync(c, tx, userId, cancellationToken))!;
        }, cancellationToken);
    }

    public static DomainProblemException NotFound() => new(ErrorCodes.NotFound, "Not found", "User not found.");
}

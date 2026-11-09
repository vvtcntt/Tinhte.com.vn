using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Identity.Application;
using Tinhte.Modules.Identity.Infrastructure;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Identity.Controllers;

/// <summary>
/// Quản trị tài khoản (tag CmsUsers, WBS-115, FR-ID-002/004). Xem: <c>users.read</c>; đình chỉ / kích hoạt / đổi vai trò:
/// <c>users.manage</c> (cần MFA). Chỉ phục vụ trên site cms (bề mặt admin).
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/users")]
public sealed class AdminUsersController(AdminUserStore store, AdminUserService service, ICursorCodec cursors) : TinhteControllerBase
{
    /// <summary>Schema AdminUser (verificationStatus thuộc xác minh thành viên — WBS-059, chưa có nên bỏ trống).</summary>
    public sealed record AdminUserResponse(Guid Id, string DisplayName, string Status, IReadOnlyList<string> Roles, long RowVersion);

    /// <summary>Schema ActionReason.</summary>
    public sealed record ActionReasonRequest(string? Reason);

    /// <summary>Schema RoleAssignment.</summary>
    public sealed record RoleAssignmentRequest(IReadOnlyList<string>? Roles, string? Reason);

    [HttpGet(Name = "getAdminUsers")]
    [RequirePermission(Permissions.UsersRead)]
    public async Task<ActionResult<CursorPage<AdminUserResponse>>> List(
        [FromQuery] PageRequest page,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "role"), StringLength(80)] string? role,
        [FromQuery(Name = "q"), StringLength(200)] string? q,
        CancellationToken cancellationToken)
    {
        if (status is not null && !AdminUserService.Statuses.Contains(status))
        {
            return Problem(ErrorCodes.BadRequest, "Invalid status", $"status must be one of: {string.Join(", ", AdminUserService.Statuses)}.");
        }

        if (role is not null && !StaffRoles.All.Contains(role))
        {
            return Problem(ErrorCodes.BadRequest, "Invalid role", $"role must be one of: {string.Join(", ", StaffRoles.All)}.");
        }

        var filter = new AdminUserFilter(status, role, string.IsNullOrWhiteSpace(q) ? null : q.Trim());
        var scope = CursorScope("admin-users", $"{filter.Status}|{filter.Role}|{filter.Query}");
        var after = page.Cursor is null ? null : cursors.Decode<AdminUserPosition>(scope, page.Cursor);
        var rows = await store.ListAsync(filter, after, page.Limit + 1, cancellationToken);
        var result = rows.ToPage(page.Limit, cursors, scope, r => new AdminUserPosition(r.CreatedAt, r.Id));
        return Ok(new CursorPage<AdminUserResponse>(result.Items.Select(ToResponse).ToList(), result.NextCursor));
    }

    [HttpGet("{userId:guid}", Name = "getAdminUsersUserid")]
    [RequirePermission(Permissions.UsersRead)]
    public async Task<ActionResult<AdminUserResponse>> Get(Guid userId, CancellationToken cancellationToken)
    {
        var user = await store.GetAsync(userId, cancellationToken) ?? throw AdminUserService.NotFound();
        SetETag(user.RowVersion);
        return Ok(ToResponse(user));
    }

    [HttpPost("{userId:guid}/suspend", Name = "postAdminUsersUseridSuspend")]
    [RequirePermission(Permissions.UsersManage)]
    [Idempotent]
    public async Task<ActionResult<AdminUserResponse>> Suspend(Guid userId, ActionReasonRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.UsersManage);
        var user = await service.SuspendAsync(actor, userId, AdminUserService.RequireReason(request.Reason), cancellationToken);
        SetETag(user.RowVersion);
        return Ok(ToResponse(user));
    }

    [HttpPost("{userId:guid}/reactivate", Name = "postAdminUsersUseridReactivate")]
    [RequirePermission(Permissions.UsersManage)]
    [Idempotent]
    public async Task<ActionResult<AdminUserResponse>> Reactivate(Guid userId, ActionReasonRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.UsersManage);
        var user = await service.ReactivateAsync(actor, userId, AdminUserService.RequireReason(request.Reason), cancellationToken);
        SetETag(user.RowVersion);
        return Ok(ToResponse(user));
    }

    [HttpPut("{userId:guid}/roles", Name = "putAdminUsersUseridRoles")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<ActionResult<AdminUserResponse>> SetRoles(Guid userId, RoleAssignmentRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.UsersManage);
        _ = await store.GetAsync(userId, cancellationToken) ?? throw AdminUserService.NotFound(); // 404 trước 428/412
        var expected = RequireIfMatch();
        if (request.Roles is null)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "roles is required.",
                new Dictionary<string, string[]> { ["roles"] = ["roles is required (use [] to remove every role)."] });
        }

        var user = await service.SetRolesAsync(actor, userId, request.Roles, AdminUserService.RequireReason(request.Reason), expected, cancellationToken);
        SetETag(user.RowVersion);
        return Ok(ToResponse(user));
    }

    private static AdminUserResponse ToResponse(AdminUserRow row) => new(row.Id, row.DisplayName, row.Status, row.Roles, row.RowVersion);
}

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.News.Application;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.News.Controllers;

/// <summary>
/// Danh mục nguồn nội dung (tag CmsTaxonomy, ADR-0007 §1, FR-NEWS-006). Xem: content.read; tạo/sửa: taxonomy.manage (theo ADR).
/// Không xóa — chỉ tắt (isEnabled = false).
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/sources")]
public sealed class AdminSourcesController(CatalogService catalog, ICursorCodec cursors) : TinhteControllerBase
{
    [HttpGet(Name = "listAdminContentSources")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<CursorPage<ContentSource>>> List([FromQuery] PageRequest page, [FromQuery(Name = "q"), StringLength(200)] string? q,
        [FromQuery(Name = "isEnabled")] bool? isEnabled, CancellationToken cancellationToken)
    {
        var filter = new CatalogFilter(string.IsNullOrWhiteSpace(q) ? null : q.Trim(), isEnabled);
        var scope = CursorScope("admin-content-sources", filter.ScopeKey());
        var after = page.Cursor is { } cursor ? cursors.Decode<CatalogPosition>(scope, cursor) : null;
        var rows = await catalog.ListSourcesAsync(filter, after, page.Limit + 1, cancellationToken);
        return Ok(rows.ToPage(page.Limit, cursors, scope, x => new CatalogPosition(x.Name, x.Id)));
    }

    [HttpPost(Name = "createAdminContentSource")]
    [RequirePermission(Permissions.TaxonomyManage)]
    [Idempotent]
    public async Task<ActionResult<ContentSource>> Create([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.TaxonomyManage);
        var created = await catalog.CreateSourceAsync(actor, body, cancellationToken);
        SetETag(created.RowVersion);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("{sourceId}", Name = "patchAdminContentSource")]
    [RequirePermission(Permissions.TaxonomyManage)]
    public async Task<ActionResult<ContentSource>> Patch(Guid sourceId, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.TaxonomyManage);
        await catalog.EnsureSourceExistsAsync(sourceId, cancellationToken);
        var expected = RequireIfMatch();
        var saved = await catalog.PatchSourceAsync(actor, sourceId, expected, body, cancellationToken);
        SetETag(saved.RowVersion);
        return Ok(saved);
    }
}

/// <summary>
/// Danh mục giấy phép (tag CmsTaxonomy, ADR-0007 §1). Xem: content.read (người duyệt nguồn M2/M3 và biên tập viên cần chọn giấy phép);
/// tạo/sửa: licenses.manage (MFA) — quyền mới, cấp tạm cho APPROVER, chờ Legal chốt Q2. Mã giấy phép không đổi; không xóa, chỉ tắt.
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/licenses")]
public sealed class AdminLicensesController(CatalogService catalog, ICursorCodec cursors) : TinhteControllerBase
{
    [HttpGet(Name = "listAdminLicenses")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<CursorPage<License>>> List([FromQuery] PageRequest page, [FromQuery(Name = "q"), StringLength(200)] string? q,
        [FromQuery(Name = "isEnabled")] bool? isEnabled, CancellationToken cancellationToken)
    {
        var filter = new CatalogFilter(string.IsNullOrWhiteSpace(q) ? null : q.Trim(), isEnabled);
        var scope = CursorScope("admin-licenses", filter.ScopeKey());
        var after = page.Cursor is { } cursor ? cursors.Decode<CatalogPosition>(scope, cursor) : null;
        var rows = await catalog.ListLicensesAsync(filter, after, page.Limit + 1, cancellationToken);
        return Ok(rows.ToPage(page.Limit, cursors, scope, x => new CatalogPosition(x.Name, x.Id)));
    }

    [HttpPost(Name = "createAdminLicense")]
    [RequirePermission(Permissions.LicensesManage)]
    [Idempotent]
    public async Task<ActionResult<License>> Create([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.LicensesManage);
        var created = await catalog.CreateLicenseAsync(actor, body, cancellationToken);
        SetETag(created.RowVersion);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("{licenseId}", Name = "patchAdminLicense")]
    [RequirePermission(Permissions.LicensesManage)]
    public async Task<ActionResult<License>> Patch(Guid licenseId, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.LicensesManage);
        await catalog.EnsureLicenseExistsAsync(licenseId, cancellationToken);
        var expected = RequireIfMatch();
        var saved = await catalog.PatchLicenseAsync(actor, licenseId, expected, body, cancellationToken);
        SetETag(saved.RowVersion);
        return Ok(saved);
    }
}

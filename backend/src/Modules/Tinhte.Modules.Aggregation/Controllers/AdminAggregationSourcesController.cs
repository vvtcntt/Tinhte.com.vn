using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Aggregation.Controllers;

/// <summary>
/// Quản lý nguồn thu thập (tag CmsAggregation, FR-AGG-001). Bề mặt admin: chỉ phục vụ trên site cms (ADR-0001 D5).
/// Cờ aggregation.radar kiểm ở service (đọc: registry; ghi: trong transaction). Quyền DRAFT chờ BA/PO duyệt (ADR-0008 §8).
/// Thứ tự lỗi: 401 → 403 CSRF → 403 quyền → 403 FEATURE_DISABLED → 404 → 428/412 → 409/422.
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/aggregation/sources")]
public sealed class AdminAggregationSourcesController(AggregationSourceService sources, ICursorCodec cursors) : TinhteControllerBase
{
    /// <summary>listAdminAggregationSources: lọc theo trạng thái (lặp tham số hoặc phân tách bằng dấu phẩy) và tên nguồn.</summary>
    [HttpGet(Name = "listAdminAggregationSources")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<CursorPage<AggregationSourceSummary>>> List(
        [FromQuery] PageRequest page,
        [FromQuery(Name = "status")] string[]? status,
        [FromQuery(Name = "q"), StringLength(200)] string? q,
        CancellationToken cancellationToken)
    {
        var statuses = (status ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (statuses.FirstOrDefault(s => !SourceStatuses.All.Contains(s)) is { } unknown)
        {
            throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid status filter", $"Unknown status '{unknown}'.");
        }

        var filter = new AggregationSourceFilter(statuses, string.IsNullOrWhiteSpace(q) ? null : q.Trim());
        var scope = CursorScope("admin-aggregation-sources", filter.ScopeKey());
        var after = page.Cursor is { } cursor ? cursors.Decode<AggregationSourcePosition>(scope, cursor) : null;
        var rows = await sources.ListAsync(filter, after, page.Limit + 1, cancellationToken);
        return Ok(rows.ToPage(page.Limit, cursors, scope, x => new AggregationSourcePosition(x.Name, x.Id)));
    }

    /// <summary>createAdminAggregationSource: hồ sơ PROPOSED + M0 cho một nguồn có sẵn trong danh mục; đã có hồ sơ → 409.</summary>
    [HttpPost(Name = "createAdminAggregationSource")]
    [RequirePermission(Permissions.AggregationSourcesManage)]
    [Idempotent]
    public async Task<ActionResult<AggregationSource>> Create(AggregationSourceCreate request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesManage);
        var source = await sources.CreateAsync(actor, request, cancellationToken);
        SetETag(source.RowVersion);
        return CreatedAtRoute("getAdminAggregationSource", new { sourceId = source.Id }, source);
    }

    /// <summary>getAdminAggregationSource: hồ sơ + feeds + lịch sử duyệt, ETag W/"rowVersion" của hồ sơ.</summary>
    [HttpGet("{sourceId}", Name = "getAdminAggregationSource")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationSource>> Get(Guid sourceId, CancellationToken cancellationToken) =>
        WithETag(await sources.GetAsync(sourceId, cancellationToken));

    /// <summary>patchAdminAggregationSource: trustWeight/language/siteKeys (If-Match). usageMode/licenseId → 422, dùng approval.</summary>
    [HttpPatch("{sourceId}", Name = "patchAdminAggregationSource")]
    [RequirePermission(Permissions.AggregationSourcesManage)]
    public async Task<ActionResult<AggregationSource>> Patch(Guid sourceId, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesManage);
        await sources.EnsureExistsAsync(sourceId, cancellationToken);
        var expected = RequireIfMatch();
        var patch = AggregationSourcePatch.Parse(body);
        return WithETag(await sources.PatchAsync(actor, sourceId, expected, patch, cancellationToken));
    }

    /// <summary>approveAdminAggregationSource: aggregation.sources_approve (MFA); M2/M3 cần giấy phép hiệu lực + tách nhiệm vụ.</summary>
    [HttpPost("{sourceId}/approval", Name = "approveAdminAggregationSource")]
    [RequirePermission(Permissions.AggregationSourcesApprove)]
    [Idempotent]
    public async Task<ActionResult<AggregationSource>> Approve(Guid sourceId, AggregationSourceApprovalRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesApprove);
        await sources.EnsureExistsAsync(sourceId, cancellationToken);
        var expected = RequireIfMatch();
        return WithETag(await sources.ApproveAsync(actor, sourceId, expected, request, cancellationToken));
    }

    /// <summary>pauseAdminAggregationSource: lý do bắt buộc (REASON_REQUIRED), ghi lịch sử chỉ thêm.</summary>
    [HttpPost("{sourceId}/pause", Name = "pauseAdminAggregationSource")]
    [RequirePermission(Permissions.AggregationSourcesManage)]
    public async Task<ActionResult<AggregationSource>> Pause(Guid sourceId, AggregationActionReason request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesManage);
        await sources.EnsureExistsAsync(sourceId, cancellationToken);
        var expected = RequireIfMatch();
        return WithETag(await sources.PauseAsync(actor, sourceId, expected, request.Reason, cancellationToken));
    }

    /// <summary>resumeAdminAggregationSource: chỉ nguồn PAUSED đã được duyệt; chưa duyệt → 409.</summary>
    [HttpPost("{sourceId}/resume", Name = "resumeAdminAggregationSource")]
    [RequirePermission(Permissions.AggregationSourcesManage)]
    public async Task<ActionResult<AggregationSource>> Resume(Guid sourceId, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesManage);
        await sources.EnsureExistsAsync(sourceId, cancellationToken);
        var expected = RequireIfMatch();
        return WithETag(await sources.ResumeAsync(actor, sourceId, expected, cancellationToken));
    }

    /// <summary>retireAdminAggregationSource: ngừng vĩnh viễn (không xóa), lý do bắt buộc.</summary>
    [HttpPost("{sourceId}/retire", Name = "retireAdminAggregationSource")]
    [RequirePermission(Permissions.AggregationSourcesManage)]
    public async Task<ActionResult<AggregationSource>> Retire(Guid sourceId, AggregationActionReason request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesManage);
        await sources.EnsureExistsAsync(sourceId, cancellationToken);
        var expected = RequireIfMatch();
        return WithETag(await sources.RetireAsync(actor, sourceId, expected, request.Reason, cancellationToken));
    }

    /// <summary>createAdminAggregationFeed: https, không trỏ vào địa chỉ nội bộ (UNSAFE_URL); hồ sơ nguồn quay lại chờ duyệt.</summary>
    [HttpPost("{sourceId}/feeds", Name = "createAdminAggregationFeed")]
    [RequirePermission(Permissions.AggregationSourcesManage)]
    [Idempotent]
    public async Task<ActionResult<AggregationFeed>> CreateFeed(Guid sourceId, AggregationFeedCreate request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesManage);
        var feed = await sources.CreateFeedAsync(actor, sourceId, request, cancellationToken);
        SetETag(feed.RowVersion);
        return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status201Created, feed);
    }

    private OkObjectResult WithETag(AggregationSource source)
    {
        SetETag(source.RowVersion);
        return Ok(source);
    }
}

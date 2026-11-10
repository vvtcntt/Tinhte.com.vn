using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Aggregation.Controllers;

/// <summary>
/// Radar cụm sự kiện (tag CmsAggregation, FR-AGG-004/005). Hợp đồng chỉ đòi <c>aggregation.read</c> cho cả xem lẫn thao tác trên cụm.
/// Thứ tự lỗi: 401 → 403 CSRF → 403 quyền → 403 FEATURE_DISABLED → 404 → 428/412 → 409/422.
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/aggregation/clusters")]
public sealed class AdminAggregationClustersController(AggregationClusterService clusters, AggregationRadarService radar, ICursorCodec cursors)
    : TinhteControllerBase
{
    /// <summary>listAdminAggregationClusters: điểm giảm dần, rồi lúc thấy đầu tiên, rồi id; mặc định OPEN + WATCHING.</summary>
    [HttpGet(Name = "listAdminAggregationClusters")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<CursorPage<AggregationClusterSummary>>> List(
        [FromQuery] PageRequest page,
        [FromQuery(Name = "siteKey")] string? siteKey,
        [FromQuery(Name = "date")] string? date,
        [FromQuery(Name = "window")] string? window,
        [FromQuery(Name = "categoryId")] Guid? categoryId,
        [FromQuery(Name = "status")] string[]? status,
        [FromQuery(Name = "minScore"), Range(0, 100)] decimal? minScore,
        CancellationToken cancellationToken)
    {
        var site = radar.RequireContentSite(siteKey);
        var (from, to, rolling) = radar.ParseWindow(date, window);
        var statuses = (status ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (statuses.FirstOrDefault(s => !AggregationClusterService.Statuses.Contains(s)) is { } unknown)
        {
            throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid status filter", $"Unknown status '{unknown}'.");
        }

        var filter = new AggregationClusterFilter(site, from, to, categoryId, statuses.Count > 0 ? statuses : AggregationClusterService.DefaultStatuses, minScore);

        // LAST_24H trượt theo thời gian: cursor mang mốc đầu cửa sổ của trang đầu; DAY gắn mốc vào scope (đổi ngày → INVALID_CURSOR).
        var scope = CursorScope(rolling ? "admin-aggregation-clusters-24h" : "admin-aggregation-clusters", (rolling ? filter with { From = default } : filter).ScopeKey());
        AggregationClusterPosition? after = null;
        if (page.Cursor is { } cursor)
        {
            var position = cursors.Decode<ClustersCursor>(scope, cursor);
            if (rolling) filter = filter with { From = position.From };
            after = new AggregationClusterPosition(position.Score, position.FirstSeenAt, position.Id);
        }

        var rows = await clusters.ListAsync(filter, after, page.Limit + 1, cancellationToken);
        return Ok(rows.ToPage(page.Limit, cursors, scope, x => new ClustersCursor(filter.From, x.Score, x.FirstSeenAt, x.Id)));
    }

    /// <summary>getAdminAggregationCluster: cụm + mục tin (theo thời gian) + lịch sử thao tác; ETag W/"rowVersion".</summary>
    [HttpGet("{clusterId}", Name = "getAdminAggregationCluster")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationClusterDetail>> Get(Guid clusterId, CancellationToken cancellationToken) =>
        WithETag(await clusters.GetAsync(clusterId, cancellationToken));

    /// <summary>dismissAdminAggregationCluster: lý do bắt buộc (REASON_REQUIRED).</summary>
    [HttpPost("{clusterId}/dismiss", Name = "dismissAdminAggregationCluster")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationClusterDetail>> Dismiss(Guid clusterId, AggregationActionReason request, CancellationToken cancellationToken)
    {
        var (actor, expected) = await PrepareAsync(clusterId, cancellationToken);
        return WithETag(await clusters.DismissAsync(actor, clusterId, expected, request.Reason, cancellationToken));
    }

    /// <summary>watchAdminAggregationCluster.</summary>
    [HttpPost("{clusterId}/watch", Name = "watchAdminAggregationCluster")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationClusterDetail>> Watch(Guid clusterId, CancellationToken cancellationToken)
    {
        var (actor, expected) = await PrepareAsync(clusterId, cancellationToken);
        return WithETag(await clusters.WatchAsync(actor, clusterId, expected, cancellationToken));
    }

    /// <summary>reopenAdminAggregationCluster: từ DISMISSED hoặc WATCHING.</summary>
    [HttpPost("{clusterId}/reopen", Name = "reopenAdminAggregationCluster")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationClusterDetail>> Reopen(Guid clusterId, CancellationToken cancellationToken)
    {
        var (actor, expected) = await PrepareAsync(clusterId, cancellationToken);
        return WithETag(await clusters.ReopenAsync(actor, clusterId, expected, cancellationToken));
    }

    /// <summary>claimAdminAggregationCluster: người khác đã nhận → 409.</summary>
    [HttpPost("{clusterId}/claim", Name = "claimAdminAggregationCluster")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationClusterDetail>> Claim(Guid clusterId, CancellationToken cancellationToken)
    {
        var (actor, expected) = await PrepareAsync(clusterId, cancellationToken);
        return WithETag(await clusters.ClaimAsync(actor, clusterId, expected, cancellationToken));
    }

    /// <summary>coverAdminAggregationCluster: bài phải thuộc site của cụm (422).</summary>
    [HttpPost("{clusterId}/coverage", Name = "coverAdminAggregationCluster")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationClusterDetail>> Cover(Guid clusterId, AggregationCoverageRequest request, CancellationToken cancellationToken)
    {
        var (actor, expected) = await PrepareAsync(clusterId, cancellationToken);
        return WithETag(await clusters.CoverAsync(actor, clusterId, expected, request.ArticleId!.Value, cancellationToken));
    }

    /// <summary>splitAdminAggregationCluster: 201 + cụm mới; an toàn khi gửi lại với Idempotency-Key.</summary>
    [HttpPost("{clusterId}/split", Name = "splitAdminAggregationCluster")]
    [RequirePermission(Permissions.AggregationRead)]
    [Idempotent]
    public async Task<ActionResult<AggregationClusterDetail>> Split(Guid clusterId, AggregationSplitRequest request, CancellationToken cancellationToken)
    {
        var (actor, expected) = await PrepareAsync(clusterId, cancellationToken);
        var created = await clusters.SplitAsync(actor, clusterId, expected, request, cancellationToken);
        SetETag(created.RowVersion);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>mergeAdminAggregationCluster: If-Match của cụm trong path (bị đóng sau khi gộp); trả cụm đích kèm ETag của nó.</summary>
    [HttpPost("{clusterId}/merge", Name = "mergeAdminAggregationCluster")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationClusterDetail>> Merge(Guid clusterId, AggregationMergeRequest request, CancellationToken cancellationToken)
    {
        var (actor, expected) = await PrepareAsync(clusterId, cancellationToken);
        return WithETag(await clusters.MergeAsync(actor, clusterId, expected, request, cancellationToken));
    }

    private async Task<(Guid Actor, long Expected)> PrepareAsync(Guid clusterId, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationRead);
        await clusters.EnsureExistsAsync(clusterId, cancellationToken);
        return (actor, RequireIfMatch());
    }

    private OkObjectResult WithETag(AggregationClusterDetail detail)
    {
        SetETag(detail.RowVersion);
        return Ok(detail);
    }

    private sealed record ClustersCursor(DateTimeOffset From, decimal Score, DateTimeOffset FirstSeenAt, Guid Id);
}

/// <summary>Thống kê thu thập + Radar của một ngày (FR-AGG-002/005); trả cả múi giờ tòa soạn để CMS hiển thị "hôm nay" đúng.</summary>
[Route(ApiRoutes.Prefix + "/admin/aggregation/stats")]
public sealed class AdminAggregationStatsController(AggregationClusterService clusters, AggregationRadarService radar) : TinhteControllerBase
{
    /// <summary>getAdminAggregationStats.</summary>
    [HttpGet(Name = "getAdminAggregationStats")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationStats>> Get([FromQuery(Name = "siteKey")] string? siteKey, [FromQuery(Name = "date")] string? date,
        CancellationToken cancellationToken)
    {
        var site = radar.RequireContentSite(siteKey);
        DateOnly? day = date is null ? null : AggregationRadarService.ParseDate(date);
        return Ok(await clusters.StatsAsync(site, day, cancellationToken));
    }
}

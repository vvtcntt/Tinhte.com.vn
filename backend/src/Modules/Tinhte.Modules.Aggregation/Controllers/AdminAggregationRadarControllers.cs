using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Aggregation.Controllers;

/// <summary>Thử đọc feed bằng bộ thu thập, không lưu (tag CmsAggregation, FR-AGG-002). Không có IdempotencyKey trong hợp đồng.</summary>
[Route(ApiRoutes.Prefix + "/admin/aggregation/feed-tests")]
public sealed class AdminAggregationFeedTestsController(FeedTestService feedTests) : TinhteControllerBase
{
    /// <summary>testAdminAggregationFeed: URL nội bộ (kể cả qua redirect/DNS) → 422 UNSAFE_URL; quá số lần/phút → 429 + Retry-After.</summary>
    [HttpPost(Name = "testAdminAggregationFeed")]
    [RequirePermission(Permissions.AggregationSourcesManage)]
    public async Task<ActionResult<AggregationFeedTestResult>> Test(AggregationFeedTestRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesManage);
        return Ok(await feedTests.TestAsync(actor, request,
            wait => Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture), cancellationToken));
    }
}

/// <summary>Luật phân loại theo chuyên mục (FR-AGG-004). If-Match/ETag theo rowVersion của luật (chưa có luật = 1).</summary>
[Route(ApiRoutes.Prefix + "/admin/aggregation/category-rules")]
public sealed class AdminAggregationCategoryRulesController(AggregationRadarService radar) : TinhteControllerBase
{
    /// <summary>listAdminAggregationCategoryRules: mỗi chuyên mục đang bật của siteKey một luật.</summary>
    [HttpGet(Name = "listAdminAggregationCategoryRules")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<AggregationCategoryRuleList>> List([FromQuery(Name = "siteKey")] string? siteKey, CancellationToken cancellationToken) =>
        Ok(await radar.ListRulesAsync(radar.RequireContentSite(siteKey), cancellationToken));

    /// <summary>patchAdminAggregationCategoryRule.</summary>
    [HttpPatch("{categoryId}", Name = "patchAdminAggregationCategoryRule")]
    [RequirePermission(Permissions.AggregationSourcesManage)]
    public async Task<ActionResult<AggregationCategoryRule>> Patch(Guid categoryId, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesManage);
        await radar.EnsureCategoryExistsAsync(categoryId, cancellationToken);
        var expected = RequireIfMatch();
        var patch = AggregationCategoryRulePatch.Parse(body);
        var rule = await radar.PatchRuleAsync(actor, categoryId, expected, patch, cancellationToken);
        SetETag(rule.RowVersion);
        return Ok(rule);
    }
}

/// <summary>Mục tin đã thu thập (FR-AGG-003), gồm cả mục chưa vào cụm và ngoài phạm vi. Văn bản thuần.</summary>
[Route(ApiRoutes.Prefix + "/admin/aggregation/items")]
public sealed class AdminAggregationItemsController(AggregationRadarService radar, ICursorCodec cursors) : TinhteControllerBase
{
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "NEW", "CLASSIFIED", "CLUSTERED", "DUPLICATE", "OUT_OF_SCOPE" };

    /// <summary>listAdminAggregationItems: date/window theo múi giờ tòa soạn; status lặp tham số hoặc phân tách bằng dấu phẩy.</summary>
    [HttpGet(Name = "listAdminAggregationItems")]
    [RequirePermission(Permissions.AggregationRead)]
    public async Task<ActionResult<CursorPage<AggregationItem>>> List(
        [FromQuery] PageRequest page,
        [FromQuery(Name = "siteKey")] string? siteKey,
        [FromQuery(Name = "date")] string? date,
        [FromQuery(Name = "window")] string? window,
        [FromQuery(Name = "sourceId")] Guid? sourceId,
        [FromQuery(Name = "categoryId")] Guid? categoryId,
        [FromQuery(Name = "status")] string[]? status,
        [FromQuery(Name = "q"), StringLength(200)] string? q,
        CancellationToken cancellationToken)
    {
        var site = radar.RequireContentSite(siteKey);
        var (from, to, rolling) = radar.ParseWindow(date, window);

        var statuses = (status ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (statuses.FirstOrDefault(s => !Statuses.Contains(s)) is { } unknown)
        {
            throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid status filter", $"Unknown status '{unknown}'.");
        }

        var filter = new AggregationItemFilter(site, from, to, sourceId, categoryId, statuses, string.IsNullOrWhiteSpace(q) ? null : q.Trim());

        // LAST_24H trượt theo thời gian: cursor mang mốc bắt đầu cửa sổ của trang đầu để các trang sau không lệch; DAY gắn mốc vào scope.
        var scope = CursorScope(rolling ? "admin-aggregation-items-24h" : "admin-aggregation-items", (rolling ? filter with { From = default } : filter).ScopeKey());
        AggregationItemPosition? after = null;
        if (page.Cursor is { } cursor)
        {
            var position = cursors.Decode<ItemsCursor>(scope, cursor);
            if (rolling) filter = filter with { From = position.From };
            after = new AggregationItemPosition(position.FirstSeenAt, position.Id);
        }

        var rows = await radar.ListItemsAsync(filter, after, page.Limit + 1, cancellationToken);
        return Ok(rows.ToPage(page.Limit, cursors, scope, x => new ItemsCursor(filter.From, x.FirstSeenAt, x.Id)));
    }

    /// <summary>Cursor của LAST_24H mang luôn mốc bắt đầu cửa sổ để các trang sau không trượt.</summary>
    private sealed record ItemsCursor(DateTimeOffset From, DateTimeOffset FirstSeenAt, Guid Id);
}

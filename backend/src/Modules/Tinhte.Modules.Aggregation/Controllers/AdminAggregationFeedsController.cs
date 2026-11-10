using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Aggregation.Controllers;

/// <summary>Feed của nguồn thu thập (tag CmsAggregation, FR-AGG-001/002). Feed chỉ tắt, không xóa; đổi URL ⇒ nguồn chờ duyệt lại.</summary>
[Route(ApiRoutes.Prefix + "/admin/aggregation/feeds")]
public sealed class AdminAggregationFeedsController(AggregationSourceService sources) : TinhteControllerBase
{
    /// <summary>patchAdminAggregationFeed (If-Match theo rowVersion của feed).</summary>
    [HttpPatch("{feedId}", Name = "patchAdminAggregationFeed")]
    [RequirePermission(Permissions.AggregationSourcesManage)]
    public async Task<ActionResult<AggregationFeed>> Patch(Guid feedId, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AggregationSourcesManage);
        await sources.EnsureFeedExistsAsync(feedId, cancellationToken);
        var expected = RequireIfMatch();
        var patch = AggregationFeedPatch.Parse(body);
        var feed = await sources.PatchFeedAsync(actor, feedId, expected, patch, cancellationToken);
        SetETag(feed.RowVersion);
        return Ok(feed);
    }
}

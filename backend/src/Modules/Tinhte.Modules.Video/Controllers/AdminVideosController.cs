using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Video.Application;
using Tinhte.Modules.Video.Domain;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Video.Controllers;

/// <summary>
/// Video trong CMS (tag CmsVideo, WBS-046). Bề mặt admin: chỉ phục vụ trên site cms (ADR-0001 D5).
/// Thứ tự lỗi: 401 → 403 CSRF → 403 quyền → 404 → 428/412 → 409/422.
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/videos")]
public sealed class AdminVideosController(VideoService videos, ICursorCodec cursors) : TinhteControllerBase
{
    /// <summary>getAdminVideos: danh sách CMS phân trang cursor, lọc theo trạng thái/người/thời gian/từ khóa.</summary>
    [HttpGet(Name = "getAdminVideos")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<CursorPage<AdminVideo>>> List(
        [FromQuery] PageRequest page,
        [FromQuery(Name = "status")] string[]? status,
        [FromQuery(Name = "ownerId")] Guid? ownerId,
        [FromQuery(Name = "assigneeId")] Guid? assigneeId,
        [FromQuery(Name = "updatedFrom")] DateTimeOffset? updatedFrom,
        [FromQuery(Name = "updatedTo")] DateTimeOffset? updatedTo,
        [FromQuery(Name = "q"), StringLength(200)] string? q,
        [FromQuery(Name = "sort"), RegularExpression(AdminVideoFilter.SortPattern)] string? sort,
        CancellationToken cancellationToken)
    {
        // ContentStatusFilter: style form, explode false → "DRAFT,PUBLISHED" (nhận cả dạng lặp tham số).
        var statuses = (status ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (statuses.FirstOrDefault(s => !VideoStatuses.All.Contains(s)) is { } unknown)
        {
            throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid status filter", $"Unknown status '{unknown}'.");
        }

        var filter = new AdminVideoFilter(statuses, ownerId, assigneeId, updatedFrom, updatedTo, string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            sort ?? AdminVideoFilter.DefaultSort);
        var scope = CursorScope("admin-videos", filter.ScopeKey());
        var after = page.Cursor is { } cursor ? cursors.Decode<AdminVideoPosition>(scope, cursor) : null;
        var rows = await videos.ListAsync(filter, after, page.Limit + 1, cancellationToken);
        var byTitle = filter.Sort == "title_asc";
        var byCreated = filter.Sort == "createdAt_desc";
        var result = rows.ToPage(page.Limit, cursors, scope, x => new AdminVideoPosition(
            byTitle ? null : byCreated ? x.CreatedAt : x.Video.UpdatedAt, byTitle ? x.Video.Title : null, x.Video.Id));
        return Ok(new CursorPage<AdminVideo>(result.Items.Select(x => x.Video).ToList(), result.NextCursor));
    }

    /// <summary>postAdminVideos: video DRAFT + working revision số 1, tùy chọn gắn media (VIDEO, chưa hỏng, chưa gắn nơi khác).</summary>
    [HttpPost(Name = "postAdminVideos")]
    [RequirePermission(Permissions.ContentWriteOwn)]
    [Idempotent]
    public async Task<ActionResult<AdminVideo>> Create(CreateVideoRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentWriteOwn);
        var video = await videos.CreateAsync(actor, request, cancellationToken);
        SetETag(video.RowVersion);
        return CreatedAtRoute("getAdminVideosVideoid", new { videoId = video.Id }, video);
    }

    /// <summary>getAdminVideosVideoid: chi tiết + ETag W/"rowVersion".</summary>
    [HttpGet("{videoId}", Name = "getAdminVideosVideoid")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<AdminVideo>> Get(Guid videoId, CancellationToken cancellationToken)
    {
        var video = await videos.GetAsync(videoId, cancellationToken);
        SetETag(video.RowVersion);
        return Ok(video);
    }

    /// <summary>
    /// patchAdminVideosVideoid: sửa working revision + gắn media (If-Match). Không bao giờ đổi publicRevisionId.
    /// Quyền theo dữ liệu: video của mình → content.write_own, của người khác → content.write_any.
    /// </summary>
    [HttpPatch("{videoId}", Name = "patchAdminVideosVideoid")]
    [RequirePermission(Permissions.ContentWriteOwn)]
    public async Task<ActionResult<AdminVideo>> Patch(Guid videoId, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var current = await videos.GetAsync(videoId, cancellationToken);
        VideoService.RequireEditPermission(User, current.OwnerId);
        var expected = RequireIfMatch();
        var patch = VideoPatch.Parse(body);
        var video = await videos.PatchAsync(User, videoId, expected, patch, cancellationToken);
        SetETag(video.RowVersion);
        return Ok(video);
    }
}

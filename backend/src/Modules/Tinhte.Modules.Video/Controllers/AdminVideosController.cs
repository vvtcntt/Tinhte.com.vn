using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
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

    // ---------------------------------------------------------------- workflow (WBS-040, VideoService.Workflow.cs)

    /// <summary>postAdminVideosVideoidSubmit: gửi working revision DRAFT (có media) đi duyệt — quyền sửa video theo dữ liệu, như PATCH.</summary>
    [HttpPost("{videoId}/submit", Name = "postAdminVideosVideoidSubmit")]
    [RequirePermission(Permissions.ContentWriteOwn)]
    [Idempotent]
    public async Task<ActionResult<AdminVideo>> Submit(Guid videoId, CancellationToken cancellationToken) =>
        WithETag(await videos.SubmitAsync(User, videoId, RequireIfMatch, cancellationToken));

    /// <summary>postAdminVideosVideoidApprove: content.approve (MFA) + tách nhiệm vụ; chốt revision + thế hệ media đang gắn.</summary>
    [HttpPost("{videoId}/approve", Name = "postAdminVideosVideoidApprove")]
    [RequirePermission(Permissions.ContentApprove)]
    [Idempotent]
    public async Task<ActionResult<AdminVideo>> Approve(Guid videoId, VideoApproveRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentApprove);
        return WithETag(await videos.ApproveAsync(actor, videoId, request.RevisionId!.Value, request.Note, RequireIfMatch, cancellationToken));
    }

    /// <summary>postAdminVideosVideoidRequestChanges: content.review; lý do bắt buộc (REASON_REQUIRED). Hợp đồng trả 201.</summary>
    [HttpPost("{videoId}/request-changes", Name = "postAdminVideosVideoidRequestChanges")]
    [RequirePermission(Permissions.ContentReview)]
    [Idempotent]
    public async Task<ActionResult<AdminVideo>> RequestChanges(Guid videoId, VideoActionReason request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentReview);
        var video = await videos.ReturnAsync(actor, videoId, request.Reason, reject: false, RequireIfMatch, cancellationToken);
        SetETag(video.RowVersion);
        return StatusCode(StatusCodes.Status201Created, video);
    }

    /// <summary>postAdminVideosVideoidReject: content.approve (MFA); lý do bắt buộc.</summary>
    [HttpPost("{videoId}/reject", Name = "postAdminVideosVideoidReject")]
    [RequirePermission(Permissions.ContentApprove)]
    [Idempotent]
    public async Task<ActionResult<AdminVideo>> Reject(Guid videoId, VideoActionReason request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentApprove);
        return WithETag(await videos.ReturnAsync(actor, videoId, request.Reason, reject: true, RequireIfMatch, cancellationToken));
    }

    /// <summary>
    /// postAdminVideosVideoidPublish: content.publish (MFA) + SoD. Chỉ revision APPROVED, media READY, quyền media + video CLEARED còn hạn
    /// (422 MEDIA_NOT_READY / RIGHTS_INVALID); SPONSORED do trigger 0307 chặn (403 FEATURE_DISABLED / 422 SPONSORSHIP_*).
    /// </summary>
    [HttpPost("{videoId}/publish", Name = "postAdminVideosVideoidPublish")]
    [RequirePermission(Permissions.ContentPublish)]
    [Idempotent]
    public async Task<ActionResult<AdminVideo>> Publish(Guid videoId, VideoPublishRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentPublish);
        return WithETag(await videos.PublishAsync(actor, videoId, request.RevisionId!.Value, RequireIfMatch, cancellationToken));
    }

    /// <summary>postAdminVideosVideoidUnpublish: gỡ khẩn cấp (content.takedown, MFA); If-Match tùy chọn; lý do bắt buộc.</summary>
    [HttpPost("{videoId}/unpublish", Name = "postAdminVideosVideoidUnpublish")]
    [RequirePermission(Permissions.ContentTakedown)]
    [Idempotent]
    public async Task<ActionResult<AdminVideo>> Unpublish(Guid videoId, VideoActionReason request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.ContentTakedown);
        await videos.GetAsync(videoId, cancellationToken);   // 404 trước 412
        long? ifMatch = Request.Headers.ContainsKey(HeaderNames.IfMatch) ? RequireIfMatch() : null;
        return WithETag(await videos.UnpublishAsync(actor, videoId, request.Reason, ifMatch, cancellationToken));
    }

    private OkObjectResult WithETag(AdminVideo video)
    {
        SetETag(video.RowVersion);
        return Ok(video);
    }
}

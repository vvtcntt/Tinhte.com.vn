using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Video.Application;
using Tinhte.Modules.Video.Domain;
using Tinhte.Modules.Video.Infrastructure;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Video.Controllers;

/// <summary>
/// Media trong CMS (tag Media, WBS-046; ADR-0006): upload intent → PUT tệp → finalize → theo dõi trạng thái; thư viện media.
/// Chỉ phục vụ trên site cms (bề mặt admin, ADR-0001 D5).
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/media")]
public sealed class AdminMediaController(MediaService media, IMediaStorage storage, ICursorCodec cursors) : TinhteControllerBase
{
    /// <summary>createUploadIntent: URL tải lên ngắn hạn (chỉ đúng object + kích thước) + token finalize một lần.</summary>
    [HttpPost("upload-intents", Name = "createUploadIntent")]
    [RequirePermission(Permissions.MediaUpload)]
    [Idempotent]
    public async Task<ActionResult<UploadIntent>> CreateUploadIntent(CreateUploadIntentRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.MediaUpload);
        var intent = await media.CreateIntentAsync(actor, request, RequestOrigin(), cancellationToken);
        return CreatedAtRoute("getAdminMedia", new { mediaId = intent.MediaId }, intent);
    }

    /// <summary>finalizeUpload: kiểm object (tồn tại, kích thước, sha256) rồi đưa vào pipeline xử lý → 202.</summary>
    [HttpPost("{mediaId}/finalize", Name = "finalizeUpload")]
    [RequirePermission(Permissions.MediaUpload)]
    [Idempotent]
    public async Task<ActionResult<MediaJobAccepted>> Finalize(Guid mediaId, FinalizeUploadRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.MediaUpload);
        var accepted = await media.FinalizeAsync(actor, mediaId, request.UploadToken, cancellationToken);
        return Accepted(accepted);
    }

    /// <summary>getAdminMedia: trạng thái quét/xử lý để CMS hỏi theo chu kỳ.</summary>
    [HttpGet("{mediaId}", Name = "getAdminMedia")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<MediaStatus>> Get(Guid mediaId, CancellationToken cancellationToken) =>
        Ok(await media.GetAsync(mediaId, cancellationToken));

    /// <summary>listAdminMediaLibrary: thư viện media (mới nhất trước), lọc theo loại, quyền, hiển thị, tên tệp.</summary>
    [HttpGet(Name = "listAdminMediaLibrary")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<CursorPage<MediaStatus>>> List(
        [FromQuery] PageRequest page,
        [FromQuery(Name = "mediaType"), RegularExpression(MediaTypes.Pattern)] string? mediaType,
        [FromQuery(Name = "rightsStatus"), RegularExpression(MediaRights.RightsPattern)] string? rightsStatus,
        [FromQuery(Name = "visibility"), RegularExpression(MediaRights.VisibilityPattern)] string? visibility,
        [FromQuery(Name = "q"), StringLength(200)] string? q,
        CancellationToken cancellationToken)
    {
        var filter = new MediaFilter(mediaType, rightsStatus, visibility, string.IsNullOrWhiteSpace(q) ? null : q.Trim());
        var scope = CursorScope("admin-media", filter.ScopeKey());
        var after = page.Cursor is { } cursor ? cursors.Decode<MediaPosition>(scope, cursor) : null;
        var rows = await media.ListAsync(filter, after, page.Limit + 1, cancellationToken);
        return Ok(rows.ToPage(page.Limit, cursors, scope, x => new MediaPosition(x.CreatedAt, x.MediaId)));
    }

    /// <summary>
    /// DEV-ONLY, NGOÀI HỢP ĐỒNG (ADR-0006): đích của <c>uploadUrl</c> khi kho lưu trữ là <see cref="LocalMediaStorage"/>.
    /// Production: client PUT thẳng lên object storage bằng URL ký sẵn, endpoint này trả 404.
    /// Cùng origin site cms nên trình duyệt gửi cookie phiên → vẫn cần <c>media.upload</c> + X-CSRF-Token như mọi request ghi.
    /// </summary>
    [HttpPut("{mediaId}/" + LocalMediaStorage.UploadPathSuffix)]
    [RequirePermission(Permissions.MediaUpload)]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> DevUpload(Guid mediaId, [FromQuery(Name = "expires")] long expires, [FromQuery(Name = "signature"), StringLength(100)] string? signature,
        CancellationToken cancellationToken)
    {
        if (storage is not LocalMediaStorage local)
        {
            throw new DomainProblemException(ErrorCodes.NotFound, "Not found", "Direct uploads to the API are only available with the local development storage.");
        }

        await media.ReceiveDevUploadAsync(local, mediaId, expires, signature, Request.ContentLength, Request.Body, cancellationToken);
        return NoContent();
    }

    /// <summary>Origin của site đang gọi (Host đã được SiteResolutionMiddleware kiểm theo registry) — không viết cứng host.</summary>
    private Uri RequestOrigin() => new($"{Request.Scheme}://{Request.Host.Value}");
}

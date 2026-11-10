using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tinhte.Modules.Ai.Application;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.Ai.Controllers;

/// <summary>
/// AI hỗ trợ biên tập (tag AiOptional, FR-AI-001/004, P2 — cờ <c>ai.assistance</c>). Bề mặt admin: chỉ phục vụ trên site cms.
/// Quyền <c>ai.assist</c> là ĐỀ XUẤT trong config/rbac/rbac.json, chờ BA/PO duyệt; nhật ký dùng <c>audit.read</c> (MFA).
/// Thứ tự lỗi gợi ý: 401 → 403 CSRF → 403 quyền → 422 body → 403 cờ → 404 → 429 → 503.
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/ai")]
[Tags("AiOptional")]
public sealed class AdminAiController(MetadataSuggestionService suggestions, AiAuditService audit) : TinhteControllerBase
{
    /// <summary>postAdminAiMetadataSuggestions: gợi ý tag (TAG) hoặc tóm tắt (SUMMARY) cho bài/video; không ghi vào nội dung.</summary>
    [HttpPost("metadata-suggestions", Name = "postAdminAiMetadataSuggestions")]
    [RequirePermission(Permissions.AiAssist)]
    [Idempotent]
    public async Task<ActionResult<AiSuggestion>> Suggest(AiSuggestionRequest request, CancellationToken cancellationToken)
    {
        var actor = User.RequirePermission(Permissions.AiAssist);
        var result = await suggestions.SuggestAsync(actor, request,
            retryAfter => Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>getAdminAiAudit: nhật ký gọi AI, mới nhất trước; lọc theo người và khoảng thời gian [from, to).</summary>
    [HttpGet("audit", Name = "getAdminAiAudit")]
    [RequirePermission(Permissions.AuditRead)]
    public async Task<ActionResult<CursorPage<AiAuditItem>>> Audit(
        [FromQuery] PageRequest page,
        [FromQuery(Name = "actorId")] Guid? actorId,
        [FromQuery(Name = "from")] DateTimeOffset? from,
        [FromQuery(Name = "to")] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var filter = new AiAuditService.Filter(actorId, from, to);
        var scope = CursorScope("admin-ai-audit", $"{actorId}|{from?.ToUnixTimeMilliseconds()}|{to?.ToUnixTimeMilliseconds()}");
        return Ok(await audit.ListAsync(filter, page, scope, cancellationToken));
    }
}

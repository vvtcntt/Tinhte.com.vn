using Microsoft.AspNetCore.Mvc;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Sites;

namespace Tinhte.Platform.Mvc;

/// <summary>
/// Base cho mọi controller của API (ADR-0002). Route đặt tường minh theo đường dẫn của hợp đồng OpenAPI v0.3
/// (ví dụ <c>[Route(ApiRoutes.Prefix + "/site")]</c>). Lỗi nghiệp vụ trả bằng <see cref="Problem(string, string, string)"/>
/// để luôn đúng schema Problem + mã trong x-error-catalog.
/// </summary>
[ApiController]
public abstract class TinhteControllerBase : ControllerBase
{
    /// <summary>Site của request (đã được SiteResolutionMiddleware phân giải từ Host — ADR-0001).</summary>
    protected SiteDefinition Site => HttpContext.RequireSite();

    /// <summary>Trả application/problem+json với mã lỗi của hợp đồng; HTTP status lấy từ ErrorCatalog.</summary>
    protected ObjectResult Problem(string code, string title, string detail) =>
        ProblemFactory.Create(HttpContext, code, title, detail);

    /// <summary>Scope cursor của danh sách trên site hiện tại (WBS-014): cursor không dùng chéo giữa danh sách/subdomain.</summary>
    protected string CursorScope(string list, string? filter = null) => CursorPaging.Scope(Site.Key, list, filter);

    /// <summary>Row version mà client gửi trong If-Match. Gọi sau khi đã nạp tài nguyên (404 đánh giá trước 428/412).</summary>
    protected long RequireIfMatch() => ETags.RequireIfMatch(HttpContext);

    /// <summary>Gắn ETag W/"rowVersion" vào response.</summary>
    protected void SetETag(long rowVersion) => ETags.Set(Response, rowVersion);
}

public static class ApiRoutes
{
    public const string Prefix = "api/v1";
}

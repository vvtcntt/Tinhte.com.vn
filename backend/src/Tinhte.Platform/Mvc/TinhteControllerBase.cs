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
}

public static class ApiRoutes
{
    public const string Prefix = "api/v1";
}

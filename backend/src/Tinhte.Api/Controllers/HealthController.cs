using Microsoft.AspNetCore.Mvc;
using Tinhte.Platform.Mvc;

namespace Tinhte.Api.Controllers;

/// <summary>Liveness/readiness cho orchestrator. Không qua phân giải site (gọi theo IP).</summary>
[Route("health")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class HealthController : TinhteControllerBase
{
    public sealed record HealthResponse(string Status);

    [HttpGet("live")]
    public ActionResult<HealthResponse> Live() => Ok(new HealthResponse("ok"));

    /// <summary>Thêm kiểm tra PostgreSQL ở WBS-013.</summary>
    [HttpGet("ready")]
    public ActionResult<HealthResponse> Ready() => Ok(new HealthResponse("ok"));
}

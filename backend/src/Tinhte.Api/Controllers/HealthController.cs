using Microsoft.AspNetCore.Mvc;
using Tinhte.Platform.Data;
using Tinhte.Platform.Mvc;

namespace Tinhte.Api.Controllers;

/// <summary>Liveness/readiness cho orchestrator. Không qua phân giải site (gọi theo IP).</summary>
[Route("health")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class HealthController(ITinhteDatabase database, IHostEnvironment environment) : TinhteControllerBase
{
    public sealed record HealthResponse(string Status, IReadOnlyDictionary<string, string>? Checks = null);

    [HttpGet("live")]
    public ActionResult<HealthResponse> Live() => Ok(new HealthResponse("ok"));

    /// <summary>
    /// Sẵn sàng nhận traffic khi PostgreSQL trả lời (WBS-013). Chưa cấu hình DB: chỉ chấp nhận ở Development
    /// (chạy khung không DB); môi trường khác trả 503 để orchestrator không đưa instance vào phục vụ.
    /// </summary>
    [HttpGet("ready")]
    public async Task<ActionResult<HealthResponse>> Ready(CancellationToken cancellationToken)
    {
        string state;
        if (!database.IsConfigured)
        {
            state = environment.IsDevelopment() ? "not_configured" : "missing";
        }
        else
        {
            state = await database.PingAsync(cancellationToken) ? "ok" : "unreachable";
        }

        var ready = state is "ok" or "not_configured";
        var body = new HealthResponse(ready ? "ok" : "unavailable", new Dictionary<string, string> { ["database"] = state });
        return ready ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}

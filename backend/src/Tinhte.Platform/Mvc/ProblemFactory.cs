using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Tinhte.Platform.Errors;

namespace Tinhte.Platform.Mvc;

/// <summary>Problem theo schema của hợp đồng: type, title, status, code, detail, traceId, errors.</summary>
public sealed class TinhteProblem
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required int Status { get; init; }

    public required string Code { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    public required string TraceId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, string[]>? Errors { get; init; }
}

public static class ProblemFactory
{
    public static ObjectResult Create(HttpContext context, string code, string title, string? detail, IDictionary<string, string[]>? errors = null)
    {
        var status = ErrorCatalog.StatusOf(code);
        var problem = new TinhteProblem
        {
            Type = $"urn:tinhte:error:{code.ToLowerInvariant()}",
            Title = title,
            Status = status,
            Code = code,
            Detail = detail,
            TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            Errors = errors,
        };
        var result = new ObjectResult(problem) { StatusCode = status };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    /// <summary>
    /// [ApiController] tự trả lỗi khi model không hợp lệ. Hợp đồng phân biệt: body/tham số sai cú pháp → 400 BAD_REQUEST;
    /// vi phạm ràng buộc dữ liệu (DataAnnotations) → 422 VALIDATION_FAILED.
    /// </summary>
    public static IActionResult FromInvalidModelState(ActionContext context)
    {
        var errors = context.ModelState
            .Where(e => e.Value is { Errors.Count: > 0 })
            .ToDictionary(e => e.Key, e => e.Value!.Errors.Select(x => x.ErrorMessage).ToArray());
        var malformed = context.ModelState.Values.Any(v => v.Errors.Any(e => e.Exception is not null))
            || context.ModelState.Any(e => e.Key.StartsWith('$') && e.Value?.ValidationState == ModelValidationState.Invalid);
        return malformed
            ? Create(context.HttpContext, ErrorCodes.BadRequest, "Malformed request", "The request body or parameters could not be parsed.", errors)
            : Create(context.HttpContext, ErrorCodes.ValidationFailed, "Validation failed", "One or more fields are invalid.", errors);
    }
}

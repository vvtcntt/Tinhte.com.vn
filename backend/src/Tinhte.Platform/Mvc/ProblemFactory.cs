using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
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
    /// [ApiController] tự trả lỗi khi model không hợp lệ. Hợp đồng phân biệt:
    /// body sai cú pháp, hoặc tham số query/route/header không hợp lệ (ví dụ limit=0) → 400 BAD_REQUEST;
    /// body đúng cú pháp nhưng vi phạm ràng buộc dữ liệu (DataAnnotations) → 422 VALIDATION_FAILED.
    /// </summary>
    public static IActionResult FromInvalidModelState(ActionContext context)
    {
        var invalid = context.ModelState.Where(e => e.Value is { Errors.Count: > 0 }).ToList();
        var errors = invalid.ToDictionary(e => e.Key, e => e.Value!.Errors.Select(x => x.ErrorMessage).ToArray());
        var malformed = invalid.Any(e => e.Key.StartsWith('$') || e.Value!.Errors.Any(x => x.Exception is not null));
        var nonBody = NonBodyKeys(context.ActionDescriptor);
        var badParameter = invalid.Any(e => nonBody.Contains(FirstSegment(e.Key)));
        return malformed || badParameter
            ? Create(context.HttpContext, ErrorCodes.BadRequest, "Malformed request", "The request body or parameters could not be parsed or are out of range.", errors)
            : Create(context.HttpContext, ErrorCodes.ValidationFailed, "Validation failed", "One or more fields are invalid.", errors);
    }

    /// <summary>Tên khóa ModelState đến từ query/route/header: tên tham số đơn, hoặc tên thuộc tính của kiểu phức [FromQuery] (ví dụ PageRequest).</summary>
    private static HashSet<string> NonBodyKeys(ActionDescriptor action)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in action.Parameters)
        {
            var source = p.BindingInfo?.BindingSource;
            if (source is null || source == BindingSource.Body || source == BindingSource.Services || source == BindingSource.Form) continue;
            keys.Add(p.BindingInfo?.BinderModelName ?? p.Name);
            if (p.ParameterType == typeof(string) || !p.ParameterType.IsClass) continue;
            foreach (var prop in p.ParameterType.GetProperties())
            {
                keys.Add(prop.GetCustomAttribute<FromQueryAttribute>()?.Name ?? prop.GetCustomAttribute<FromRouteAttribute>()?.Name
                    ?? prop.GetCustomAttribute<FromHeaderAttribute>()?.Name ?? prop.Name);
            }
        }

        return keys;
    }

    private static string FirstSegment(string key)
    {
        var end = key.IndexOfAny(['.', '[']);
        return end < 0 ? key : key[..end];
    }
}

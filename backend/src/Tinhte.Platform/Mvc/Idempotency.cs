using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Npgsql;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Sites;

namespace Tinhte.Platform.Mvc;

/// <summary>
/// Đánh dấu action yêu cầu header <c>Idempotency-Key</c> (components.parameters.IdempotencyKey, AIR-023):
/// <list type="bullet">
///   <item>thiếu khóa / độ dài ngoài 8–200 → 400 BAD_REQUEST; chưa đăng nhập → 401 UNAUTHENTICATED;</item>
///   <item>gửi lại cùng khóa + cùng request → phát lại status/body/ETag/Location gốc, kèm <c>Idempotent-Replayed: true</c>;</item>
///   <item>cùng khóa, request khác (body, đường dẫn hoặc query) → 409 IDEMPOTENCY_KEY_REUSED;</item>
///   <item>request cùng khóa đang chạy → 409 STATE_CONFLICT + Retry-After (catalog chưa có mã riêng — xem ADR-0003).</item>
/// </list>
/// Chỉ lưu response thành công (2xx/3xx). Lỗi 4xx/5xx không có tác dụng phụ (transaction đã rollback) nên khóa được giải phóng
/// để client sửa request và thử lại.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class IdempotentAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    public bool IsReusable => true;

    /// <summary>Chạy sớm (trước model binding) để request trùng không chạm tới action.</summary>
    public int Order => -1000;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new IdempotencyFilter(serviceProvider.GetRequiredService<IIdempotencyStore>());
}

public sealed class IdempotencyFilter(IIdempotencyStore store) : IAsyncResourceFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    private static readonly string[] ReplayedHeaders = [HeaderNames.ETag, HeaderNames.Location];

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].ToString();
        if (key.Length is < 8 or > 200)
        {
            context.Result = ProblemFactory.Create(http, ErrorCodes.BadRequest, "Idempotency-Key required",
                $"Send a unique {HeaderName} header (8–200 characters) with this request.");
            return;
        }

        var actor = http.User.FindFirst("sub")?.Value ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(actor))
        {
            context.Result = ProblemFactory.Create(http, ErrorCodes.Unauthenticated, "Authentication required", "Sign in to perform this action.");
            return;
        }

        // Route kèm site: cùng template trên hai subdomain là hai phạm vi khác nhau (ADR-0001).
        var route = $"{http.RequireSite().Key} {context.ActionDescriptor.AttributeRouteInfo?.Template ?? http.Request.Path.Value}";
        var scope = new IdempotencyScope(actor, http.Request.Method.ToUpperInvariant(), route.Length > 200 ? route[..200] : route, key);
        var hash = await RequestHasher.HashAsync(http.Request, http.RequestAborted);

        // Resource filter nằm ngoài phạm vi exception filter của MVC → tự đổi lỗi kho lưu thành Problem.
        IdempotencyBegin begin;
        try
        {
            begin = await store.BeginAsync(scope, hash, http.RequestAborted);
        }
        catch (DomainProblemException e)
        {
            context.Result = ProblemFactory.Create(http, e.Code, e.Title, e.Detail);
            return;
        }
        catch (NpgsqlException e) when (e is not PostgresException)
        {
            context.Result = ProblemFactory.Create(http, ErrorCodes.DependencyUnavailable, "Database unavailable", "Please retry later.");
            return;
        }

        switch (begin)
        {
            case IdempotencyBegin.Mismatch:
                context.Result = ProblemFactory.Create(http, ErrorCodes.IdempotencyKeyReused, "Idempotency key reused",
                    $"This {HeaderName} was already used with a different request.");
                return;

            case IdempotencyBegin.InProgress:
                http.Response.Headers.RetryAfter = "1";
                context.Result = ProblemFactory.Create(http, ErrorCodes.StateConflict, "Request in progress",
                    $"A request with this {HeaderName} is still being processed. Retry shortly.");
                return;

            case IdempotencyBegin.Replay replay:
                context.Result = Replay(http, replay);
                return;
        }

        await ExecuteAndRecordAsync(http, scope, next);
    }

    private async Task ExecuteAndRecordAsync(HttpContext http, IdempotencyScope scope, ResourceExecutionDelegate next)
    {
        var original = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        var stored = false;
        try
        {
            var executed = await next();
            var failed = executed.Exception is not null && !executed.ExceptionHandled;
            var status = http.Response.StatusCode;
            if (!failed && status < 400)
            {
                await store.CompleteAsync(scope, status, Envelope(http, buffer.ToArray()), CancellationToken.None);
                stored = true;
            }
        }
        finally
        {
            http.Response.Body = original;
            if (buffer.Length > 0)
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(original, CancellationToken.None);
            }

            if (!stored)
            {
                await store.AbandonAsync(scope, CancellationToken.None);
            }
        }
    }

    private static string Envelope(HttpContext http, byte[] body)
    {
        var headers = new JsonObject();
        foreach (var name in ReplayedHeaders)
        {
            if (http.Response.Headers.TryGetValue(name, out var value)) headers[name] = value.ToString();
        }

        var envelope = new JsonObject { ["contentType"] = http.Response.ContentType, ["headers"] = headers };
        if (body.Length > 0)
        {
            // Lưu nguyên văn dạng chuỗi: cột jsonb sắp xếp lại khóa của object lồng nhau, nên lưu body dạng JSON
            // sẽ phát lại một body khác thứ tự trường so với response gốc.
            envelope["bodyText"] = Encoding.UTF8.GetString(body);
        }

        return envelope.ToJsonString();
    }

    private static IActionResult Replay(HttpContext http, IdempotencyBegin.Replay replay)
    {
        var envelope = JsonNode.Parse(replay.ResponseJson)?.AsObject() ?? [];
        if (envelope["headers"] is JsonObject headers)
        {
            foreach (var (name, value) in headers)
            {
                if (value is not null) http.Response.Headers[name] = value.GetValue<string>();
            }
        }

        http.Response.Headers[ReplayedHeader] = "true";
        var body = envelope["body"]?.ToJsonString() ?? envelope["bodyText"]?.GetValue<string>();
        return body is null
            ? new StatusCodeResult(replay.StatusCode)
            : new ContentResult { StatusCode = replay.StatusCode, Content = body, ContentType = envelope["contentType"]?.GetValue<string>() };
    }
}

/// <summary>
/// Băm request để phát hiện "cùng khóa, request khác": method + path + query + body.
/// Body JSON được chuẩn hóa (sắp xếp khóa, bỏ khoảng trắng) nên khác thứ tự trường vẫn coi là cùng request.
/// </summary>
public static class RequestHasher
{
    public static async Task<string> HashAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory, cancellationToken);
        request.Body.Position = 0;

        var body = memory.ToArray();
        var canonical = request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ? CanonicalJson(body) : null;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes($"{request.Method.ToUpperInvariant()}\n{request.Path.Value}\n{request.QueryString.Value}\n"));
        sha.AppendData(canonical ?? body);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public static byte[]? CanonicalJson(byte[] body)
    {
        if (body.Length == 0) return body;
        try
        {
            return Encoding.UTF8.GetBytes(Sort(JsonNode.Parse(body))?.ToJsonString() ?? "null");
        }
        catch (JsonException)
        {
            return null; // không phải JSON hợp lệ: băm nguyên byte
        }
    }

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        _ => node?.DeepClone(),
    };
}

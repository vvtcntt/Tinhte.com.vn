using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Tinhte.Platform.Errors;

/// <summary>Mã lỗi của hợp đồng v0.3 (x-error-catalog). Test ContractConsistency giữ khớp với spec.</summary>
public static class ErrorCodes
{
    public const string BadRequest = "BAD_REQUEST";
    public const string InvalidCursor = "INVALID_CURSOR";
    public const string OidcStateInvalid = "OIDC_STATE_INVALID";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string CsrfInvalid = "CSRF_INVALID";
    public const string FeatureDisabled = "FEATURE_DISABLED";
    public const string Forbidden = "FORBIDDEN";
    public const string EligibilityRequired = "ELIGIBILITY_REQUIRED";
    public const string SodViolation = "SOD_VIOLATION";
    public const string RightsExpired = "RIGHTS_EXPIRED";
    public const string NotFound = "NOT_FOUND";
    public const string StateConflict = "STATE_CONFLICT";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string ContentLocked = "CONTENT_LOCKED";
    public const string ContentWithdrawn = "CONTENT_WITHDRAWN";
    public const string RevisionConflict = "REVISION_CONFLICT";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string SponsorshipLabelRequired = "SPONSORSHIP_LABEL_REQUIRED";
    public const string SponsorshipExpired = "SPONSORSHIP_EXPIRED";
    public const string RightsInvalid = "RIGHTS_INVALID";
    public const string MediaNotReady = "MEDIA_NOT_READY";
    public const string UnsafeUrl = "UNSAFE_URL";
    public const string PreconditionRequired = "PRECONDITION_REQUIRED";
    public const string RateLimited = "RATE_LIMITED";
    public const string DependencyUnavailable = "DEPENDENCY_UNAVAILABLE";
}

public static class ErrorCatalog
{
    public static readonly IReadOnlyDictionary<string, int> Statuses = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [ErrorCodes.BadRequest] = 400,
        [ErrorCodes.InvalidCursor] = 400,
        [ErrorCodes.OidcStateInvalid] = 400,
        [ErrorCodes.Unauthenticated] = 401,
        [ErrorCodes.CsrfInvalid] = 403,
        [ErrorCodes.FeatureDisabled] = 403,
        [ErrorCodes.Forbidden] = 403,
        [ErrorCodes.EligibilityRequired] = 403,
        [ErrorCodes.SodViolation] = 403,
        [ErrorCodes.RightsExpired] = 403,
        [ErrorCodes.NotFound] = 404,
        [ErrorCodes.StateConflict] = 409,
        [ErrorCodes.IdempotencyKeyReused] = 409,
        [ErrorCodes.ContentLocked] = 409,
        [ErrorCodes.ContentWithdrawn] = 410,
        [ErrorCodes.RevisionConflict] = 412,
        [ErrorCodes.PayloadTooLarge] = 413,
        [ErrorCodes.ValidationFailed] = 422,
        [ErrorCodes.ReasonRequired] = 422,
        [ErrorCodes.SponsorshipLabelRequired] = 422,
        [ErrorCodes.SponsorshipExpired] = 422,
        [ErrorCodes.RightsInvalid] = 422,
        [ErrorCodes.MediaNotReady] = 422,
        [ErrorCodes.UnsafeUrl] = 422,
        [ErrorCodes.PreconditionRequired] = 428,
        [ErrorCodes.RateLimited] = 429,
        [ErrorCodes.DependencyUnavailable] = 503,
    };

    public static int StatusOf(string code) =>
        Statuses.TryGetValue(code, out var status) ? status : throw new ArgumentException($"Unknown error code '{code}'", nameof(code));
}

/// <summary>Ghi application/problem+json theo schema Problem của hợp đồng.</summary>
public static class Problems
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, string code, string title, string detail)
    {
        var status = ErrorCatalog.StatusOf(code);
        context.Response.StatusCode = status;
        var problem = new
        {
            type = $"urn:tinhte:error:{code.ToLowerInvariant()}",
            title,
            status,
            code,
            detail,
            traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
        };
        return context.Response.WriteAsJsonAsync(problem, Options, "application/problem+json", context.RequestAborted);
    }
}

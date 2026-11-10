using System.Text.Json;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>
/// AggregationSourcePatch (additionalProperties: false, minProperties: 1). Chế độ khai thác/giấy phép/trạng thái KHÔNG đổi được ở đây
/// (ADR-0008 §2): gửi các trường đó → 422 VALIDATION_FAILED chỉ sang thao tác approval/pause/resume/retire.
/// </summary>
public sealed class AggregationSourcePatch
{
    private static readonly HashSet<string> ApprovalOnly = new(StringComparer.Ordinal) { "usageMode", "licenseId", "termsEvidenceRef" };

    public int? TrustWeight { get; private init; }

    public string? Language { get; private init; }

    public IReadOnlyList<string>? SiteKeys { get; private init; }

    public static AggregationSourcePatch Parse(JsonElement body)
    {
        var errors = PatchReader.Begin(body);
        int? trust = null;
        string? language = null;
        IReadOnlyList<string>? siteKeys = null;
        foreach (var p in body.EnumerateObject())
        {
            var v = p.Value;
            switch (p.Name)
            {
                case "trustWeight":
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var t) && t is >= 1 and <= 5) trust = t;
                    else errors[p.Name] = ["An integer from 1 to 5."];
                    break;
                case "language":
                    if (v.ValueKind == JsonValueKind.String && AggregationLimits.IsLanguage(v.GetString())) language = v.GetString();
                    else errors[p.Name] = ["Two lowercase letters (ISO 639-1)."];
                    break;
                case "siteKeys":
                    siteKeys = PatchReader.StringArray(v);
                    if (AggregationLimits.SiteKeysError(siteKeys) is { } siteError) errors[p.Name] = [siteError];
                    break;
                default:
                    errors[p.Name] = [ApprovalOnly.Contains(p.Name)
                        ? "Usage mode and licence change only through POST /admin/aggregation/sources/{sourceId}/approval."
                        : "Unknown property (additionalProperties: false)."];
                    break;
            }
        }

        PatchReader.End(errors);
        return new AggregationSourcePatch { TrustWeight = trust, Language = language, SiteKeys = siteKeys };
    }
}

/// <summary>AggregationFeedPatch (additionalProperties: false, minProperties: 1). URL được kiểm SSRF ở service (cần DNS).</summary>
public sealed class AggregationFeedPatch
{
    public string? Url { get; private init; }

    public string? Format { get; private init; }

    public bool? IsEnabled { get; private init; }

    public int? ScheduleMinutes { get; private init; }

    public IReadOnlyList<AggregationCategoryMapping>? CategoryMapping { get; private init; }

    public static AggregationFeedPatch Parse(JsonElement body)
    {
        var errors = PatchReader.Begin(body);
        string? url = null, format = null;
        bool? enabled = null;
        int? schedule = null;
        IReadOnlyList<AggregationCategoryMapping>? mapping = null;
        foreach (var p in body.EnumerateObject())
        {
            var v = p.Value;
            switch (p.Name)
            {
                case "url":
                    if (v.ValueKind == JsonValueKind.String) url = v.GetString();
                    else errors[p.Name] = ["An absolute https URL."];
                    break;
                case "format":
                    if (v.ValueKind == JsonValueKind.String && FeedFormats.All.Contains(v.GetString()!)) format = v.GetString();
                    else errors[p.Name] = [$"One of {string.Join(", ", FeedFormats.All.Order(StringComparer.Ordinal))}."];
                    break;
                case "isEnabled":
                    if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) enabled = v.GetBoolean();
                    else errors[p.Name] = ["A boolean."];
                    break;
                case "scheduleMinutes":
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var m) && m is >= AggregationLimits.MinScheduleMinutes and <= AggregationLimits.MaxScheduleMinutes) schedule = m;
                    else errors[p.Name] = [$"An integer from {AggregationLimits.MinScheduleMinutes} to {AggregationLimits.MaxScheduleMinutes}."];
                    break;
                case "categoryMapping":
                    mapping = ParseMapping(v, errors);
                    break;
                default:
                    errors[p.Name] = ["Unknown property (additionalProperties: false)."];
                    break;
            }
        }

        PatchReader.End(errors);
        return new AggregationFeedPatch { Url = url, Format = format, IsEnabled = enabled, ScheduleMinutes = schedule, CategoryMapping = mapping };
    }

    private static List<AggregationCategoryMapping>? ParseMapping(JsonElement v, Dictionary<string, string[]> errors)
    {
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > AggregationLimits.MaxCategoryMappings)
        {
            errors["categoryMapping"] = [$"An array of at most {AggregationLimits.MaxCategoryMappings} mappings."];
            return null;
        }

        var result = new List<AggregationCategoryMapping>();
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            var feedCategory = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("feedCategory", out var fc) && fc.ValueKind == JsonValueKind.String
                ? fc.GetString() : null;
            Guid? categoryId = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("categoryId", out var ci) && ci.ValueKind == JsonValueKind.String
                && ci.TryGetGuid(out var g) ? g : null;
            if (string.IsNullOrWhiteSpace(feedCategory) || feedCategory.Length > AggregationLimits.MaxFeedCategoryLength || categoryId is null)
            {
                errors[$"categoryMapping[{i}]"] = ["feedCategory (1–200 characters) and categoryId (uuid) are required."];
            }
            else
            {
                result.Add(new AggregationCategoryMapping(feedCategory, categoryId));
            }

            i++;
        }

        return result;
    }
}

/// <summary>Khung chung cho PATCH đọc thủ công: body phải là object, ít nhất một trường, lỗi gom vào <c>errors</c>.</summary>
internal static class PatchReader
{
    public static Dictionary<string, string[]> Begin(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "The body must be a JSON object.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!body.EnumerateObject().Any()) errors["$"] = ["At least one property is required (minProperties: 1)."];
        return errors;
    }

    public static void End(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0) throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "One or more fields are invalid.", errors);
    }

    public static List<string>? StringArray(JsonElement v) =>
        v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
            ? v.EnumerateArray().Select(x => x.GetString()!).ToList()
            : null;
}

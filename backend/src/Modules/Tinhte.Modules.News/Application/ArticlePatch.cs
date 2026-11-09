using System.Text.Json;
using Tinhte.Modules.News.Domain;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.News.Application;

/// <summary>Trường có trong body hay không (PATCH phân biệt "không gửi" với "gửi null").</summary>
public readonly record struct Optional<T>(bool IsSet, T Value)
{
    public static Optional<T> Unset => default;

    public static Optional<T> Of(T value) => new(true, value);
}

/// <summary>
/// ArticlePatchRequest đã kiểm. Đọc thủ công từ JSON vì cần phân biệt trường vắng mặt với <c>null</c>
/// (summary/sponsorship/rightsExpiresAt = null nghĩa là xóa). Vi phạm → 422 VALIDATION_FAILED kèm <c>errors</c>.
/// </summary>
public sealed class ArticlePatch
{
    public Optional<string> Title { get; private init; }

    public Optional<string?> Summary { get; private init; }

    public Optional<Guid> CategoryId { get; private init; }

    public Optional<JsonElement> BodyBlocks { get; private init; }

    public Optional<string> ContentKind { get; private init; }

    public Optional<DateTimeOffset?> RightsExpiresAt { get; private init; }

    public Optional<string> Slug { get; private init; }

    public Optional<SponsorshipInput?> Sponsorship { get; private init; }

    public Optional<string> ChangeReason { get; private init; }

    /// <summary>Có đổi nội dung revision (khác với chỉ đổi slug/rightsExpiresAt của bài).</summary>
    public bool TouchesRevision => Title.IsSet || Summary.IsSet || CategoryId.IsSet || BodyBlocks.IsSet || ContentKind.IsSet
        || Sponsorship.IsSet || ChangeReason.IsSet;

    public static ArticlePatch Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "The body must be a JSON object.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string field, string message) => errors[field] = [message];

        var count = 0;
        var title = Optional<string>.Unset;
        var summary = Optional<string?>.Unset;
        var categoryId = Optional<Guid>.Unset;
        var bodyBlocks = Optional<JsonElement>.Unset;
        var contentKind = Optional<string>.Unset;
        var rightsExpiresAt = Optional<DateTimeOffset?>.Unset;
        var slug = Optional<string>.Unset;
        var sponsorship = Optional<SponsorshipInput?>.Unset;
        var changeReason = Optional<string>.Unset;

        foreach (var p in body.EnumerateObject())
        {
            count++;
            var v = p.Value;
            switch (p.Name)
            {
                case "title":
                    if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()) || v.GetString()!.Length > 300) Fail("title", "1–300 characters, not blank.");
                    else title = Optional<string>.Of(v.GetString()!);
                    break;
                case "summary":
                    if (v.ValueKind == JsonValueKind.Null) summary = Optional<string?>.Of(null);
                    else if (v.ValueKind == JsonValueKind.String) summary = Optional<string?>.Of(v.GetString());
                    else Fail("summary", "Must be a string or null.");
                    break;
                case "categoryId":
                    if (v.ValueKind == JsonValueKind.String && v.TryGetGuid(out var category)) categoryId = Optional<Guid>.Of(category);
                    else Fail("categoryId", "Must be a uuid.");
                    break;
                case "bodyBlocks":
                    if (BodyBlocksError(v) is { } blocksError) Fail("bodyBlocks", blocksError);
                    else bodyBlocks = Optional<JsonElement>.Of(v.Clone());
                    break;
                case "contentKind":
                    if (v.ValueKind == JsonValueKind.String && v.GetString() is ContentKinds.Editorial or ContentKinds.Sponsored) contentKind = Optional<string>.Of(v.GetString()!);
                    else Fail("contentKind", "Must be EDITORIAL or SPONSORED.");
                    break;
                case "rightsExpiresAt":
                    if (v.ValueKind == JsonValueKind.Null) rightsExpiresAt = Optional<DateTimeOffset?>.Of(null);
                    else if (v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var expires)) rightsExpiresAt = Optional<DateTimeOffset?>.Of(expires);
                    else Fail("rightsExpiresAt", "Must be a date-time or null.");
                    break;
                case "slug":
                    if (v.ValueKind == JsonValueKind.String && ArticleSlug.IsValid(v.GetString())) slug = Optional<string>.Of(v.GetString()!);
                    else Fail("slug", $"Must match {ArticleSlug.Pattern} (max {ArticleSlug.MaxLength}).");
                    break;
                case "sponsorship":
                    if (v.ValueKind == JsonValueKind.Null) sponsorship = Optional<SponsorshipInput?>.Of(null);
                    else if (ParseSponsorship(v, Fail) is { } s) sponsorship = Optional<SponsorshipInput?>.Of(s);
                    break;
                case "changeReason":
                    if (v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 500) changeReason = Optional<string>.Of(v.GetString()!);
                    else Fail("changeReason", "Must be a string of at most 500 characters.");
                    break;
                case "sourceId":
                case "licenseId":
                    // Hợp đồng có trường này nhưng taxonomy nguồn/giấy phép (bảng + kiểm tra rights) thuộc WBS-023: từ chối rõ ràng
                    // thay vì bỏ qua im lặng.
                    Fail(p.Name, "Not supported yet: source/license taxonomy is delivered by WBS-023.");
                    break;
                default:
                    // Trường không có trong ArticlePatchRequest (kể cả readOnly như approvedBy) bị bỏ qua, giống binder JSON mặc định.
                    count--;
                    break;
            }
        }

        if (count == 0 && errors.Count == 0) Fail("$", "At least one property is required (minProperties: 1).");
        if (errors.Count > 0) throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "One or more fields are invalid.", errors);

        return new ArticlePatch
        {
            Title = title, Summary = summary, CategoryId = categoryId, BodyBlocks = bodyBlocks, ContentKind = contentKind,
            RightsExpiresAt = rightsExpiresAt, Slug = slug, Sponsorship = sponsorship, ChangeReason = changeReason,
        };
    }

    /// <summary>bodyBlocks: mảng các object (schema <c>array of object</c>). null nếu hợp lệ.</summary>
    public static string? BodyBlocksError(JsonElement value) =>
        value.ValueKind != JsonValueKind.Array ? "Must be an array of objects."
        : value.EnumerateArray().Any(b => b.ValueKind != JsonValueKind.Object) ? "Every block must be a JSON object."
        : null;

    private static SponsorshipInput? ParseSponsorship(JsonElement v, Action<string, string> fail)
    {
        if (v.ValueKind != JsonValueKind.Object)
        {
            fail("sponsorship", "Must be an object or null.");
            return null;
        }

        string? Text(string name, int max)
        {
            if (v.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()) && x.GetString()!.Length <= max) return x.GetString();
            fail($"sponsorship.{name}", $"Required, 1–{max} characters.");
            return null;
        }

        DateTimeOffset? Time(string name)
        {
            if (v.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String && x.TryGetDateTimeOffset(out var t)) return t;
            fail($"sponsorship.{name}", "Required date-time.");
            return null;
        }

        var result = new SponsorshipInput(Text("sponsorName", 180)!, Text("sponsorReference", 120)!, Text("evidenceRef", 300)!, Time("validFrom"), Time("validTo"));
        return result.SponsorName is null || result.SponsorReference is null || result.EvidenceRef is null || result.ValidFrom is null || result.ValidTo is null ? null : result;
    }
}

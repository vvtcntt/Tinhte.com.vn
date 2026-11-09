using System.Text.Json;
using Tinhte.Modules.Video.Domain;
using Tinhte.Platform.Errors;

namespace Tinhte.Modules.Video.Application;

/// <summary>Trường có trong body hay không (PATCH phân biệt "không gửi" với "gửi null").</summary>
public readonly record struct PatchField<T>(bool IsSet, T Value)
{
    public static PatchField<T> Unset => default;

    public static PatchField<T> Of(T value) => new(true, value);
}

/// <summary>
/// UpdateVideoRequest đã kiểm. Đọc thủ công từ JSON để phân biệt trường vắng mặt với <c>null</c>
/// (summary/sponsorship = null nghĩa là xóa). Vi phạm → 422 VALIDATION_FAILED kèm <c>errors</c>.
/// </summary>
public sealed class VideoPatch
{
    public PatchField<string> Title { get; private init; }

    public PatchField<string?> Summary { get; private init; }

    public PatchField<Guid> MediaId { get; private init; }

    public PatchField<string> ContentKind { get; private init; }

    public PatchField<VideoSponsorshipInput?> Sponsorship { get; private init; }

    public PatchField<string> ChangeReason { get; private init; }

    /// <summary>Có đổi nội dung revision. Media cũng là nội dung của revision (WBS-040, migration 0334).</summary>
    public bool TouchesRevision => Title.IsSet || Summary.IsSet || MediaId.IsSet || ContentKind.IsSet || Sponsorship.IsSet || ChangeReason.IsSet;

    public static VideoPatch Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "The body must be a JSON object.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Fail(string field, string message) => errors[field] = [message];

        var count = 0;
        var title = PatchField<string>.Unset;
        var summary = PatchField<string?>.Unset;
        var mediaId = PatchField<Guid>.Unset;
        var contentKind = PatchField<string>.Unset;
        var sponsorship = PatchField<VideoSponsorshipInput?>.Unset;
        var changeReason = PatchField<string>.Unset;

        foreach (var p in body.EnumerateObject())
        {
            count++;
            var v = p.Value;
            switch (p.Name)
            {
                case "title":
                    if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()) || v.GetString()!.Length > 300) Fail("title", "1–300 characters, not blank.");
                    else title = PatchField<string>.Of(v.GetString()!);
                    break;
                case "summary":
                    if (v.ValueKind == JsonValueKind.Null) summary = PatchField<string?>.Of(null);
                    else if (v.ValueKind == JsonValueKind.String) summary = PatchField<string?>.Of(v.GetString());
                    else Fail("summary", "Must be a string or null.");
                    break;
                case "mediaId":
                    // Hợp đồng: uuid, không nullable — gỡ media khỏi video không có trong UpdateVideoRequest.
                    if (v.ValueKind == JsonValueKind.String && v.TryGetGuid(out var media)) mediaId = PatchField<Guid>.Of(media);
                    else Fail("mediaId", "Must be a uuid.");
                    break;
                case "contentKind":
                    if (v.ValueKind == JsonValueKind.String && v.GetString() is VideoContentKinds.Editorial or VideoContentKinds.Sponsored) contentKind = PatchField<string>.Of(v.GetString()!);
                    else Fail("contentKind", "Must be EDITORIAL or SPONSORED.");
                    break;
                case "sponsorship":
                    if (v.ValueKind == JsonValueKind.Null) sponsorship = PatchField<VideoSponsorshipInput?>.Of(null);
                    else if (ParseSponsorship(v, Fail) is { } s) sponsorship = PatchField<VideoSponsorshipInput?>.Of(s);
                    break;
                case "changeReason":
                    if (v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 500) changeReason = PatchField<string>.Of(v.GetString()!);
                    else Fail("changeReason", "Must be a string of at most 500 characters.");
                    break;
                case "rightsSource":
                    // Có trong hợp đồng nhưng taxonomy nguồn/giấy phép thuộc WBS-023: từ chối rõ ràng thay vì bỏ qua im lặng.
                    Fail(p.Name, "Not supported yet: source/license taxonomy is delivered by WBS-023.");
                    break;
                default:
                    count--;   // trường ngoài UpdateVideoRequest bị bỏ qua, giống binder JSON mặc định
                    break;
            }
        }

        if (count == 0 && errors.Count == 0) Fail("$", "At least one property is required (minProperties: 1).");
        if (errors.Count > 0) throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "One or more fields are invalid.", errors);

        return new VideoPatch
        {
            Title = title, Summary = summary, MediaId = mediaId, ContentKind = contentKind, Sponsorship = sponsorship, ChangeReason = changeReason,
        };
    }

    private static VideoSponsorshipInput? ParseSponsorship(JsonElement v, Action<string, string> fail)
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

        var result = new VideoSponsorshipInput(Text("sponsorName", 180)!, Text("sponsorReference", 120)!, Text("evidenceRef", 300)!, Time("validFrom"), Time("validTo"));
        return result.SponsorName is null || result.SponsorReference is null || result.EvidenceRef is null || result.ValidFrom is null || result.ValidTo is null ? null : result;
    }
}

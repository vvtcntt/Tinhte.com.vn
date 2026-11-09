using System.Text.Json;

namespace Tinhte.Modules.News.Application;

/// <summary>
/// So hai revision (RevisionDetail.diff, FR-CMS-004): từng trường nội dung, bodyBlocks so theo vị trí block.
/// Đủ cho màn so sánh phiên bản của CMS; diff theo từ/ký tự trong block là việc của UI (WBS-030).
/// </summary>
public static class RevisionDiffer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RevisionDiff Compare(ArticleRevision from, ArticleRevision to)
    {
        var changes = new List<RevisionChange>();
        void Field<T>(string path, T before, T after)
        {
            var b = before is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(before, Json);
            var a = after is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(after, Json);
            if (!Same(b, a)) changes.Add(new RevisionChange(path, b, a));
        }

        Field("/title", from.Title, to.Title);
        Field("/summary", from.Summary, to.Summary);
        Field("/categoryId", from.CategoryId, to.CategoryId);
        Field("/contentKind", from.ContentKind, to.ContentKind);
        Field("/sponsorship", from.Sponsorship, to.Sponsorship);
        Field("/changeReason", from.ChangeReason, to.ChangeReason);

        var before = Blocks(from.BodyBlocks);
        var after = Blocks(to.BodyBlocks);
        for (var i = 0; i < Math.Max(before.Count, after.Count); i++)
        {
            JsonElement? b = i < before.Count ? before[i] : null;
            JsonElement? a = i < after.Count ? after[i] : null;
            if (!Same(b, a)) changes.Add(new RevisionChange($"/bodyBlocks/{i}", b, a));
        }

        return new RevisionDiff(from.RevisionId, changes);
    }

    private static List<JsonElement> Blocks(JsonElement? blocks) =>
        blocks is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToList() : [];

    private static bool Same(JsonElement? a, JsonElement? b) =>
        a is null || b is null ? a is null && b is null : JsonElement.DeepEquals(a.Value, b.Value);
}

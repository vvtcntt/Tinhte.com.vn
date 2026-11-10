using Microsoft.Extensions.Options;
using Tinhte.Modules.News.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Flags;

namespace Tinhte.Modules.News.Application;

/// <summary>Vị trí cursor của danh sách bài công khai: sắp (publishedAt DESC, id DESC).</summary>
public sealed record PublicArticlePosition(DateTimeOffset PublishedAt, Guid Id);

/// <summary>Chuyên mục công khai (đang bật) của site.</summary>
public sealed record PublicCategory(Guid Id, string Slug, string Name);

/// <summary>
/// listPublicArticles / getCategoriesSlugArticles (FR-DISC-002/004, FR-ID-001, WBS-028/051): chỉ bài đang hiển thị công khai
/// của site trong Host (cùng luật <see cref="PublicArticleSql.Visible"/> với trang bài và trang chủ), mới nhất trước.
/// </summary>
public sealed class PublicArticleListService(ITinhteDatabase database, IOptions<NewsOptions> options)
{
    /// <summary>Chuyên mục đang bật của site; không có / đã tắt / site khác → null.</summary>
    public async Task<PublicCategory?> FindCategoryAsync(string siteKey, string slug, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = ArticleSql.Command(connection, null,
            "SELECT id, slug, name FROM tt.categories WHERE slug = @slug AND site_key = @site AND is_enabled")
            .WithText("slug", slug).WithText("site", siteKey);
        await using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PublicCategory(r.GetGuid(0), r.GetString(1), r.GetString(2)) : null;
    }

    /// <summary>Lấy <paramref name="take"/> bài sau <paramref name="after"/>; <paramref name="categorySlug"/> lọc theo chuyên mục đang bật.</summary>
    public async Task<IReadOnlyList<PublicArticleSummary>> ListAsync(string siteKey, string? categorySlug, PublicArticlePosition? after, int take, CancellationToken ct)
    {
        var label = options.Value.SponsoredLabel;
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = ArticleSql.Command(connection, null, $"""
            SELECT {PublicArticleSql.SummaryColumns}
              FROM tt.articles a
              {PublicArticleSql.JoinPublicRevision}
             WHERE a.primary_site_key = @site AND {PublicArticleSql.Visible}
               AND (@category::text IS NULL OR EXISTS (
                     SELECT 1 FROM tt.categories c
                      WHERE c.id = a.category_id AND c.slug = @category AND c.site_key = @site AND c.is_enabled))
               AND (@afterAt::timestamptz IS NULL OR (a.published_at, a.id) < (@afterAt, @afterId))
             ORDER BY a.published_at DESC, a.id DESC
             LIMIT @take
            """)
            .WithText("site", siteKey)
            .WithText("flag", FeatureFlagKeys.AdsSponsoredPublication)
            .WithText("category", categorySlug)
            .WithTime("afterAt", after?.PublishedAt)
            .WithUuid("afterId", after?.Id ?? Guid.Empty);
        command.Parameters.AddWithValue("sponsorLabel", !string.IsNullOrWhiteSpace(label));
        command.Parameters.AddWithValue("take", take);
        var items = new List<PublicArticleSummary>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(PublicArticleSql.ReadSummary(r, label));
        return items;
    }

    public static DomainProblemException CategoryNotFound() =>
        new(ErrorCodes.NotFound, "Category not found", "No enabled category with this slug on this site.");
}

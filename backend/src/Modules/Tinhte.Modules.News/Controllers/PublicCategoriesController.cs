using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Tinhte.Modules.News.Application;
using Tinhte.Platform.Data;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.News.Controllers;

/// <summary>getCategories (tag Discovery; FR-NEWS-003): chuyên mục đang bật của site trong Host, sắp theo tên.</summary>
[Route(ApiRoutes.Prefix + "/categories")]
[Tags("Discovery")]
public sealed class PublicCategoriesController(ITinhteDatabase database, PublicArticleListService list, ICursorCodec cursors) : TinhteControllerBase
{

    /// <summary>Schema Category.</summary>
    public sealed record Category(Guid Id, string Slug, string Name, bool IsEnabled, long RowVersion);

    /// <summary>Schema CategoryList.</summary>
    public sealed record CategoryList(IReadOnlyList<Category> Items);

    [HttpGet(Name = "getCategories")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<CategoryList>> List(CancellationToken cancellationToken)
    {
        await using var c = await database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, slug, name, is_enabled, row_version FROM tt.categories
             WHERE site_key = @site AND is_enabled
             ORDER BY name, id
            """, c);
        command.Parameters.AddWithValue("site", Site.Key);
        var items = new List<Category>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new Category(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4)));
        }

        return Ok(new CategoryList(items));
    }

    /// <summary>
    /// getCategoriesSlugArticles (FR-DISC-002, WBS-028): bài đang công khai của chuyên mục, mới nhất trước, phân trang cursor.
    /// Chuyên mục không có / đã tắt / thuộc site khác → 404.
    /// </summary>
    [HttpGet("{slug}/articles", Name = "getCategoriesSlugArticles")]
    [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<CursorPage<PublicArticleSummary>>> Articles(
        [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$"), StringLength(120)] string slug,
        [FromQuery] PageRequest page,
        CancellationToken cancellationToken)
    {
        _ = await list.FindCategoryAsync(Site.Key, slug, cancellationToken) ?? throw PublicArticleListService.CategoryNotFound();
        var scope = CursorScope("category-articles", slug);
        var after = page.Cursor is { } cursor ? cursors.Decode<PublicArticlePosition>(scope, cursor) : null;
        var rows = await list.ListAsync(Site.Key, slug, after, page.Limit + 1, cancellationToken);
        return Ok(rows.ToPage(page.Limit, cursors, scope, a => new PublicArticlePosition(a.PublishedAt, a.Id)));
    }
}

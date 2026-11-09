using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Tinhte.Platform.Data;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.News.Controllers;

/// <summary>getCategories (tag Discovery; FR-NEWS-003): chuyên mục đang bật của site trong Host, sắp theo tên.</summary>
[Route(ApiRoutes.Prefix + "/categories")]
[Tags("Discovery")]
public sealed class PublicCategoriesController(ITinhteDatabase database) : TinhteControllerBase
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
}

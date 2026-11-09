using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Tinhte.Platform.Data;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.News.Controllers;

/// <summary>
/// getAdminCategories (tag CmsTaxonomy): danh sách chuyên mục cho form bài viết trong CMS.
/// Chỉ phần ĐỌC — tạo/sửa chuyên mục (postAdminCategories, …/{categoryId}) thuộc WBS-023/032.
/// </summary>
[Route(ApiRoutes.Prefix + "/admin/categories")]
public sealed class AdminCategoriesController(ITinhteDatabase database) : TinhteControllerBase
{
    /// <summary>Schema Category.</summary>
    public sealed record Category(Guid Id, string Slug, string Name, bool IsEnabled, long RowVersion);

    /// <summary>Schema CategoryList.</summary>
    public sealed record CategoryList(IReadOnlyList<Category> Items);

    [HttpGet(Name = "getAdminCategories")]
    [RequirePermission(Permissions.ContentRead)]
    public async Task<ActionResult<CategoryList>> List(CancellationToken cancellationToken)
    {
        await using var c = await database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id, slug, name, is_enabled, row_version FROM tt.categories ORDER BY name, id", c);
        var items = new List<Category>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new Category(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4)));
        }

        return Ok(new CategoryList(items));
    }
}

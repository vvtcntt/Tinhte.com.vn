using System.Data.Common;
using Tinhte.Modules.News.Application;

namespace Tinhte.Modules.News.Infrastructure;

/// <summary>
/// Chuyên mục trên thẻ bài công khai (ArticleSummary.category — DRAFT v0.3, đợt 3 trang chủ). Alias bắt buộc: <c>a</c> = tt.articles.
/// Lấy <c>tt.articles.category_id</c> (chuyên mục theo bản đã xuất bản — cùng cột mà danh sách theo chuyên mục lọc), chỉ khi chuyên
/// mục đang bật và cùng site với bài; ngược lại null. Alias <c>cat</c> để không đụng alias <c>c</c> của truy vấn khác.
/// </summary>
internal static class PublicArticleCategorySql
{
    /// <summary>Ba cột liên tiếp, đọc bằng <see cref="Read"/>.</summary>
    public const string Columns = "cat.id, cat.slug, cat.name";

    public const string Join = "LEFT JOIN tt.categories cat ON cat.id = a.category_id AND cat.site_key = a.primary_site_key AND cat.is_enabled";

    public static PublicCategoryRef? Read(DbDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : new PublicCategoryRef(r.GetGuid(ordinal), r.GetString(ordinal + 1), r.GetString(ordinal + 2));
}

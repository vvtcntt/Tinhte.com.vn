using Microsoft.Extensions.Hosting;
using Npgsql;
using Tinhte.Platform.Data.Migrations;

namespace Tinhte.DbMigrator;

/// <summary>
/// Lối tắt CHỈ cho máy dev. Hợp đồng v0.3 chưa có cách xác nhận quyền nội dung của bài (nguồn/giấy phép — WBS-023,
/// đề xuất ở ADR-0007 chờ TL/PO/Legal/DBA), nên publish luôn bị 422 RIGHTS_INVALID. Lệnh này đặt
/// <c>rights_status = CLEARED</c> để thử luồng xuất bản trên máy dev. Ngoài Development lệnh từ chối chạy:
/// trên staging/production quyền chỉ được xác nhận qua quy trình đã duyệt — không bao giờ "coi giấy phép là đã có".
/// </summary>
internal static class DevShortcuts
{
    /// <summary>
    /// Nạp dữ liệu mẫu (DevSeed.sql): user, chuyên mục, 20 bài ở đủ trạng thái, tin nổi bật trang chủ. Idempotent.
    /// CHỈ Development — không bao giờ đưa dữ liệu giả lên staging/production.
    /// </summary>
    public static async Task<string> SeedAsync(NpgsqlDataSource dataSource, IHostEnvironment env, MigrationRunner runner)
    {
        if (!env.IsDevelopment())
        {
            throw new MigrationException("seed-dev is a Development-only command: sample content must never reach staging or production.");
        }

        var path = Path.Combine(AppContext.BaseDirectory, "DevSeed.sql");
        if (!File.Exists(path)) throw new MigrationException($"seed file not found: {path}");
        await runner.RunInTransactionAsync(await File.ReadAllTextAsync(path));

        await using var c = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM tt.articles WHERE id::text LIKE '00000000-0000-4000-8003-%'),
                   (SELECT count(*) FROM tt.articles WHERE id::text LIKE '00000000-0000-4000-8003-%' AND status = 'PUBLISHED'),
                   (SELECT count(*) FROM tt.home_featured_articles WHERE site_key = 'www')
            """, c);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return $"dev seed: {reader.GetInt64(0)} sample articles ({reader.GetInt64(1)} published), {reader.GetInt64(2)} home pins on site www — DEV ONLY";
    }

    public static async Task<string> ClearRightsAsync(NpgsqlDataSource dataSource, IHostEnvironment env, string? article, string? expires)
    {
        if (!env.IsDevelopment())
        {
            throw new MigrationException("clear-rights is a Development-only shortcut. Outside Development, content rights must be verified through the approved rights workflow (WBS-023).");
        }

        if (string.IsNullOrWhiteSpace(article))
        {
            throw new MigrationException("clear-rights requires --article <article id or slug> [--expires <ISO date-time>].");
        }

        DateTimeOffset? until = null;
        if (!string.IsNullOrWhiteSpace(expires))
        {
            if (!DateTimeOffset.TryParse(expires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                throw new MigrationException($"--expires '{expires}' is not a date-time (e.g. 2027-01-01T00:00:00Z).");
            }

            until = parsed.ToUniversalTime();
        }

        await using var c = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE tt.articles SET rights_status = 'CLEARED', rights_expires_at = @expires
             WHERE id::text = @article OR slug = @article
            RETURNING id, slug, row_version
            """, c);
        command.Parameters.AddWithValue("article", article.Trim());
        command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)until ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new MigrationException($"no article with id or slug '{article}'.");
        return $"rights CLEARED{(until is { } u ? $" until {u:u}" : "")} for article {reader.GetGuid(0)} ({reader.GetString(1)}), row_version {reader.GetInt64(2)} — DEV ONLY";
    }
}

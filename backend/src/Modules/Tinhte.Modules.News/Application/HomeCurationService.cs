using Microsoft.Extensions.Options;
using Npgsql;
using Tinhte.Modules.News.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Flags;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Sites;

namespace Tinhte.Modules.News.Application;

/// <summary>Schema HomeFeaturedArticle.</summary>
public sealed record HomeFeaturedArticle(int Position, Guid ArticleId, string Slug, string? Title, bool IsPublic);

/// <summary>Schema HomeFeaturedArticles. Site chưa từng ghim: danh sách rỗng, rowVersion 1.</summary>
public sealed record HomeFeaturedArticles(string SiteKey, IReadOnlyList<HomeFeaturedArticle> Items, long RowVersion, DateTimeOffset? UpdatedAt, Guid? UpdatedBy);

/// <summary>
/// Biên tập tin nổi bật trang chủ theo site (getAdminHomeFeaturedArticles / putAdminHomeFeaturedArticles; FR-DISC-003, migration 0322).
/// Thay cả danh sách có thứ tự; lịch sử (người, lúc, ảnh chụp) do trigger 0322 ghi.
/// </summary>
public sealed class HomeCurationService(ITinhteDatabase database, SiteRegistry sites, IOptions<NewsOptions> options)
{
    public const int MaxItems = 10;

    /// <summary>Chỉ site public-web phân giải được trong môi trường hiện tại; ngược lại 404.</summary>
    public SiteDefinition RequireSite(string siteKey) =>
        sites.ResolvableSites.FirstOrDefault(s => s.Key == siteKey && s.Kind == "public-web")
        ?? throw new DomainProblemException(ErrorCodes.NotFound, "Site not found", $"'{siteKey}' is not a public site of the Site Registry.");

    public async Task<HomeFeaturedArticles> GetAsync(string siteKey, CancellationToken ct)
    {
        RequireSite(siteKey);
        await using var connection = await database.OpenConnectionAsync(ct);
        return await ReadAsync(connection, null, siteKey, ct);
    }

    public Task<HomeFeaturedArticles> ReplaceAsync(Guid actorId, string siteKey, IReadOnlyList<Guid> articleIds, long expectedRowVersion, CancellationToken ct)
    {
        RequireSite(siteKey);
        if (articleIds.Count > MaxItems || articleIds.Distinct().Count() != articleIds.Count)
        {
            throw Invalid("articleIds", $"articleIds must contain at most {MaxItems} distinct article ids.");
        }

        return database.InTransactionAsync(actorId, async (c, tx) =>
        {
            // Dòng layout là "phiên bản" của danh sách: tạo nếu chưa có (row_version 1), rồi khóa để so If-Match.
            await Exec(c, tx, "INSERT INTO tt.home_layouts (site_key) VALUES (@site) ON CONFLICT (site_key) DO NOTHING", ct, siteKey);
            await using (var lockCmd = ArticleSql.Command(c, tx, "SELECT row_version FROM tt.home_layouts WHERE site_key = @site FOR UPDATE").WithText("site", siteKey))
            {
                ETags.EnsureMatches((long)(await lockCmd.ExecuteScalarAsync(ct))!, expectedRowVersion);
            }

            await EnsurePublishableAsync(c, tx, siteKey, articleIds, ct);

            await Exec(c, tx, "DELETE FROM tt.home_featured_articles WHERE site_key = @site", ct, siteKey);
            await using (var insert = ArticleSql.Command(c, tx, """
                INSERT INTO tt.home_featured_articles (site_key, article_id, position)
                SELECT @site, id, ord FROM unnest(@ids) WITH ORDINALITY AS t(id, ord)
                """).WithText("site", siteKey))
            {
                insert.Parameters.AddWithValue("ids", articleIds.ToArray());
                await insert.ExecuteNonQueryAsync(ct);
            }

            // Tăng row_version (trigger tg_touch_row) → trigger AFTER ghi ảnh chụp vào tt.home_featured_history.
            await using (var touch = ArticleSql.Command(c, tx, "UPDATE tt.home_layouts SET updated_by = @actor WHERE site_key = @site")
                .WithText("site", siteKey).WithUuid("actor", actorId))
            {
                await touch.ExecuteNonQueryAsync(ct);
            }

            return await ReadAsync(c, tx, siteKey, ct);
        }, ct);
    }

    /// <summary>Mọi bài phải thuộc site và đang hiển thị công khai lúc lưu (không ghim bài nháp/đã gỡ/khác site).</summary>
    private async Task EnsurePublishableAsync(NpgsqlConnection c, NpgsqlTransaction tx, string siteKey, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await using var command = ArticleSql.Command(c, tx, $"""
            SELECT a.id FROM tt.articles a
              JOIN tt.article_revisions r ON r.id = a.public_revision_id AND r.article_id = a.id
             WHERE a.id = ANY(@ids) AND a.primary_site_key = @site AND {PublicArticleSql.Visible}
            """).WithText("site", siteKey).WithText("flag", FeatureFlagKeys.AdsSponsoredPublication);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        command.Parameters.AddWithValue("sponsorLabel", !string.IsNullOrWhiteSpace(options.Value.SponsoredLabel));
        var ok = new HashSet<Guid>();
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) ok.Add(r.GetGuid(0));
        }

        var errors = ids.Select((id, i) => (id, i)).Where(x => !ok.Contains(x.id))
            .ToDictionary(x => $"articleIds[{x.i}]", x => new[] { $"Article {x.id} is not publicly visible on site '{siteKey}'." });
        if (errors.Count > 0)
        {
            throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed",
                "Only published, publicly visible articles of this site can be featured.", errors);
        }
    }

    private async Task<HomeFeaturedArticles> ReadAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string siteKey, CancellationToken ct)
    {
        long rowVersion = 1;
        DateTimeOffset? updatedAt = null;
        Guid? updatedBy = null;
        await using (var layout = ArticleSql.Command(c, tx, "SELECT row_version, updated_at, updated_by FROM tt.home_layouts WHERE site_key = @site").WithText("site", siteKey))
        await using (var r = await layout.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                rowVersion = r.GetInt64(0);
                updatedAt = ArticleSql.Time(r, 1);
                updatedBy = ArticleSql.NullableGuid(r, 2);
            }
        }

        await using var command = ArticleSql.Command(c, tx, $"""
            SELECT f.position, a.id, a.slug, r.title, COALESCE({PublicArticleSql.Visible}, false)
              FROM tt.home_featured_articles f
              JOIN tt.articles a ON a.id = f.article_id
              LEFT JOIN tt.article_revisions r ON r.id = a.public_revision_id AND r.article_id = a.id
             WHERE f.site_key = @site
             ORDER BY f.position
            """).WithText("site", siteKey).WithText("flag", FeatureFlagKeys.AdsSponsoredPublication);
        command.Parameters.AddWithValue("sponsorLabel", !string.IsNullOrWhiteSpace(options.Value.SponsoredLabel));
        var items = new List<HomeFeaturedArticle>();
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                items.Add(new HomeFeaturedArticle(r.GetInt16(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4)));
            }
        }

        return new HomeFeaturedArticles(siteKey, items, rowVersion, updatedAt, updatedBy);
    }

    private static async Task Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, string siteKey)
    {
        await using var command = ArticleSql.Command(c, tx, sql).WithText("site", siteKey);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DomainProblemException Invalid(string field, string message) =>
        new(ErrorCodes.ValidationFailed, "Validation failed", message, new Dictionary<string, string[]> { [field] = [message] });
}

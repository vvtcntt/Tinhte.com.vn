using System.Data.Common;
using Microsoft.Extensions.Options;
using Npgsql;
using Tinhte.Modules.News.Domain;
using Tinhte.Modules.News.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Flags;

namespace Tinhte.Modules.News.Application;

/// <summary>Schema ArticleSummary (thẻ bài công khai).</summary>
public sealed record PublicArticleSummary(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    DateTimeOffset PublishedAt,
    string? ThumbnailUrl,
    string ContentKind,
    string? SponsorLabel)
{
    /// <summary>ArticleSummary.category (DRAFT đợt 3): init-only để nơi khác dựng thẻ bài không phải đổi chữ ký; null = không hiện.</summary>
    public PublicCategoryRef? Category { get; init; }
}

/// <summary>Schema CategoryRef (chuyên mục tối thiểu trên thẻ bài).</summary>
public sealed record PublicCategoryRef(Guid Id, string Slug, string Name);

/// <summary>Schema HomePage. featuredVideos: thuộc module video (WBS-041/046) — tạm rỗng tới khi có feed video công khai.</summary>
public sealed record HomePage(
    IReadOnlyList<PublicArticleSummary> FeaturedArticles,
    IReadOnlyList<PublicArticleSummary> RecentArticles,
    IReadOnlyList<object> FeaturedVideos);

/// <summary>
/// getHome (FR-DISC-003, WBS-027/051): chỉ nội dung hiển thị công khai của site trong Host (ADR-0001 D8).
/// Tin nổi bật = bài biên tập ghim (tt.home_featured_articles) theo position, bỏ bài không còn hiển thị (FR-DISC-004),
/// thiếu thì bù bằng bài mới nhất. Tin mới = bài mới nhất không trùng Tin nổi bật.
/// </summary>
public sealed class HomeService(ITinhteDatabase database, IOptions<NewsOptions> options)
{
    public const int FeaturedLimit = 5;
    public const int RecentLimit = 12;

    private const string Columns = "a.id, a.slug, r.title, r.summary, a.published_at, r.content_kind, " + PublicArticleCategorySql.Columns;
    private const string Join = "JOIN tt.article_revisions r ON r.id = a.public_revision_id AND r.article_id = a.id " + PublicArticleCategorySql.Join;

    private const string PickedSql = $"""
        SELECT {Columns}
          FROM tt.home_featured_articles f
          JOIN tt.articles a ON a.id = f.article_id
          {Join}
         WHERE f.site_key = @site AND a.primary_site_key = @site AND {PublicArticleSql.Visible}
         ORDER BY f.position
         LIMIT @limit
        """;

    private const string LatestSql = $"""
        SELECT {Columns}
          FROM tt.articles a
          {Join}
         WHERE a.primary_site_key = @site AND {PublicArticleSql.Visible} AND NOT (a.id = ANY(@exclude))
         ORDER BY a.published_at DESC, a.id DESC
         LIMIT @limit
        """;

    public async Task<HomePage> GetAsync(string siteKey, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        var picked = await ReadAsync(connection, PickedSql, siteKey, FeaturedLimit, [], ct);
        var latest = await ReadAsync(connection, LatestSql, siteKey, FeaturedLimit - picked.Count + RecentLimit, picked.Select(a => a.Id).ToArray(), ct);
        var topUp = FeaturedLimit - picked.Count;
        return new HomePage([.. picked, .. latest.Take(topUp)], [.. latest.Skip(topUp)], []);
    }

    private async Task<List<PublicArticleSummary>> ReadAsync(NpgsqlConnection connection, string sql, string siteKey, int limit, Guid[] exclude, CancellationToken ct)
    {
        var label = options.Value.SponsoredLabel;
        await using var command = ArticleSql.Command(connection, null, sql)
            .WithText("site", siteKey)
            .WithText("flag", FeatureFlagKeys.AdsSponsoredPublication);
        command.Parameters.AddWithValue("sponsorLabel", !string.IsNullOrWhiteSpace(label));
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("exclude", exclude);
        var items = new List<PublicArticleSummary>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(Read(r, label));
        return items;
    }

    private static PublicArticleSummary Read(DbDataReader r, string? label)
    {
        var kind = r.GetString(5);
        return new PublicArticleSummary(
            Id: r.GetGuid(0),
            Slug: r.GetString(1),
            Title: r.GetString(2),
            Summary: r.IsDBNull(3) ? null : r.GetString(3),
            PublishedAt: ArticleSql.Time(r, 4),
            ThumbnailUrl: null,                 // ảnh đại diện: thuộc WBS media
            ContentKind: kind,
            SponsorLabel: kind == ContentKinds.Sponsored ? label : null) { Category = PublicArticleCategorySql.Read(r, 6) };
    }
}

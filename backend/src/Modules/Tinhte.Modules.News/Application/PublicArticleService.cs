using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tinhte.Modules.News.Domain;
using Tinhte.Modules.News.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Flags;

namespace Tinhte.Modules.News.Application;

public sealed class NewsOptions
{
    public const string Section = "News";

    /// <summary>
    /// Nhãn công khai của nội dung tài trợ (ArticleSummary.sponsorLabel). Câu chữ do Legal duyệt (AIR-011) — chưa có thì
    /// để trống: bài SPONSORED không được phục vụ công khai (fail-closed), kể cả khi cờ ads.sponsored_publication ON.
    /// </summary>
    public string? SponsoredLabel { get; set; }
}

/// <summary>Kết quả getPublicArticle: bài, redirect 308 tới slug hiện tại, hoặc lỗi.</summary>
public abstract record PublicArticleResult
{
    public sealed record Found(PublicArticle Article) : PublicArticleResult;

    public sealed record Moved(string Slug) : PublicArticleResult;
}

/// <summary>
/// Đọc bài công khai (getPublicArticle; FR-NEWS-001/002/008, FR-DISC-007): CHỈ nội dung của public revision,
/// chỉ bài thuộc site của Host (primary_site_key — ADR-0001 D8).
/// <list type="bullet">
///   <item>Hiển thị khi có publish pointer, bài không UNPUBLISHED/RETRACTED, quyền CLEARED còn hạn. Không dựa vào
///         <c>status = PUBLISHED</c>: bài đang public vẫn hiển thị bản cũ trong lúc bản nháp mới đi duyệt.</item>
///   <item>Từng public nhưng đã gỡ (UNPUBLISHED/RETRACTED) hoặc hết quyền → 410 CONTENT_WITHDRAWN.</item>
///   <item>SPONSORED chỉ hiển thị khi cờ ads.sponsored_publication ON (đọc tt.feature_gates — cùng nguồn với trigger 0307),
///         tài trợ đã duyệt, đang trong cửa sổ hiệu lực và đã cấu hình nhãn → ngược lại 404.</item>
///   <item>Slug cũ (tt.article_slug_redirects) → 308 tới slug hiện tại nếu bài đích đang hiển thị được.</item>
/// </list>
/// </summary>
public sealed partial class PublicArticleService(ITinhteDatabase database, IOptions<NewsOptions> options, ILogger<PublicArticleService> logger)
{
    private const string Query = """
        SELECT a.id, a.slug, a.status, a.published_at, a.created_at,
               a.rights_status = 'CLEARED' AND (a.rights_expires_at IS NULL OR a.rights_expires_at > now()) AS rights_ok,
               r.id, r.title, r.summary, r.body::text, r.content_kind, r.published_at,
               r.content_kind <> 'SPONSORED' OR (
                   COALESCE((SELECT g.enabled FROM tt.feature_gates g WHERE g.key = @flag), false)
                   AND r.sponsorship_approved_at IS NOT NULL
                   AND now() >= r.sponsorship_valid_from AND now() < r.sponsorship_valid_to) AS sponsor_ok,
               c.slug, u.display_name
          FROM tt.articles a
          JOIN tt.article_revisions r ON r.id = a.public_revision_id AND r.article_id = a.id
          LEFT JOIN tt.categories c ON c.id = r.category_id
          LEFT JOIN tt.users u ON u.id = a.owner_id
        """;

    public async Task<PublicArticleResult> GetAsync(string siteKey, string slug, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        if (await ReadAsync(connection, siteKey, slug, ct) is { } article) return new PublicArticleResult.Found(article);

        // Slug cũ → 308 về slug hiện tại, chỉ khi bài đích hiển thị được (không lộ bài nháp/đã gỡ qua redirect).
        await using (var redirect = ArticleSql.Command(connection, null, """
            SELECT a.slug FROM tt.article_slug_redirects s JOIN tt.articles a ON a.id = s.article_id
             WHERE s.old_slug = @slug AND a.primary_site_key = @site
            """).WithText("slug", slug).WithText("site", siteKey))
        {
            if (await redirect.ExecuteScalarAsync(ct) is string current && current != slug)
            {
                try
                {
                    if (await ReadAsync(connection, siteKey, current, ct) is not null) return new PublicArticleResult.Moved(current);
                }
                catch (DomainProblemException e) when (e.Code is ErrorCodes.NotFound or ErrorCodes.ContentWithdrawn)
                {
                    // bài đích không hiển thị → 404 cho slug cũ
                }
            }
        }

        throw NotFound();
    }

    /// <summary>null: không có bài nào mang slug này trên site; có bài nhưng không hiển thị được → ném 404/410.</summary>
    private async Task<PublicArticle?> ReadAsync(Npgsql.NpgsqlConnection connection, string siteKey, string slug, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, null, Query + " WHERE a.slug = @slug AND a.primary_site_key = @site")
            .WithText("slug", slug).WithText("site", siteKey).WithText("flag", FeatureFlagKeys.AdsSponsoredPublication);
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        var status = r.GetString(2);
        var wasPublic = !r.IsDBNull(3);
        if (status is ArticleStatuses.Unpublished or ArticleStatuses.Retracted || !r.GetBoolean(5))
        {
            throw wasPublic ? Withdrawn() : NotFound();
        }

        if (!wasPublic) throw NotFound();   // có con trỏ nhưng chưa qua publish (dữ liệu cũ chưa backfill)
        var kind = r.GetString(10);
        string? label = null;
        if (kind == ContentKinds.Sponsored)
        {
            label = options.Value.SponsoredLabel;
            if (!r.GetBoolean(12)) throw NotFound();
            if (string.IsNullOrWhiteSpace(label))
            {
                LogMissingSponsorLabel(logger, NewsOptions.Section);
                throw NotFound();
            }
        }

        return new PublicArticle(
            Id: r.GetGuid(0),
            Slug: r.GetString(1),
            Title: r.GetString(7),
            Summary: r.IsDBNull(8) ? null : r.GetString(8),
            PublishedAt: ArticleSql.Time(r, 3),
            ThumbnailUrl: null,                 // ảnh đại diện: thuộc WBS media
            ContentKind: kind,
            SponsorLabel: label,
            RevisionId: r.GetGuid(6),
            AuthorDisplayName: r.IsDBNull(14) ? null : r.GetString(14),
            CategorySlug: r.IsDBNull(13) ? null : r.GetString(13),
            BodyBlocks: ArticleSql.ParseBody(r.IsDBNull(9) ? null : r.GetString(9)),
            RelatedVideoIds: []);               // liên kết bài–video: FR-DISC-005/FR-VID-009, WBS khác
    }

    private static DomainProblemException NotFound() => new(ErrorCodes.NotFound, "Article not found", "No published article with this slug.");

    private static DomainProblemException Withdrawn() =>
        new(ErrorCodes.ContentWithdrawn, "Content withdrawn", "This article is no longer available.");

    [LoggerMessage(EventId = 5201, Level = LogLevel.Warning, Message = "Sponsored article hidden: {Section}:SponsoredLabel (Legal-approved text) is not configured")]
    private static partial void LogMissingSponsorLabel(ILogger logger, string section);
}

using System.Data.Common;
using Tinhte.Modules.News.Application;
using Tinhte.Modules.News.Domain;

namespace Tinhte.Modules.News.Infrastructure;

/// <summary>
/// Điều kiện "bài đang hiển thị công khai" dùng chung cho getPublicArticle, getHome và màn ghim tin nổi bật
/// (FR-NEWS-001/008, FR-DISC-004). Alias bắt buộc: <c>a</c> = tt.articles, <c>r</c> = public revision
/// (<c>r.id = a.public_revision_id AND r.article_id = a.id</c>). Tham số: <c>@flag</c> = khóa cờ ads.sponsored_publication.
/// </summary>
internal static class PublicArticleSql
{
    /// <summary>Tài trợ chỉ hiển thị khi cờ ON (tt.feature_gates — cùng nguồn với trigger 0307), đã duyệt và trong cửa sổ hiệu lực.</summary>
    public const string SponsorOk = """
        (r.content_kind <> 'SPONSORED' OR (
            COALESCE((SELECT g.enabled FROM tt.feature_gates g WHERE g.key = @flag), false)
            AND r.sponsorship_approved_at IS NOT NULL
            AND now() >= r.sponsorship_valid_from AND now() < r.sponsorship_valid_to))
        """;

    public const string RightsOk = "(a.rights_status = 'CLEARED' AND (a.rights_expires_at IS NULL OR a.rights_expires_at > now()))";

    /// <summary>
    /// Đã qua publish, chưa gỡ, quyền còn hạn, tài trợ hợp lệ. Thêm <c>@sponsorLabel</c> (bool): chưa cấu hình nhãn Legal duyệt
    /// thì bài SPONSORED không hiển thị (fail-closed, AIR-011). Không dựa vào <c>status = PUBLISHED</c>: bài đang public vẫn hiển thị
    /// bản cũ trong lúc bản nháp mới đi duyệt.
    /// </summary>
    public const string Visible = $"""
        (a.published_at IS NOT NULL AND a.status NOT IN ('UNPUBLISHED', 'RETRACTED') AND {RightsOk} AND {SponsorOk}
         AND (r.content_kind <> 'SPONSORED' OR @sponsorLabel))
        """;

    /// <summary>Cột của thẻ bài công khai, đọc bằng <see cref="ReadSummary"/>.</summary>
    public const string SummaryColumns = "a.id, a.slug, r.title, r.summary, a.published_at, r.content_kind";

    public const string JoinPublicRevision = "JOIN tt.article_revisions r ON r.id = a.public_revision_id AND r.article_id = a.id";

    /// <summary>Schema ArticleSummary. Nhãn tài trợ chỉ gắn cho SPONSORED (đã lọc bằng <see cref="Visible"/>).</summary>
    public static PublicArticleSummary ReadSummary(DbDataReader r, string? sponsorLabel)
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
            SponsorLabel: kind == ContentKinds.Sponsored ? sponsorLabel : null);
    }
}

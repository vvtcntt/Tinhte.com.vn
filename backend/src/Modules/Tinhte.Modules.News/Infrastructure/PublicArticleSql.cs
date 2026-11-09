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
}

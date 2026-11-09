using System.Text;
using System.Text.RegularExpressions;

namespace Tinhte.Modules.News.Domain;

/// <summary>ContentStatus của hợp đồng v0.3 (trạng thái bài) — khớp CHECK ck_articles_status (0306).</summary>
public static class ArticleStatuses
{
    public const string Draft = "DRAFT";
    public const string InReview = "IN_REVIEW";
    public const string ChangesRequested = "CHANGES_REQUESTED";
    public const string Rejected = "REJECTED";
    public const string Approved = "APPROVED";
    public const string Scheduled = "SCHEDULED";
    public const string Published = "PUBLISHED";
    public const string Unpublished = "UNPUBLISHED";
    public const string Retracted = "RETRACTED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Draft, InReview, ChangesRequested, Rejected, Approved, Scheduled, Published, Unpublished, Retracted,
    };

    /// <summary>
    /// Trạng thái cho phép sửa bài qua PATCH. Đang chờ duyệt / đã duyệt / đã lên lịch / đã rút: phải qua workflow (WBS-024/025)
    /// trước — PATCH trả 409 STATE_CONFLICT để không âm thầm vô hiệu một quyết định duyệt.
    /// </summary>
    public static readonly IReadOnlySet<string> Editable = new HashSet<string>(StringComparer.Ordinal)
    {
        Draft, ChangesRequested, Rejected, Published, Unpublished,
    };
}

/// <summary>Vòng đời revision (cột tt.article_revisions.status, migration 0320).</summary>
public static class RevisionStatuses
{
    public const string Draft = "DRAFT";
    public const string InReview = "IN_REVIEW";
    public const string ChangesRequested = "CHANGES_REQUESTED";
    public const string Rejected = "REJECTED";
    public const string Approved = "APPROVED";
    public const string Published = "PUBLISHED";
    public const string Superseded = "SUPERSEDED";
}

public static class ContentKinds
{
    public const string Editorial = "EDITORIAL";
    public const string Sponsored = "SPONSORED";
    public const string Pattern = "^(EDITORIAL|SPONSORED)$";
}

/// <summary>Slug bài (FR-NEWS-002): <c>^[a-z0-9]+(?:-[a-z0-9]+)*$</c>, tối đa 250 ký tự (ArticlePatchRequest.slug, CHECK ck_articles_slug_format).</summary>
public static partial class ArticleSlug
{
    public const int MaxLength = 250;
    public const string Pattern = "^[a-z0-9]+(?:-[a-z0-9]+)*$";

    /// <summary>Phần slug sinh từ tiêu đề được cắt ở đây, chừa chỗ cho hậu tố chống trùng.</summary>
    private const int MaxGeneratedLength = 200;

    public static bool IsValid(string? slug) => slug is { Length: > 0 and <= MaxLength } && SlugRegex().IsMatch(slug);

    /// <summary>
    /// Sinh slug từ tiêu đề tiếng Việt: bỏ dấu (bảng tự viết — app chạy InvariantGlobalization), chữ thường, ký tự khác → '-'.
    /// Tiêu đề không còn chữ/số nào trả "bai-viet".
    /// </summary>
    public static string FromTitle(string title)
    {
        var sb = new StringBuilder(title.Length);
        var pendingDash = false;
        foreach (var ch in title)
        {
            // Dấu tổ hợp (văn bản dạng NFD, ví dụ gõ trên macOS): bỏ, chữ gốc đứng trước đã được giữ.
            if (ch is >= '̀' and <= 'ͯ') continue;
            var c = Fold(char.ToLowerInvariant(ch));
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingDash && sb.Length > 0) sb.Append('-');
                pendingDash = false;
                if (sb.Length >= MaxGeneratedLength) break;
                sb.Append(c);
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "bai-viet" : slug;
    }

    private const string Accented = "àáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵđ";
    private const string Plain = "aaaaaaaaaaaaaaaaaeeeeeeeeeeeiiiiiooooooooooooooooouuuuuuuuuuuyyyyyd";

    private static char Fold(char c)
    {
        var i = Accented.IndexOf(c, StringComparison.Ordinal);
        return i >= 0 ? Plain[i] : c;
    }

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();
}

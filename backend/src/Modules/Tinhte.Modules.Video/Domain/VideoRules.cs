using System.Text;

namespace Tinhte.Modules.Video.Domain;

/// <summary>ContentStatus của hợp đồng v0.3 (trạng thái video) — khớp CHECK ck_videos_status (0306).</summary>
public static class VideoStatuses
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
    /// Trạng thái cho phép PATCH (giống bài viết, ADR-0005): đang chờ duyệt / đã duyệt / đã lên lịch / đã rút phải qua
    /// workflow (WBS-040) trước — PATCH trả 409 để không âm thầm vô hiệu một quyết định duyệt.
    /// </summary>
    public static readonly IReadOnlySet<string> Editable = new HashSet<string>(StringComparer.Ordinal)
    {
        Draft, ChangesRequested, Rejected, Published, Unpublished,
    };
}

/// <summary>Vòng đời revision video (tt.video_revisions.status, migration 0331).</summary>
public static class VideoRevisionStatuses
{
    public const string Draft = "DRAFT";
}

public static class VideoContentKinds
{
    public const string Editorial = "EDITORIAL";
    public const string Sponsored = "SPONSORED";
    public const string Pattern = "^(EDITORIAL|SPONSORED)$";
}

/// <summary>
/// Slug video (cột tt.videos.slug UNIQUE của schema 04). Hợp đồng v0.3 không có slug cho video (watch theo id), nên slug được
/// sinh một lần lúc tạo: phần từ tiêu đề + 8 ký tự hex của id → không cần vòng thử trùng.
/// Cùng thuật toán bỏ dấu với ArticleSlug (module không tham chiếu nhau — ADR-0002); gom về Platform khi có module thứ ba cần.
/// </summary>
public static class VideoSlug
{
    private const int MaxTitlePart = 200;

    public static string For(string title, Guid videoId) => $"{FromTitle(title)}-{videoId.ToString("N")[^8..]}";

    public static string FromTitle(string title)
    {
        var sb = new StringBuilder(title.Length);
        var pendingDash = false;
        foreach (var ch in title)
        {
            if (ch is >= '̀' and <= 'ͯ') continue;   // dấu tổ hợp (NFD)
            var c = Fold(char.ToLowerInvariant(ch));
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingDash && sb.Length > 0) sb.Append('-');
                pendingDash = false;
                if (sb.Length >= MaxTitlePart) break;
                sb.Append(c);
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "video" : slug;
    }

    private const string Accented = "àáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵđ";
    private const string Plain = "aaaaaaaaaaaaaaaaaeeeeeeeeeeeiiiiiooooooooooooooooouuuuuuuuuuuyyyyyd";

    private static char Fold(char c)
    {
        var i = Accented.IndexOf(c, StringComparison.Ordinal);
        return i >= 0 ? Plain[i] : c;
    }
}

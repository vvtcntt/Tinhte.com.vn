using System.Text;

namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>
/// MinHash trên shingle từ (ADR-0008 §7): tiêu đề + tóm tắt ở dạng <see cref="TextNormalizer.Fold"/> (giữ dấu), bỏ từ dừng; shingle = từng âm tiết
/// + từng cặp âm tiết liền nhau (từ ghép tiếng Việt thường 2 âm tiết). 64 hàm băm; LSH 32 dải × 2 hàng để tìm ứng viên qua index GIN
/// (xác suất là ứng viên ≈ 95% khi độ giống 0,3; ≈ 27% khi 0,1 — ứng viên luôn được kiểm lại bằng <see cref="Similarity"/>).
/// Hàm băm cố định (FNV-1a 64 + hệ số sinh từ hạt giống cố định): chữ ký ổn định giữa các tiến trình và các lần chạy.
/// </summary>
public static class MinHash
{
    public const int Hashes = 64;
    public const int Bands = 32;
    public const int Rows = Hashes / Bands;

    private static readonly (ulong A, ulong B)[] Coefficients = CreateCoefficients();

    /// <summary>Từ dừng tiếng Việt/Anh thường gặp trong tiêu đề tin (không mang nghĩa sự kiện).</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "và", "của", "là", "có", "được", "cho", "các", "những", "một", "này", "với", "trong", "đã", "sẽ", "không", "khi", "từ", "để", "đến",
        "về", "theo", "như", "tại", "ra", "lên", "vào", "bị", "thì", "mà", "nhưng", "còn", "cũng", "rất", "nhiều", "đang", "vừa", "sau", "trước",
        "nào", "gì", "ở", "hay", "hoặc", "nên", "vì", "do", "nếu", "đó", "kia", "chỉ", "lại", "thêm", "hơn",
        "the", "a", "an", "of", "to", "in", "for", "on", "and", "or", "is", "are", "was", "were", "with", "its", "it", "at", "by", "from", "as",
        "be", "will", "has", "have", "this", "that", "after", "before", "about", "into", "over", "how", "why", "what", "you", "your",
    };

    public static HashSet<ulong> Shingles(string? title, string? summary)
    {
        var tokens = TextNormalizer.Fold(title + " " + summary).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !StopWords.Contains(t)).ToList();
        var shingles = new HashSet<ulong>();
        for (var i = 0; i < tokens.Count; i++)
        {
            shingles.Add(Fnv64(tokens[i]));
            if (i + 1 < tokens.Count) shingles.Add(Fnv64(tokens[i] + " " + tokens[i + 1]));
        }

        return shingles;
    }

    /// <summary>Chữ ký 64 giá trị; tập rỗng → null (mục không có chữ nào để so, không gom cụm tự động).</summary>
    public static int[]? Signature(IReadOnlySet<ulong> shingles)
    {
        if (shingles.Count == 0) return null;
        var signature = new int[Hashes];
        for (var i = 0; i < Hashes; i++)
        {
            var (a, b) = Coefficients[i];
            var min = ulong.MaxValue;
            foreach (var x in shingles)
            {
                var h = Mix(unchecked((a * x) + b));
                if (h < min) min = h;
            }

            signature[i] = unchecked((int)(min >> 32));
        }

        return signature;
    }

    public static int[]? Signature(string? title, string? summary) => Signature(Shingles(title, summary));

    /// <summary>Khóa dải LSH (lưu agg_items.minhash_bands, index GIN): hai mục chung ≥ 1 khóa là ứng viên.</summary>
    public static long[] BandKeys(int[] signature)
    {
        var keys = new long[Bands];
        for (var band = 0; band < Bands; band++)
        {
            var h = 14695981039346656037UL ^ (ulong)band;
            for (var row = 0; row < Rows; row++)
            {
                h = Mix(unchecked((h * 1099511628211UL) ^ (uint)signature[(band * Rows) + row]));
            }

            keys[band] = unchecked((long)h);
        }

        return keys;
    }

    /// <summary>Ước lượng độ giống Jaccard: tỉ lệ vị trí trùng của hai chữ ký.</summary>
    public static double Similarity(int[] a, int[] b)
    {
        if (a.Length != Hashes || b.Length != Hashes) return 0;
        var same = 0;
        for (var i = 0; i < Hashes; i++)
        {
            if (a[i] == b[i]) same++;
        }

        return (double)same / Hashes;
    }

    private static ulong Fnv64(string value)
    {
        var h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            h ^= b;
            h = unchecked(h * 1099511628211UL);
        }

        return h;
    }

    /// <summary>Bộ trộn của SplitMix64.</summary>
    private static ulong Mix(ulong z)
    {
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    private static (ulong, ulong)[] CreateCoefficients()
    {
        var state = 0x7A6E_7465_6E65_7773UL;   // hạt giống cố định — đổi là mọi chữ ký đã lưu mất hiệu lực
        ulong Next() => Mix(state = unchecked(state + 0x9E3779B97F4A7C15UL));
        var result = new (ulong, ulong)[Hashes];
        for (var i = 0; i < Hashes; i++) result[i] = (Next() | 1UL, Next());
        return result;
    }
}

/// <summary>Ngưỡng gom cụm (<c>Aggregation:Clustering:*</c>), chờ hiệu chỉnh bằng dữ liệu tách/gộp tay của BTV (BR-23).</summary>
public sealed record ClusteringOptions(double Similarity, double SimilarityWithEntity, TimeSpan Window)
{
    /// <summary>Giống ≥ 0,5 ⇒ cùng cụm; giống ≥ 0,25 và chung một thực thể (từ khóa thực thể của luật chuyên mục) ⇒ cùng cụm; trong 48 giờ.</summary>
    public static ClusteringOptions Default { get; } = new(0.5, 0.25, TimeSpan.FromHours(48));

    public bool SameStory(double similarity, bool sharesEntity) => similarity >= Similarity || (sharesEntity && similarity >= SimilarityWithEntity);
}

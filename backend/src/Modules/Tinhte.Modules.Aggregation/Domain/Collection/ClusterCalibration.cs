using System.Globalization;
using System.Text;

namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>Một cặp mục có nhãn của BTV: <c>SameStory</c> = gộp (true) hoặc tách (false) — BR-23, migration 0345.</summary>
public sealed record LabeledPair(double Similarity, bool SharesEntity, bool SameStory);

/// <summary>Kết quả của một bộ ngưỡng trên các cặp có nhãn ("cùng sự kiện" là lớp dương).</summary>
public sealed record ThresholdScore(double Similarity, double SimilarityWithEntity, int TruePositives, int FalsePositives, int FalseNegatives, int TrueNegatives)
{
    public double Precision => TruePositives + FalsePositives == 0 ? 0 : (double)TruePositives / (TruePositives + FalsePositives);

    public double Recall => TruePositives + FalseNegatives == 0 ? 0 : (double)TruePositives / (TruePositives + FalseNegatives);

    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

/// <summary>Báo cáo hiệu chỉnh: điểm của ngưỡng hiện tại, bộ ngưỡng tốt nhất trên lưới, và có đủ dữ liệu để tin hay không.</summary>
public sealed record CalibrationReport(int Positives, int Negatives, bool Sufficient, ThresholdScore Current, ThresholdScore Best, IReadOnlyList<ThresholdScore> Top)
{
    public string ToText()
    {
        static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        static string Row(ThresholdScore s) =>
            $"  similarity ≥ {F(s.Similarity)} | với thực thể ≥ {F(s.SimilarityWithEntity)} | precision {F(s.Precision)} | recall {F(s.Recall)} | F1 {F(s.F1)} | TP {s.TruePositives} FP {s.FalsePositives} FN {s.FalseNegatives} TN {s.TrueNegatives}";
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Cặp có nhãn: {Positives} cùng sự kiện (gộp), {Negatives} khác sự kiện (tách).");
        if (!Sufficient)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"CHƯA ĐỦ DỮ LIỆU: cần ít nhất {ClusterCalibration.MinimumPerClass} cặp mỗi loại để đề xuất ngưỡng. Giữ ngưỡng hiện tại.");
        }

        sb.AppendLine("Ngưỡng hiện tại:").AppendLine(Row(Current));
        sb.AppendLine("Tốt nhất trên lưới (F1, hòa thì precision cao hơn):").AppendLine(Row(Best));
        sb.AppendLine("Top 5:");
        foreach (var s in Top) sb.AppendLine(Row(s));
        if (Sufficient)
        {
            sb.AppendLine("Đề xuất cấu hình (đổi qua pull request, BA/TL duyệt):");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Aggregation__Clustering__Similarity={F(Best.Similarity)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Aggregation__Clustering__SimilarityWithEntity={F(Best.SimilarityWithEntity)}");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Hiệu chỉnh ngưỡng gom cụm từ dữ liệu tách/gộp tay của BTV (BR-23). Quét lưới ngưỡng (giống ≥ s, hoặc giống ≥ e và chung thực thể; e ≤ s),
/// chấm precision/recall/F1 cho quyết định "cùng sự kiện". Gộp tay là dương, tách tay là âm — lệch về các ca khó (BTV chỉ sửa chỗ thuật toán
/// sai), nên chỉ dùng làm gợi ý; đổi ngưỡng vẫn qua pull request có người duyệt.
/// </summary>
public static class ClusterCalibration
{
    public const int MinimumPerClass = 30;

    public static CalibrationReport Evaluate(IReadOnlyList<LabeledPair> pairs, ClusteringOptions current)
    {
        var positives = pairs.Count(p => p.SameStory);
        var negatives = pairs.Count - positives;
        var grid = new List<ThresholdScore>();
        for (var s = 20; s <= 90; s += 5)
        {
            for (var e = 10; e <= s; e += 5)
            {
                grid.Add(Score(pairs, s / 100.0, e / 100.0));
            }
        }

        var ranked = grid.OrderByDescending(g => g.F1).ThenByDescending(g => g.Precision).ThenByDescending(g => g.Similarity).ToList();
        return new CalibrationReport(positives, negatives, positives >= MinimumPerClass && negatives >= MinimumPerClass,
            Score(pairs, current.Similarity, current.SimilarityWithEntity), ranked[0], ranked.Take(5).ToList());
    }

    public static ThresholdScore Score(IReadOnlyList<LabeledPair> pairs, double similarity, double withEntity)
    {
        var options = new ClusteringOptions(similarity, withEntity, TimeSpan.FromHours(48));
        int tp = 0, fp = 0, fn = 0, tn = 0;
        foreach (var p in pairs)
        {
            var predicted = options.SameStory(p.Similarity, p.SharesEntity);
            if (predicted && p.SameStory) tp++;
            else if (predicted) fp++;
            else if (p.SameStory) fn++;
            else tn++;
        }

        return new ThresholdScore(similarity, withEntity, tp, fp, fn, tn);
    }
}

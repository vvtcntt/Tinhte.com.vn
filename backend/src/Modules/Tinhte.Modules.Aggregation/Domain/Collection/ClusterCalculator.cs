namespace Tinhte.Modules.Aggregation.Domain.Collection;

/// <summary>Một mục trong cụm, đủ dữ liệu để tính đại diện, chuyên mục, nhãn và điểm (BR-21, BR-22, BR-32).</summary>
public sealed record ClusterMember(
    Guid ItemId,
    Guid SourceId,
    Guid FeedId,
    string Title,
    Guid? CategoryId,
    decimal? Confidence,
    bool NeedsReview,
    bool Sensitive,
    int Version,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset EffectiveAt,
    int TrustWeight);

/// <summary>
/// Trọng số điểm ưu tiên (BR-32), cấu hình <c>Aggregation:Scoring:*</c> — đổi qua pull request nên lịch sử nằm trong git. Điểm = tổng có trọng số
/// của các thành phần 0–1 chia cho tổng trọng số × 100. "Chủ đề nóng trong ngày" chưa có ở GĐ1 (chưa có nơi BTV đặt) → không tính.
/// </summary>
public sealed record ScoringWeights(double Sources, double Trust, double Relevance, double Velocity, double Freshness)
{
    public static ScoringWeights Default { get; } = new(35, 15, 15, 15, 20);

    public double Total => Sources + Trust + Relevance + Velocity + Freshness;
}

/// <summary>Các cột tính được của tt.agg_clusters.</summary>
public sealed record ClusterFacts(
    string Title,
    Guid RepresentativeItemId,
    Guid? PrimaryCategoryId,
    bool CategoryNeedsReview,
    decimal Score,
    IReadOnlyList<string> Labels,
    int ItemCount,
    int FeedCount,
    int SourceCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastItemAt);

/// <summary>
/// Tính tóm tắt cụm từ các mục:
/// <list type="bullet">
///   <item>mục đại diện: nguồn uy tín nhất, rồi đăng sớm nhất (BR-21); tiêu đề cụm = tiêu đề mục đại diện;</item>
///   <item>chuyên mục chính: chuyên mục nhiều mục nhất (hòa → của mục đại diện); "cần xem" khi mọi mục thuộc chuyên mục đó đều cần xem;</item>
///   <item>số nguồn độc lập = số nguồn khác nhau (hai feed của cùng nguồn tính một); <c>sourceCount</c> của hợp đồng = số feed khác nhau;</item>
///   <item>điểm (BR-32): nguồn độc lập (đủ 5 = tối đa), uy tín trung bình, độ liên quan chuyên mục (độ tin trung bình), tốc độ (nguồn mới trong
///         2 giờ, đủ 3 = tối đa), độ mới (giảm tuyến tính về 0 sau 48 giờ không có mục mới) — tính tại <paramref name="now"/>;</item>
///   <item>nhãn: SINGLE_SOURCE, SENSITIVE, SOURCE_UPDATED (có mục phiên bản &gt; 1), NEW_SINCE_COVERED (đã đưa tin mà có mục thấy sau đó — BR-22).</item>
/// </list>
/// </summary>
public static class ClusterCalculator
{
    public static ClusterFacts Compute(IReadOnlyList<ClusterMember> members, string status, DateTimeOffset? coveredAt, DateTimeOffset now, ScoringWeights weights)
    {
        if (members.Count == 0) throw new ArgumentException("A cluster needs at least one item.", nameof(members));

        var representative = members.OrderByDescending(m => m.TrustWeight).ThenBy(m => m.EffectiveAt).ThenBy(m => m.ItemId).First();
        var primary = members.Where(m => m.CategoryId is not null)
            .GroupBy(m => m.CategoryId!.Value)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key == representative.CategoryId)
            .ThenByDescending(g => g.Sum(m => m.Confidence ?? 0))
            .ThenBy(g => g.Key)
            .FirstOrDefault();

        var sources = members.GroupBy(m => m.SourceId).Select(g => (Trust: g.Max(m => m.TrustWeight), First: g.Min(m => m.EffectiveAt))).ToList();
        var lastItem = members.Max(m => m.EffectiveAt);

        var components = new[]
        {
            (weights.Sources, Math.Min(1.0, sources.Count / 5.0)),
            (weights.Trust, Math.Clamp((sources.Average(s => s.Trust) - 1) / 4.0, 0, 1)),
            (weights.Relevance, primary is null ? 0 : (double)primary.Average(m => m.Confidence ?? 0)),
            (weights.Velocity, Math.Min(1.0, sources.Count(s => s.First >= now - TimeSpan.FromHours(2)) / 3.0)),
            (weights.Freshness, Math.Clamp(1 - ((now - lastItem).TotalHours / 48.0), 0, 1)),
        };
        var score = weights.Total <= 0 ? 0 : components.Sum(c => c.Item1 * c.Item2) / weights.Total * 100;

        var labels = new List<string>();
        if (sources.Count == 1) labels.Add("SINGLE_SOURCE");
        if (members.Any(m => m.Sensitive)) labels.Add("SENSITIVE");
        if (members.Any(m => m.Version > 1)) labels.Add("SOURCE_UPDATED");
        if (status == "COVERED" && coveredAt is { } covered && members.Any(m => m.FirstSeenAt > covered)) labels.Add("NEW_SINCE_COVERED");

        return new ClusterFacts(
            Title: representative.Title,
            RepresentativeItemId: representative.ItemId,
            PrimaryCategoryId: primary?.Key,
            CategoryNeedsReview: primary is not null && primary.All(m => m.NeedsReview),
            Score: Math.Round((decimal)Math.Clamp(score, 0, 100), 2),
            Labels: labels,
            ItemCount: members.Count,
            FeedCount: members.Select(m => m.FeedId).Distinct().Count(),
            SourceCount: sources.Count,
            FirstSeenAt: members.Min(m => m.EffectiveAt),
            LastItemAt: lastItem);
    }
}

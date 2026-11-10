using Tinhte.Modules.Aggregation.Domain.Collection;

namespace Tinhte.Api.Tests;

/// <summary>Radar tin GĐ1 lát 3 — phần không cần DB: MinHash/LSH, ngưỡng gom cụm, đại diện/chuyên mục/nhãn/điểm của cụm (BR-21…23, BR-32).</summary>
public sealed class AggregationClusteringTests
{
    private const string LaunchA = "ModelX chính thức ra mắt tại Việt Nam, giá từ 20 triệu đồng";
    private const string LaunchB = "ModelX ra mắt Việt Nam với giá 20 triệu đồng";
    private const string Gold = "Giá vàng hôm nay tăng mạnh";

    [Fact]
    public void Minhash_is_deterministic_and_separates_stories()
    {
        var a = MinHash.Signature(LaunchA, "Hãng điện thoại công bố ModelX bán ra từ tuần sau")!;
        var b = MinHash.Signature(LaunchB, "ModelX bán chính thức từ tuần sau tại các cửa hàng")!;
        var gold = MinHash.Signature(Gold, "Vàng SJC tăng 1 triệu đồng mỗi lượng")!;

        Assert.Equal(a, MinHash.Signature(LaunchA, "Hãng điện thoại công bố ModelX bán ra từ tuần sau"));   // ổn định giữa các lần gọi/tiến trình
        Assert.Equal(1.0, MinHash.Similarity(a, a));
        Assert.InRange(MinHash.Similarity(a, b), 0.25, 0.6);    // cùng sự kiện, viết khác
        Assert.InRange(MinHash.Similarity(a, gold), 0, 0.15);   // khác sự kiện
        Assert.NotEmpty(MinHash.BandKeys(a).Intersect(MinHash.BandKeys(b)));   // LSH đưa cặp giống vừa thành ứng viên
        Assert.Empty(MinHash.BandKeys(a).Intersect(MinHash.BandKeys(gold)));
        Assert.Equal(MinHash.Bands, MinHash.BandKeys(a).Length);
        Assert.Null(MinHash.Signature("và của là", "the of"));   // chỉ có từ dừng → không gom tự động
        Assert.Equal(MinHash.Signature("APPLE ra mắt!", null), MinHash.Signature("apple   ra mắt", null));
    }

    [Theory]
    [InlineData(0.55, false, true)]
    [InlineData(0.39, false, false)]
    [InlineData(0.39, true, true)]    // giống vừa + chung thực thể (BR-21)
    [InlineData(0.20, true, false)]
    public void Same_story_needs_similarity_or_shared_entity(double similarity, bool entity, bool expected) =>
        Assert.Equal(expected, ClusteringOptions.Default.SameStory(similarity, entity));

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly Guid Phones = Guid.NewGuid();
    private static readonly Guid Laptops = Guid.NewGuid();

    private static ClusterMember Member(Guid source, int trust, double hoursAgo, Guid? category = null, decimal? confidence = 0.9m, bool review = false,
        bool sensitive = false, int version = 1, string title = "Tin", Guid? feed = null) =>
        new(Guid.NewGuid(), source, feed ?? Guid.NewGuid(), title, category, confidence, review, sensitive, version, Now.AddHours(-hoursAgo), Now.AddHours(-hoursAgo), trust);

    [Fact]
    public void Representative_is_most_trusted_then_earliest_and_category_is_the_majority()
    {
        var (s1, s2, s3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var members = new[]
        {
            Member(s1, 3, 5, Phones, title: "Sớm nhất nhưng uy tín 3"),
            Member(s2, 5, 2, Laptops, review: true, title: "Uy tín 5, muộn hơn"),
            Member(s2, 5, 3, Laptops, review: true, title: "Uy tín 5, sớm hơn"),
            Member(s3, 4, 1, Phones, title: "Uy tín 4"),
        };
        var facts = ClusterCalculator.Compute(members, "OPEN", null, Now, ScoringWeights.Default);
        Assert.Equal("Uy tín 5, sớm hơn", facts.Title);
        Assert.Equal(members[2].ItemId, facts.RepresentativeItemId);
        Assert.Equal(Laptops, facts.PrimaryCategoryId);   // hòa 2–2 → chuyên mục của mục đại diện
        Assert.True(facts.CategoryNeedsReview);
        Assert.Equal((4, 4, 3), (facts.ItemCount, facts.FeedCount, facts.SourceCount));
        Assert.Equal((Now.AddHours(-5), Now.AddHours(-1)), (facts.FirstSeenAt, facts.LastItemAt));
        Assert.Empty(facts.Labels);
    }

    [Fact]
    public void Score_grows_with_independent_sources_and_decays_with_age()   // TC-AGG-10
    {
        var feed = Guid.NewGuid();
        var source = Guid.NewGuid();
        var single = ClusterCalculator.Compute([Member(source, 3, 1, Phones, feed: feed), Member(source, 3, 1, Phones)], "OPEN", null, Now, ScoringWeights.Default);
        var three = ClusterCalculator.Compute([Member(Guid.NewGuid(), 3, 1, Phones), Member(Guid.NewGuid(), 3, 1, Phones), Member(Guid.NewGuid(), 3, 1, Phones)],
            "OPEN", null, Now, ScoringWeights.Default);
        Assert.True(three.Score > single.Score, $"{three.Score} > {single.Score}");
        Assert.Equal(["SINGLE_SOURCE"], single.Labels);   // hai feed của cùng nguồn vẫn là một nguồn độc lập
        Assert.Equal((2, 2, 1), (single.ItemCount, single.FeedCount, single.SourceCount));

        var later = ClusterCalculator.Compute([Member(Guid.NewGuid(), 3, 1, Phones)], "OPEN", null, Now.AddHours(30), ScoringWeights.Default);
        var fresh = ClusterCalculator.Compute([Member(Guid.NewGuid(), 3, 1, Phones)], "OPEN", null, Now, ScoringWeights.Default);
        Assert.True(later.Score < fresh.Score);
        Assert.InRange(three.Score, 0m, 100m);

        // Trọng số chỉ tính một thành phần: 5 nguồn độc lập = 100 điểm.
        var onlySources = new ScoringWeights(1, 0, 0, 0, 0);
        Assert.Equal(100m, ClusterCalculator.Compute(Enumerable.Range(0, 5).Select(_ => Member(Guid.NewGuid(), 1, 40)).ToList(), "OPEN", null, Now, onlySources).Score);
    }

    [Fact]
    public void Labels_follow_br22_and_br34()
    {
        var covered = Now.AddHours(-2);
        var members = new[] { Member(Guid.NewGuid(), 3, 3, sensitive: true), Member(Guid.NewGuid(), 3, 1, version: 2) };
        var facts = ClusterCalculator.Compute(members, "COVERED", covered, Now, ScoringWeights.Default);
        Assert.Equal(["SENSITIVE", "SOURCE_UPDATED", "NEW_SINCE_COVERED"], facts.Labels);
        Assert.DoesNotContain("NEW_SINCE_COVERED", ClusterCalculator.Compute(members, "OPEN", covered, Now, ScoringWeights.Default).Labels);
        Assert.Throws<ArgumentException>(() => ClusterCalculator.Compute([], "OPEN", null, Now, ScoringWeights.Default));
    }
}

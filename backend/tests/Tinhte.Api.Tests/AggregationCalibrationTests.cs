using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Platform.Data;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>Hiệu chỉnh ngưỡng gom cụm từ tách/gộp tay (BR-23, migration 0345): chấm lưới ngưỡng, đủ/thiếu dữ liệu.</summary>
public sealed class AggregationCalibrationTests
{
    private static IEnumerable<LabeledPair> Many(int n, double similarity, bool entity, bool same) => Enumerable.Repeat(new LabeledPair(similarity, entity, same), n);

    [Fact]
    public void Grid_search_finds_thresholds_that_separate_labels()
    {
        // Cùng sự kiện: giống 0,35 + chung thực thể, hoặc giống 0,6; khác sự kiện: giống 0,3 không thực thể, hoặc 0,15 có thực thể.
        var pairs = Many(40, 0.35, true, true).Concat(Many(20, 0.6, false, true))
            .Concat(Many(40, 0.30, false, false)).Concat(Many(20, 0.15, true, false)).ToList();
        var report = ClusterCalibration.Evaluate(pairs, ClusteringOptions.Default);
        Assert.True(report.Sufficient);
        Assert.Equal((60, 60), (report.Positives, report.Negatives));
        Assert.Equal(1.0, report.Best.F1);
        Assert.Equal(1.0, report.Current.F1);   // ngưỡng mặc định (0,5 / 0,25) đã tách đúng bộ này
        Assert.Contains("Aggregation__Clustering__Similarity=", report.ToText(), StringComparison.Ordinal);

        var tooStrict = ClusterCalibration.Score(pairs, 0.9, 0.9);
        Assert.Equal((0, 60, 60), (tooStrict.TruePositives, tooStrict.FalseNegatives, tooStrict.TrueNegatives));
        Assert.Equal(0, tooStrict.F1);
    }

    [Fact]
    public void Little_data_is_reported_as_insufficient()
    {
        var report = ClusterCalibration.Evaluate([.. Many(3, 0.4, true, true), .. Many(2, 0.1, false, false)], ClusteringOptions.Default);
        Assert.False(report.Sufficient);
        Assert.Contains("CHƯA ĐỦ DỮ LIỆU", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Đề xuất cấu hình", report.ToText(), StringComparison.Ordinal);
        Assert.False(ClusterCalibration.Evaluate([], ClusteringOptions.Default).Sufficient);
    }
}

/// <summary>Tách/gộp ghi item_ids/other_item_ids và bộ hiệu chỉnh đọc ra cặp có nhãn.</summary>
public sealed partial class AggregationCollectionDbTests
{
    [DbFact]
    public async Task Split_and_merge_record_labelled_pairs_for_calibration()
    {
        var s = await StoryAsync();
        using var cms = Cms();
        var (detail, etag) = await ClusterCallAsync(cms, HttpMethod.Get, $"/clusters/{s.Story}", _editor);
        var items = detail.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        var (split, splitEtag) = await ClusterCallAsync(cms, HttpMethod.Post, $"/clusters/{s.Story}/split", _editor,
            $$"""{"itemIds":["{{items[2]}}"]}""", etag, HttpStatusCode.Created, "split-" + Guid.NewGuid());
        var created = split.GetProperty("id").GetGuid();
        Assert.Equal($"SPLIT|{{{items[2]}}}|2", await db.ScalarAsync<string>($"""
            SELECT action || '|' || item_ids::text || '|' || cardinality(other_item_ids) FROM tt.agg_cluster_actions
             WHERE cluster_id = '{s.Story}' AND action = 'SPLIT'
            """));
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.agg_cluster_actions WHERE cluster_id = '{created}' AND item_ids IS NULL"));   // dòng đối xứng không lặp dữ liệu

        await ClusterCallAsync(cms, HttpMethod.Post, $"/clusters/{created}/merge", _editor, $$"""{"targetClusterId":"{{s.Story}}"}""", splitEtag);
        Assert.Equal($"MERGE|{{{items[2]}}}|2", await db.ScalarAsync<string>($"""
            SELECT action || '|' || item_ids::text || '|' || cardinality(other_item_ids) FROM tt.agg_cluster_actions
             WHERE cluster_id = '{created}' AND action = 'MERGE'
            """));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Tinhte"] = db.ConnectionString }).Build();
        var database = new TinhteDatabase(config);
        _databases.Add(database);
        var report = await new ClusterCalibrationService(database, AggregationOptions.From(config)).RunAsync(30, CancellationToken.None);
        Assert.True(report.Positives >= 2 && report.Negatives >= 2, report.ToText());   // cặp của test này (và có thể của test khác trong cùng DB)
    }
}

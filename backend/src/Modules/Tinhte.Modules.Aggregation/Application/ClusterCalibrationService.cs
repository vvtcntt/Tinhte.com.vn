using Npgsql;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Platform.Data;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>
/// Lấy cặp có nhãn từ các lần tách/gộp tay (agg_cluster_actions.item_ids/other_item_ids — migration 0345) và chấm ngưỡng gom cụm
/// (<see cref="ClusterCalibration"/>). Chỉ đọc. Chạy bằng <c>Tinhte.Worker calibrate [--days 90]</c>.
/// </summary>
public sealed class ClusterCalibrationService(ITinhteDatabase database, AggregationOptions options)
{
    /// <summary>Tối đa mỗi phía của một thao tác (cụm rất lớn không làm lệch báo cáo).</summary>
    public const int MaxItemsPerSide = 50;

    public async Task<CalibrationReport> RunAsync(int days, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        var actions = new List<(bool Same, Guid[] Moved, Guid[] Other)>();
        await using (var command = AggregationSql.Command(connection, null, """
            SELECT action, item_ids, other_item_ids FROM tt.agg_cluster_actions
             WHERE action IN ('SPLIT', 'MERGE') AND item_ids IS NOT NULL AND other_item_ids IS NOT NULL AND created_at >= now() - @days * interval '1 day'
             ORDER BY created_at
            """).WithInt("days", days))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                actions.Add((r.GetString(0) == "MERGE", r.GetFieldValue<Guid[]>(1).Take(MaxItemsPerSide).ToArray(), r.GetFieldValue<Guid[]>(2).Take(MaxItemsPerSide).ToArray()));
            }
        }

        var ids = actions.SelectMany(a => a.Moved.Concat(a.Other)).Distinct().ToList();
        var items = new Dictionary<Guid, (int[]? Signature, string Title, string? Summary, string? Site)>();
        if (ids.Count > 0)
        {
            await using var command = AggregationSql.Command(connection, null, "SELECT id, minhash, title, summary, site_key FROM tt.agg_items WHERE id = ANY(@ids)")
                .WithUuidArray("ids", ids);
            await using var r = await command.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var title = r.GetString(2);
                var summary = AggregationSql.NullableText(r, 3);
                var signature = r.IsDBNull(1) ? MinHash.Signature(title, summary) : r.GetFieldValue<int[]>(1);
                items[r.GetGuid(0)] = (signature, title, summary, AggregationSql.NullableText(r, 4));
            }
        }

        var entities = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var site in items.Values.Select(i => i.Site).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            entities[site] = await EntityKeywordsAsync(connection, site, ct);
        }

        var pairs = new List<LabeledPair>();
        foreach (var (same, moved, other) in actions)
        {
            foreach (var a in moved)
            {
                foreach (var b in other)
                {
                    if (!items.TryGetValue(a, out var x) || !items.TryGetValue(b, out var y) || x.Signature is null || y.Signature is null) continue;
                    var keywords = x.Site is not null && entities.TryGetValue(x.Site, out var k) ? k : [];
                    var shares = keywords.Count > 0 && EntitiesOf(x.Title, x.Summary, keywords).Overlaps(EntitiesOf(y.Title, y.Summary, keywords));
                    pairs.Add(new LabeledPair(MinHash.Similarity(x.Signature, y.Signature), shares, same));
                }
            }
        }

        return ClusterCalibration.Evaluate(pairs, options.Clustering);
    }

    private static async Task<List<string>> EntityKeywordsAsync(NpgsqlConnection connection, string site, CancellationToken ct)
    {
        var list = new List<string>();
        await using var command = AggregationSql.Command(connection, null, """
            SELECT DISTINCT k FROM tt.agg_category_rules r JOIN tt.categories c ON c.id = r.category_id, unnest(r.entity_keywords) AS k
             WHERE c.site_key = @site AND c.is_enabled
            """).WithText("site", site);
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var folded = TextNormalizer.Fold(r.GetString(0));
            if (folded.Length > 0) list.Add(folded);
        }

        return list;
    }

    private static HashSet<string> EntitiesOf(string title, string? summary, List<string> keywords)
    {
        var padded = " " + TextNormalizer.Fold(title + " " + summary) + " ";
        return keywords.Where(k => ItemClassifier.HasWord(padded, k)).ToHashSet(StringComparer.Ordinal);
    }
}

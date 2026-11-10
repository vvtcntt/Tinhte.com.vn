using Npgsql;
using Tinhte.Modules.Aggregation.Domain.Collection;

namespace Tinhte.Modules.Aggregation.Infrastructure;

/// <summary>
/// Gom cụm và tính lại cụm trên DB (ADR-0008 §7) — dùng chung cho worker (mục mới) và API (tách/gộp). Mọi thay đổi thành viên cụm của một site
/// chạy dưới khóa tư vấn theo site (<c>pg_advisory_xact_lock</c>, giữ tới hết transaction): hai worker không tạo hai cụm cho cùng một sự kiện,
/// và thao tác tách/gộp của BTV không chen giữa lúc worker gom. Thứ tự khóa: (hồ sơ → feed của worker) → khóa site → dòng cụm.
/// </summary>
internal static class ClusterStore
{
    /// <summary>Trạng thái cụm còn nhận mục mới; CLOSED thì không (mục mới mở cụm khác).</summary>
    private const string JoinableStatuses = "('OPEN', 'WATCHING', 'DISMISSED', 'COVERED')";

    public static async Task LockSitesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, IEnumerable<string> siteKeys, CancellationToken ct)
    {
        foreach (var site in siteKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            await using var command = AggregationSql.Command(connection, tx, "SELECT pg_advisory_xact_lock(hashtextextended('tt.agg_clusters:' || @site, 0))")
                .WithText("site", site);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed record PendingItem(Guid Id, string SiteKey, string Status, Guid? ClusterId, string Title, string? Summary, DateTimeOffset EffectiveAt,
        Guid[] CategoryIds);

    /// <summary>
    /// Mục mới/đổi nội dung: tính chữ ký MinHash; mục CLASSIFIED chưa vào cụm → vào cụm giống nhất (BR-21: giống đủ ngưỡng, hoặc giống vừa + chung thực thể,
    /// trong cửa sổ 48 giờ, cùng site, có chung ít nhất một chuyên mục — BA-01 P-02: mục nhiều chuyên mục được so trong từng chuyên mục của nó)
    /// hoặc mở cụm mới; mục đã trong cụm → tính lại cụm đó. Trả số mục vừa vào cụm.
    /// </summary>
    public static async Task<int> ClusterItemsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyCollection<Guid> itemIds, ClusteringOptions options,
        ScoringWeights weights, DateTimeOffset now, CancellationToken ct)
    {
        if (itemIds.Count == 0) return 0;
        var pending = new List<PendingItem>();
        await using (var command = AggregationSql.Command(connection, tx, $"""
            SELECT i.id, i.site_key, i.status, i.cluster_id, i.title, i.summary, {AggregationSql.ItemEffectiveAt}, i.category_ids
              FROM tt.agg_items i WHERE i.id = ANY(@ids) AND i.site_key IS NOT NULL ORDER BY i.first_seen_at, i.id
            """).WithUuidArray("ids", [.. itemIds]))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                pending.Add(new PendingItem(r.GetGuid(0), r.GetString(1), r.GetString(2), AggregationSql.NullableGuid(r, 3), r.GetString(4),
                    AggregationSql.NullableText(r, 5), r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<Guid[]>(7)));
            }
        }

        await LockSitesAsync(connection, tx, pending.Select(p => p.SiteKey), ct);
        var entities = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var touched = new HashSet<Guid>();
        var joined = 0;
        foreach (var item in pending)
        {
            var signature = MinHash.Signature(item.Title, item.Summary);
            var bands = signature is null ? null : MinHash.BandKeys(signature);
            Guid? cluster = item.ClusterId;
            if (cluster is null && item.Status == ItemClassifier.Classified && signature is not null && item.CategoryIds.Length > 0)
            {
                if (!entities.TryGetValue(item.SiteKey, out var siteEntities)) entities[item.SiteKey] = siteEntities = await EntityKeywordsAsync(connection, tx, item.SiteKey, ct);
                cluster = await FindClusterAsync(connection, tx, item, signature, bands!, siteEntities, options, ct)
                          ?? await CreateClusterAsync(connection, tx, item, now, ct);
                joined++;
            }

            await using (var update = AggregationSql.Command(connection, tx, """
                UPDATE tt.agg_items
                   SET minhash = @minhash, minhash_bands = @bands, cluster_id = @cluster,
                       status = CASE WHEN @cluster IS NOT NULL AND status = 'CLASSIFIED' THEN 'CLUSTERED' ELSE status END
                 WHERE id = @id
                """).WithIntArray("minhash", signature).WithLongArray("bands", bands).WithUuid("cluster", cluster).WithUuid("id", item.Id))
            {
                await update.ExecuteNonQueryAsync(ct);
            }

            if (cluster is { } c) touched.Add(c);
        }

        foreach (var c in touched) await RecomputeAsync(connection, tx, c, weights, now, ct);
        return joined;
    }

    /// <summary>Từ khóa thực thể (sản phẩm/hãng) của mọi chuyên mục đang bật của site — dạng Fold.</summary>
    private static async Task<List<string>> EntityKeywordsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string siteKey, CancellationToken ct)
    {
        var list = new List<string>();
        await using var command = AggregationSql.Command(connection, tx, """
            SELECT DISTINCT k FROM tt.agg_category_rules r JOIN tt.categories c ON c.id = r.category_id, unnest(r.entity_keywords) AS k
             WHERE c.site_key = @site AND c.is_enabled
            """).WithText("site", siteKey);
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var folded = TextNormalizer.Fold(r.GetString(0));
            if (folded.Length > 0) list.Add(folded);
        }

        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static HashSet<string> EntitiesOf(string title, string? summary, List<string> keywords)
    {
        var padded = " " + TextNormalizer.Fold(title + " " + summary) + " ";
        return keywords.Where(k => ItemClassifier.HasWord(padded, k)).ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<Guid?> FindClusterAsync(NpgsqlConnection connection, NpgsqlTransaction tx, PendingItem item, int[] signature, long[] bands,
        List<string> entityKeywords, ClusteringOptions options, CancellationToken ct)
    {
        var mine = EntitiesOf(item.Title, item.Summary, entityKeywords);
        await using var command = AggregationSql.Command(connection, tx, $"""
            SELECT i.cluster_id, i.minhash, i.title, i.summary
              FROM tt.agg_items i
              JOIN tt.agg_clusters c ON c.id = i.cluster_id
             WHERE i.site_key = @site AND i.cluster_id IS NOT NULL AND i.minhash_bands && @bands AND i.id <> @self
               AND i.category_ids && @categories AND c.site_key = @site AND c.status IN {JoinableStatuses}
               AND {AggregationSql.ItemEffectiveAt} BETWEEN @from AND @to
             LIMIT 500
            """)
            .WithText("site", item.SiteKey).WithLongArray("bands", bands).WithUuid("self", item.Id).WithUuidArray("categories", item.CategoryIds)
            .WithTime("from", item.EffectiveAt - options.Window).WithTime("to", item.EffectiveAt + options.Window);
        Guid? best = null;
        var bestSimilarity = -1.0;
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (r.IsDBNull(1)) continue;
            var similarity = MinHash.Similarity(signature, r.GetFieldValue<int[]>(1));
            var shares = mine.Count > 0 && EntitiesOf(r.GetString(2), AggregationSql.NullableText(r, 3), entityKeywords).Overlaps(mine);
            if (options.SameStory(similarity, shares) && similarity > bestSimilarity)
            {
                (best, bestSimilarity) = (r.GetGuid(0), similarity);
            }
        }

        return best;
    }

    private static async Task<Guid> CreateClusterAsync(NpgsqlConnection connection, NpgsqlTransaction tx, PendingItem item, DateTimeOffset now, CancellationToken ct)
    {
        await using var insert = AggregationSql.Command(connection, tx, """
            INSERT INTO tt.agg_clusters (site_key, title, representative_item_id, first_seen_at, last_item_at)
            VALUES (@site, @title, @item, @at, @at) RETURNING id
            """).WithText("site", item.SiteKey).WithText("title", item.Title).WithUuid("item", item.Id).WithTime("at", item.EffectiveAt > now ? now : item.EffectiveAt);
        return (Guid)(await insert.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Tính lại đại diện, chuyên mục, nhãn, điểm, bộ đếm của cụm. Cụm không còn mục (đã gộp sang cụm khác) giữ nguyên ảnh chụp cuối.</summary>
    public static async Task RecomputeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid clusterId, ScoringWeights weights, DateTimeOffset now,
        CancellationToken ct)
    {
        string status;
        DateTimeOffset? coveredAt;
        await using (var head = AggregationSql.Command(connection, tx, "SELECT status, covered_at FROM tt.agg_clusters WHERE id = @id").WithUuid("id", clusterId))
        await using (var r = await head.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return;
            (status, coveredAt) = (r.GetString(0), AggregationSql.NullableTime(r, 1));
        }

        var members = new List<ClusterMember>();
        await using (var command = AggregationSql.Command(connection, tx, $"""
            SELECT i.id, i.content_source_id, i.feed_id, i.title, i.category_id, i.category_confidence, i.needs_review, i.sensitive, i.version,
                   i.first_seen_at, {AggregationSql.ItemEffectiveAt}, p.trust_weight
              FROM tt.agg_items i JOIN tt.agg_source_profiles p ON p.content_source_id = i.content_source_id
             WHERE i.cluster_id = @id
            """).WithUuid("id", clusterId))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                members.Add(new ClusterMember(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), AggregationSql.NullableGuid(r, 4),
                    r.IsDBNull(5) ? null : r.GetDecimal(5), r.GetBoolean(6), r.GetBoolean(7), r.GetInt32(8), r.GetFieldValue<DateTimeOffset>(9),
                    r.GetFieldValue<DateTimeOffset>(10), r.GetInt16(11)));
            }
        }

        if (members.Count == 0) return;
        var facts = ClusterCalculator.Compute(members, status, coveredAt, now, weights);
        await using var update = AggregationSql.Command(connection, tx, """
            UPDATE tt.agg_clusters
               SET title = @title, representative_item_id = @rep, primary_category_id = @category, category_needs_review = @review, score = @score,
                   labels = @labels::varchar(20)[], item_count = @items, feed_count = @feeds, source_count = @sources,
                   first_seen_at = LEAST(@first, now()), last_item_at = LEAST(@last, now())
             WHERE id = @id
            """)
            .WithText("title", facts.Title).WithUuid("rep", facts.RepresentativeItemId).WithUuid("category", facts.PrimaryCategoryId)
            .WithBool("review", facts.CategoryNeedsReview).WithDecimal("score", facts.Score).WithTextArray("labels", facts.Labels)
            .WithInt("items", facts.ItemCount).WithInt("feeds", facts.FeedCount).WithInt("sources", facts.SourceCount)
            .WithTime("first", facts.FirstSeenAt).WithTime("last", facts.LastItemAt).WithUuid("id", clusterId);
        await update.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Bảo trì một site (worker, định kỳ): cụm OPEN chưa ai nhận và không có mục mới quá cửa sổ ⇒ CLOSED (BA-01: "hết cửa sổ 48h, không ai xử lý");
    /// gom bù mục CLASSIFIED chưa vào cụm (mục cũ trước khi có gom cụm, mục được phân loại lại); tính lại điểm (độ mới giảm theo thời gian) của cụm
    /// OPEN/WATCHING còn trong cửa sổ.
    /// </summary>
    public static async Task<(int Closed, int Clustered, int Refreshed)> MaintainSiteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string siteKey,
        ClusteringOptions options, ScoringWeights weights, DateTimeOffset now, CancellationToken ct)
    {
        await LockSitesAsync(connection, tx, [siteKey], ct);
        int closed;
        await using (var close = AggregationSql.Command(connection, tx, """
            UPDATE tt.agg_clusters SET status = 'CLOSED'
             WHERE site_key = @site AND status = 'OPEN' AND assignee_id IS NULL AND last_item_at < @cutoff
            """).WithText("site", siteKey).WithTime("cutoff", now - options.Window))
        {
            closed = await close.ExecuteNonQueryAsync(ct);
        }

        var pending = new List<Guid>();
        await using (var find = AggregationSql.Command(connection, tx, """
            SELECT id FROM tt.agg_items WHERE site_key = @site AND status = 'CLASSIFIED' AND cluster_id IS NULL AND first_seen_at >= @cutoff
             ORDER BY first_seen_at LIMIT 500
            """).WithText("site", siteKey).WithTime("cutoff", now - options.Window))
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) pending.Add(r.GetGuid(0));
        }

        var clustered = await ClusterItemsAsync(connection, tx, pending, options, weights, now, ct);

        var active = new List<Guid>();
        await using (var find = AggregationSql.Command(connection, tx, """
            SELECT id FROM tt.agg_clusters WHERE site_key = @site AND status IN ('OPEN', 'WATCHING') AND last_item_at >= @cutoff
            """).WithText("site", siteKey).WithTime("cutoff", now - options.Window))
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) active.Add(r.GetGuid(0));
        }

        foreach (var id in active) await RecomputeAsync(connection, tx, id, weights, now, ct);
        return (closed, clustered, active.Count);
    }
}

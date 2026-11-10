using System.Data.Common;
using System.Text;
using Npgsql;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>
/// Radar cụm sự kiện và thao tác trên cụm (FR-AGG-004/005, ADR-0008 §7–§9), thống kê. Mọi thao tác chỉ cần <c>aggregation.read</c> (hợp đồng);
/// cờ kiểm phía server, thao tác ghi kiểm trong transaction. Vòng đời GĐ1 (BA-01 §8, chưa có nháp AI):
/// <code>
/// OPEN ⇄ WATCHING;  OPEN/WATCHING → DISMISSED (lý do) → OPEN (mở lại);  * → COVERED (gắn bài);  OPEN hết cửa sổ 48 giờ, chưa ai nhận → CLOSED (worker)
/// </code>
/// Gộp: cụm nguồn → CLOSED (giữ ảnh chụp số liệu lúc gộp), mục chuyển sang cụm đích. Tách: mục chọn sang cụm mới (OPEN). Tách/gộp ghi lịch sử chỉ thêm
/// để hiệu chỉnh ngưỡng (BR-23). Thứ tự khóa như worker: khóa tư vấn theo site → dòng cụm.
/// </summary>
public sealed class AggregationClusterService(ITinhteDatabase database, AggregationOptions options, NewsroomClock clock, TimeProvider time)
{
    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(StringComparer.Ordinal) { "OPEN", "WATCHING", "DISMISSED", "COVERED", "CLOSED" };
    public static readonly IReadOnlyList<string> DefaultStatuses = ["OPEN", "WATCHING"];

    private const string SummarySelect = """
        SELECT c.id, c.site_key, c.title, c.representative_item_id, c.primary_category_id, c.category_needs_review, c.score, c.status,
               c.feed_count, c.source_count, c.item_count, c.first_seen_at, c.last_item_at, c.labels,
               COALESCE((SELECT max(p.usage_mode) FROM tt.agg_items i JOIN tt.agg_source_profiles p ON p.content_source_id = i.content_source_id
                          WHERE i.cluster_id = c.id), 'M0'),
               c.assignee_id, c.covered_article_id, c.row_version
          FROM tt.agg_clusters c
        """;

    // ---------------------------------------------------------------- đọc

    public async Task<IReadOnlyList<AggregationClusterSummary>> ListAsync(AggregationClusterFilter filter, AggregationClusterPosition? after, int limitPlusOne,
        CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        var sql = new StringBuilder(SummarySelect).Append(" WHERE c.site_key = @site AND c.status = ANY(@statuses) AND c.last_item_at >= @from");
        await using var command = AggregationSql.Command(connection, null, "")
            .WithText("site", filter.SiteKey).WithTextArray("statuses", filter.Statuses).WithTime("from", filter.From);
        if (filter.To is { } to)
        {
            sql.Append(" AND c.first_seen_at < @to");
            command.WithTime("to", to);
        }

        if (filter.CategoryId is { } category)
        {
            sql.Append(" AND c.primary_category_id = @category");
            command.WithUuid("category", category);
        }

        if (filter.MinScore is { } minScore)
        {
            sql.Append(" AND c.score >= @min_score");
            command.WithDecimal("min_score", minScore);
        }

        if (after is not null)
        {
            sql.Append(" AND (c.score < @after_score OR (c.score = @after_score AND (c.first_seen_at, c.id) > (@after_at, @after_id)))");
            command.WithDecimal("after_score", after.Score).WithTime("after_at", after.FirstSeenAt).WithUuid("after_id", after.Id);
        }

        sql.Append(" ORDER BY c.score DESC, c.first_seen_at, c.id LIMIT @limit");
        command.WithInt("limit", limitPlusOne);
        command.CommandText = sql.ToString();
        var rows = new List<AggregationClusterSummary>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(ReadSummary(r));
        return rows;
    }

    public async Task<AggregationClusterDetail> GetAsync(Guid clusterId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        return await LoadAsync(connection, null, clusterId, ct) ?? throw NotFound();
    }

    /// <summary>Kiểm cờ + tồn tại (404 trước 428/412).</summary>
    public async Task EnsureExistsAsync(Guid clusterId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        await using var command = AggregationSql.Command(connection, null, "SELECT 1 FROM tt.agg_clusters WHERE id = @id").WithUuid("id", clusterId);
        if (await command.ExecuteScalarAsync(ct) is null) throw NotFound();
    }

    // ---------------------------------------------------------------- thao tác

    public Task<AggregationClusterDetail> DismissAsync(Guid actorId, Guid clusterId, long expected, string? reason, CancellationToken ct) =>
        MutateAsync(actorId, clusterId, expected, ct, async (connection, tx, cluster) =>
        {
            RequireStatus(cluster, "dismissed", "OPEN", "WATCHING");
            var text = AggregationLimits.CheckReason(reason);
            await SetStatusAsync(connection, tx, clusterId, "DISMISSED", ct);
            await ActionAsync(connection, tx, clusterId, "DISMISS", actorId, text, null, null, ct);
            return clusterId;
        });

    public Task<AggregationClusterDetail> WatchAsync(Guid actorId, Guid clusterId, long expected, CancellationToken ct) =>
        MutateAsync(actorId, clusterId, expected, ct, async (connection, tx, cluster) =>
        {
            RequireStatus(cluster, "watched", "OPEN");
            await SetStatusAsync(connection, tx, clusterId, "WATCHING", ct);
            await ActionAsync(connection, tx, clusterId, "WATCH", actorId, null, null, null, ct);
            return clusterId;
        });

    public Task<AggregationClusterDetail> ReopenAsync(Guid actorId, Guid clusterId, long expected, CancellationToken ct) =>
        MutateAsync(actorId, clusterId, expected, ct, async (connection, tx, cluster) =>
        {
            RequireStatus(cluster, "reopened", "DISMISSED", "WATCHING");
            await SetStatusAsync(connection, tx, clusterId, "OPEN", ct);
            await ActionAsync(connection, tx, clusterId, "REOPEN", actorId, null, null, null, ct);
            return clusterId;
        });

    /// <summary>Nhận việc (chống trùng việc, BR-22): người khác đã nhận → 409; chính mình đã nhận → giữ nguyên, không ghi thêm lịch sử.</summary>
    public Task<AggregationClusterDetail> ClaimAsync(Guid actorId, Guid clusterId, long expected, CancellationToken ct) =>
        MutateAsync(actorId, clusterId, expected, ct, async (connection, tx, cluster) =>
        {
            RequireStatus(cluster, "claimed", "OPEN", "WATCHING");
            if (cluster.AssigneeId == actorId) return clusterId;
            if (cluster.AssigneeId is { } other) throw Conflict($"The cluster is already assigned to {other}.");
            await using (var update = AggregationSql.Command(connection, tx, "UPDATE tt.agg_clusters SET assignee_id = @actor WHERE id = @id")
                .WithUuid("actor", actorId).WithUuid("id", clusterId))
            {
                await update.ExecuteNonQueryAsync(ct);
            }

            await ActionAsync(connection, tx, clusterId, "CLAIM", actorId, null, null, null, ct);
            return clusterId;
        });

    /// <summary>Gắn bài đã đưa tin (BR-22): bài phải tồn tại và thuộc site của cụm (422); cụm đã COVERED → 409. Mục thấy sau đó → NEW_SINCE_COVERED.</summary>
    public Task<AggregationClusterDetail> CoverAsync(Guid actorId, Guid clusterId, long expected, Guid articleId, CancellationToken ct) =>
        MutateAsync(actorId, clusterId, expected, ct, async (connection, tx, cluster) =>
        {
            if (cluster.Status == "COVERED") throw Conflict("The cluster is already linked to an article.");
            await using (var article = AggregationSql.Command(connection, tx, "SELECT primary_site_key FROM tt.articles WHERE id = @id FOR SHARE")
                .WithUuid("id", articleId))
            {
                var site = await article.ExecuteScalarAsync(ct) as string;
                if (site is null) throw Invalid("articleId", "No article with this id.");
                if (site != cluster.SiteKey) throw Invalid("articleId", $"The article belongs to site '{site}', not '{cluster.SiteKey}'.");
            }

            await using (var update = AggregationSql.Command(connection, tx, """
                UPDATE tt.agg_clusters SET status = 'COVERED', covered_article_id = @article, covered_at = now() WHERE id = @id
                """).WithUuid("article", articleId).WithUuid("id", clusterId))
            {
                await update.ExecuteNonQueryAsync(ct);
            }

            await ActionAsync(connection, tx, clusterId, "COVER", actorId, null, null, articleId, ct);
            await ClusterStore.RecomputeAsync(connection, tx, clusterId, options.Scoring, time.GetUtcNow(), ct);
            return clusterId;
        });

    /// <summary>Tách các mục chọn sang cụm mới (OPEN, cùng site). Mục phải thuộc cụm (422) và phải còn ít nhất một mục ở lại (422); cụm CLOSED → 409.</summary>
    public Task<AggregationClusterDetail> SplitAsync(Guid actorId, Guid clusterId, long expected, AggregationSplitRequest request, CancellationToken ct)
    {
        var itemIds = request.ItemIds!;
        if (itemIds.Distinct().Count() != itemIds.Count) throw Invalid("itemIds", "Item ids must be unique.");
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        return MutateAsync(actorId, clusterId, expected, ct, async (connection, tx, cluster) =>
        {
            if (cluster.Status == "CLOSED") throw Conflict("A CLOSED cluster cannot be split.");
            var members = new HashSet<Guid>();
            await using (var command = AggregationSql.Command(connection, tx, "SELECT id FROM tt.agg_items WHERE cluster_id = @id FOR UPDATE").WithUuid("id", clusterId))
            await using (var r = await command.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct)) members.Add(r.GetGuid(0));
            }

            if (itemIds.FirstOrDefault(i => !members.Contains(i)) is var stranger && stranger != Guid.Empty) throw Invalid("itemIds", $"Item {stranger} is not in this cluster.");
            if (itemIds.Count >= members.Count) throw Invalid("itemIds", "Leave at least one item in the cluster; to move everything, merge instead.");

            Guid created;
            await using (var insert = AggregationSql.Command(connection, tx, """
                INSERT INTO tt.agg_clusters (site_key, title, representative_item_id)
                SELECT @site, i.title, i.id FROM tt.agg_items i WHERE i.id = @first RETURNING id
                """).WithText("site", cluster.SiteKey).WithUuid("first", itemIds[0]))
            {
                created = (Guid)(await insert.ExecuteScalarAsync(ct))!;
            }

            await MoveItemsAsync(connection, tx, itemIds, created, ct);
            await ActionAsync(connection, tx, clusterId, "SPLIT", actorId, reason, created, null, ct);
            await ActionAsync(connection, tx, created, "SPLIT", actorId, reason, clusterId, null, ct);
            var now = time.GetUtcNow();
            await ClusterStore.RecomputeAsync(connection, tx, clusterId, options.Scoring, now, ct);
            await ClusterStore.RecomputeAsync(connection, tx, created, options.Scoring, now, ct);
            return created;
        });
    }

    /// <summary>
    /// Gộp cụm (path) vào cụm đích: cùng site (422), đích khác chính nó và tồn tại (422), đích không CLOSED và cụm nguồn không CLOSED/COVERED (409).
    /// Cụm nguồn → CLOSED, giữ ảnh chụp số liệu lúc gộp; trả cụm đích.
    /// </summary>
    public Task<AggregationClusterDetail> MergeAsync(Guid actorId, Guid clusterId, long expected, AggregationMergeRequest request, CancellationToken ct)
    {
        var targetId = request.TargetClusterId!.Value;
        if (targetId == clusterId) throw Invalid("targetClusterId", "A cluster cannot be merged into itself.");
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        return MutateAsync(actorId, clusterId, expected, ct, async (connection, tx, cluster) =>
        {
            if (cluster.Status is "CLOSED" or "COVERED") throw Conflict($"A {cluster.Status} cluster cannot be merged into another one.");
            await using (var target = AggregationSql.Command(connection, tx, "SELECT site_key, status FROM tt.agg_clusters WHERE id = @id FOR UPDATE")
                .WithUuid("id", targetId))
            await using (var r = await target.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) throw Invalid("targetClusterId", "No cluster with this id.");
                if (r.GetString(0) != cluster.SiteKey) throw Invalid("targetClusterId", "Both clusters must belong to the same site.");
                if (r.GetString(1) == "CLOSED") throw Conflict("The target cluster is CLOSED.");
            }

            var moved = new List<Guid>();
            await using (var command = AggregationSql.Command(connection, tx, "SELECT id FROM tt.agg_items WHERE cluster_id = @id FOR UPDATE").WithUuid("id", clusterId))
            await using (var r = await command.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct)) moved.Add(r.GetGuid(0));
            }

            await MoveItemsAsync(connection, tx, moved, targetId, ct);
            await SetStatusAsync(connection, tx, clusterId, "CLOSED", ct);
            await ActionAsync(connection, tx, clusterId, "MERGE", actorId, reason, targetId, null, ct);
            await ActionAsync(connection, tx, targetId, "MERGE", actorId, reason, clusterId, null, ct);
            await ClusterStore.RecomputeAsync(connection, tx, targetId, options.Scoring, time.GetUtcNow(), ct);
            return targetId;
        });
    }

    // ---------------------------------------------------------------- thống kê

    /// <summary>getAdminAggregationStats: số liệu một ngày (giờ tòa soạn) của site + sức khỏe từng nguồn cấp cho site đó.</summary>
    public async Task<AggregationStats> StatsAsync(string siteKey, DateOnly? date, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        var day = date ?? clock.Today(time.GetUtcNow());
        var (from, to) = clock.DayRange(day);

        int itemsToday, clustersToday, openToday;
        double? successRate;
        await using (var counts = AggregationSql.Command(connection, null, $"""
            SELECT (SELECT count(*)::int FROM tt.agg_items i WHERE i.site_key = @site AND {AggregationSql.ItemEffectiveAt} >= @from AND {AggregationSql.ItemEffectiveAt} < @to),
                   (SELECT count(*)::int FROM tt.agg_clusters c WHERE c.site_key = @site AND c.last_item_at >= @from AND c.first_seen_at < @to),
                   (SELECT count(*)::int FROM tt.agg_clusters c WHERE c.site_key = @site AND c.last_item_at >= @from AND c.first_seen_at < @to AND c.status = 'OPEN'),
                   (SELECT avg(CASE WHEN l.outcome = 'FAILED' THEN 0.0 ELSE 1.0 END)::float8
                      FROM tt.agg_fetch_log l JOIN tt.agg_feeds f ON f.id = l.feed_id JOIN tt.agg_source_profiles p ON p.content_source_id = f.content_source_id
                     WHERE @site = ANY(p.site_keys) AND l.started_at >= now() - interval '24 hours')
            """).WithText("site", siteKey).WithTime("from", from).WithTime("to", to))
        await using (var r = await counts.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (itemsToday, clustersToday, openToday, successRate) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetDouble(3));
        }

        var byStatus = SourceStatuses.All.Order(StringComparer.Ordinal).ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        var sources = new List<AggregationSourceHealth>();
        await using (var command = AggregationSql.Command(connection, null, AggregationSql.SourceSelect + $"""
             WHERE @site = ANY(p.site_keys) ORDER BY cs.name, p.content_source_id
            """).WithText("site", siteKey))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var s = AggregationSql.ReadSource(r).Summary;
                byStatus[s.Status]++;
                sources.Add(new AggregationSourceHealth(s.Id, s.Name, s.Status, s.WorstFeedHealth, 0, null));
            }
        }

        var perSource = new Dictionary<Guid, (int Items, DateTimeOffset? LastSuccess)>();
        await using (var command = AggregationSql.Command(connection, null, $"""
            SELECT p.content_source_id,
                   (SELECT count(*)::int FROM tt.agg_items i WHERE i.content_source_id = p.content_source_id AND i.site_key = @site
                       AND {AggregationSql.ItemEffectiveAt} >= @from AND {AggregationSql.ItemEffectiveAt} < @to),
                   (SELECT max(f.last_success_at) FROM tt.agg_feeds f WHERE f.content_source_id = p.content_source_id)
              FROM tt.agg_source_profiles p WHERE @site = ANY(p.site_keys)
            """).WithText("site", siteKey).WithTime("from", from).WithTime("to", to))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) perSource[r.GetGuid(0)] = (r.GetInt32(1), AggregationSql.NullableTime(r, 2));
        }

        var health = sources.Select(s => perSource.TryGetValue(s.SourceId, out var x) ? s with { ItemsToday = x.Items, LastSuccessAt = x.LastSuccess } : s).ToList();

        // Giám sát worker (nhịp tim 7 ngày) + cảnh báo nguồn của site (7 ngày, mới trước) — NFR-AGG-06, BR-06.
        var workers = new List<AggregationWorkerStatus>();
        await using (var command = AggregationSql.Command(connection, null, """
            SELECT worker_id, host, version, started_at, last_seen_at, last_seen_at >= now() - interval '5 minutes', last_error
              FROM tt.agg_worker_heartbeats WHERE last_seen_at >= now() - interval '7 days' ORDER BY last_seen_at DESC LIMIT 50
            """))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                workers.Add(new AggregationWorkerStatus(r.GetString(0), AggregationSql.NullableText(r, 1), AggregationSql.NullableText(r, 2),
                    r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4), r.GetBoolean(5), AggregationSql.NullableText(r, 6)));
            }
        }

        var alerts = new List<AggregationAlert>();
        await using (var command = AggregationSql.Command(connection, null, """
            SELECT a.kind, a.content_source_id, cs.name, a.message, a.created_at, a.delivered_at IS NOT NULL
              FROM tt.agg_alerts a
              JOIN tt.agg_source_profiles p ON p.content_source_id = a.content_source_id
              JOIN tt.content_sources cs ON cs.id = a.content_source_id
             WHERE @site = ANY(p.site_keys) AND a.created_at >= now() - interval '7 days'
             ORDER BY a.created_at DESC, a.id DESC LIMIT 50
            """).WithText("site", siteKey))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                alerts.Add(new AggregationAlert(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetBoolean(5)));
            }
        }

        return new AggregationStats(siteKey, day, clock.Id, itemsToday, clustersToday, openToday, successRate, byStatus, health, workers, alerts);
    }

    // ---------------------------------------------------------------- nội bộ

    private sealed record LockedCluster(string SiteKey, string Status, Guid? AssigneeId);

    /// <summary>Khung chung: cờ trong transaction → khóa site → khóa cụm (404) → If-Match (412) → thao tác → đọc lại cụm trả về.</summary>
    private Task<AggregationClusterDetail> MutateAsync(Guid actorId, Guid clusterId, long expected, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, LockedCluster, Task<Guid>> act) =>
        database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await AggregationSql.RequireRadarEnabledAsync(connection, tx, ct);
            string site;
            await using (var find = AggregationSql.Command(connection, tx, "SELECT site_key FROM tt.agg_clusters WHERE id = @id").WithUuid("id", clusterId))
            {
                site = await find.ExecuteScalarAsync(ct) as string ?? throw NotFound();
            }

            await ClusterStore.LockSitesAsync(connection, tx, [site], ct);
            LockedCluster cluster;
            await using (var command = AggregationSql.Command(connection, tx,
                "SELECT site_key, status, assignee_id, row_version FROM tt.agg_clusters WHERE id = @id FOR UPDATE").WithUuid("id", clusterId))
            await using (var r = await command.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) throw NotFound();
                ETags.EnsureMatches(r.GetInt64(3), expected);
                cluster = new LockedCluster(r.GetString(0), r.GetString(1), AggregationSql.NullableGuid(r, 2));
            }

            var resultId = await act(connection, tx, cluster);
            return (await LoadAsync(connection, tx, resultId, ct))!;
        }, ct);

    private static void RequireStatus(LockedCluster cluster, string verb, params string[] allowed)
    {
        if (!allowed.Contains(cluster.Status)) throw Conflict($"A {cluster.Status} cluster cannot be {verb} (allowed from {string.Join(", ", allowed)}).");
    }

    private static async Task SetStatusAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid clusterId, string status, CancellationToken ct)
    {
        await using var update = AggregationSql.Command(connection, tx, "UPDATE tt.agg_clusters SET status = @status WHERE id = @id")
            .WithText("status", status).WithUuid("id", clusterId);
        await update.ExecuteNonQueryAsync(ct);
    }

    private static async Task MoveItemsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<Guid> itemIds, Guid targetId, CancellationToken ct)
    {
        await using var move = AggregationSql.Command(connection, tx, "UPDATE tt.agg_items SET cluster_id = @target WHERE id = ANY(@ids)")
            .WithUuid("target", targetId).WithUuidArray("ids", itemIds);
        await move.ExecuteNonQueryAsync(ct);
    }

    private static async Task ActionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid clusterId, string action, Guid actorId, string? reason,
        Guid? relatedClusterId, Guid? articleId, CancellationToken ct)
    {
        await using var insert = AggregationSql.Command(connection, tx, """
            INSERT INTO tt.agg_cluster_actions (cluster_id, action, actor_id, reason, related_cluster_id, article_id)
            VALUES (@cluster, @action, @actor, @reason, @related, @article)
            """).WithUuid("cluster", clusterId).WithText("action", action).WithUuid("actor", actorId).WithText("reason", reason)
            .WithUuid("related", relatedClusterId).WithUuid("article", articleId);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task<AggregationClusterDetail?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid clusterId, CancellationToken ct)
    {
        AggregationClusterSummary summary;
        await using (var command = AggregationSql.Command(connection, tx, SummarySelect + " WHERE c.id = @id").WithUuid("id", clusterId))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            summary = ReadSummary(r);
        }

        var items = new List<AggregationItem>();
        await using (var command = AggregationSql.Command(connection, tx, AggregationSql.ItemSelect
            + $" WHERE i.cluster_id = @id AND i.url IS NOT NULL ORDER BY {AggregationSql.ItemEffectiveAt}, i.id").WithUuid("id", clusterId))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) items.Add(AggregationSql.ReadItem(r));
        }

        var actions = new List<AggregationClusterAction>();
        await using (var command = AggregationSql.Command(connection, tx, """
            SELECT action, actor_id, created_at, reason, related_cluster_id, article_id FROM tt.agg_cluster_actions
             WHERE cluster_id = @id ORDER BY created_at DESC, id DESC
            """).WithUuid("id", clusterId))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                actions.Add(new AggregationClusterAction(r.GetString(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), AggregationSql.NullableText(r, 3),
                    AggregationSql.NullableGuid(r, 4), AggregationSql.NullableGuid(r, 5)));
            }
        }

        return AggregationClusterDetail.From(summary, items, actions);
    }

    private static AggregationClusterSummary ReadSummary(DbDataReader r) => new(
        Id: r.GetGuid(0),
        SiteKey: r.GetString(1),
        Title: r.GetString(2),
        RepresentativeItemId: r.GetGuid(3),
        PrimaryCategoryId: AggregationSql.NullableGuid(r, 4),
        CategoryNeedsReview: r.GetBoolean(5),
        Score: r.GetDecimal(6),
        Status: r.GetString(7),
        SourceCount: r.GetInt32(8),
        IndependentSourceCount: r.GetInt32(9),
        ItemCount: r.GetInt32(10),
        FirstSeenAt: r.GetFieldValue<DateTimeOffset>(11),
        LastItemAt: r.GetFieldValue<DateTimeOffset>(12),
        Labels: r.GetFieldValue<string[]>(13),
        MaxUsageMode: r.GetString(14),
        AssigneeId: AggregationSql.NullableGuid(r, 15),
        CoveredArticleId: AggregationSql.NullableGuid(r, 16),
        RowVersion: r.GetInt64(17));

    private static DomainProblemException NotFound() => new(ErrorCodes.NotFound, "Not found", "No aggregation cluster with this id.");

    private static DomainProblemException Conflict(string detail) => new(ErrorCodes.StateConflict, "State conflict", detail);

    private static DomainProblemException Invalid(string field, string message) =>
        new(ErrorCodes.ValidationFailed, "Validation failed", message, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Modules.Aggregation.Infrastructure.Collection;
using Tinhte.Platform.Data;
using Tinhte.Platform.Flags;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>Feed đã thuê (lease) cho một worker. <c>LeaseUntil</c> là "token": chỉ ghi kết quả nếu lease vẫn là của mình.</summary>
public sealed record FeedLease(
    Guid FeedId,
    Guid SourceId,
    string Url,
    string Format,
    int ScheduleMinutes,
    string? ETag,
    string? LastModified,
    bool FirstFetch,
    DateTimeOffset LeaseUntil);

/// <summary>Kết quả xử lý một feed. <c>Committed = false</c>: lease đã mất (hết hạn/feed bị sửa) hoặc cờ tắt — không ghi gì.</summary>
public sealed record FeedRunResult(Guid FeedId, string Outcome, string? Failure, int ItemsSeen, int ItemsNew, int ItemsUpdated, bool Committed);

/// <summary>
/// Thu thập feed cho worker (ADR-0008 §3–§7). Mỗi vòng:
/// <list type="number">
///   <item>cờ <c>aggregation.radar</c> OFF ⇒ không làm gì (ADR-0008 §8);</item>
///   <item>thuê tối đa N feed đến hạn của nguồn ENABLED/DEGRADED bằng <c>FOR UPDATE SKIP LOCKED</c> + đặt <c>lease_until</c>, commit ngay —
///         nhiều worker song song không lấy trùng; worker chết thì lease hết hạn và feed được lấy lại;</item>
///   <item>gọi HTTP NGOÀI transaction; rồi trong MỘT transaction: kiểm lại cờ + lease, ghi/cập nhật mục tin (khử trùng, phân loại),
///         <c>agg_fetch_log</c>, trạng thái feed (ETag, sức khỏe, lịch kế tiếp/backoff), sức khỏe nguồn (BR-06), xóa lease.</item>
/// </list>
/// Khóa theo thứ tự hồ sơ nguồn → feed như thao tác CMS (tránh deadlock).
/// </summary>
public sealed partial class FeedCollectionService(
    ITinhteDatabase database,
    FeedFetcher fetcher,
    AggregationOptions options,
    NewsroomClock clock,
    SensitiveTopics sensitiveTopics,
    TimeProvider time,
    ILogger<FeedCollectionService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Một vòng quét. null = cờ OFF (không thu thập); ngược lại = số feed đã thuê.</summary>
    public async Task<int?> RunOnceAsync(CancellationToken ct)
    {
        if (!await RadarEnabledAsync(ct)) return null;
        var leases = await LeaseAsync(options.Parallelism, ct);
        await Parallel.ForEachAsync(leases, new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism, CancellationToken = ct },
            async (lease, token) => await ProcessAsync(lease, token));
        if (time.GetUtcNow() >= _nextMaintenance)
        {
            await MaintainAsync(ct);
            _nextMaintenance = time.GetUtcNow() + options.MaintenanceInterval;
        }

        return leases.Count;
    }

    private DateTimeOffset _nextMaintenance = DateTimeOffset.MinValue;

    /// <summary>
    /// Bảo trì cụm theo từng site (mỗi site một transaction, khóa tư vấn theo site): đóng cụm OPEN hết cửa sổ 48 giờ, gom bù mục chưa vào cụm,
    /// tính lại điểm cụm đang mở. Nhiều worker chạy cùng lúc vẫn an toàn (khóa theo site, thao tác lặp lại không đổi kết quả).
    /// </summary>
    public async Task MaintainAsync(CancellationToken ct)
    {
        var sites = new List<string>();
        await using (var connection = await database.OpenConnectionAsync(ct))
        await using (var command = AggregationSql.Command(connection, null, """
            SELECT site_key FROM tt.agg_clusters WHERE status IN ('OPEN', 'WATCHING')
            UNION
            SELECT site_key FROM tt.agg_items WHERE status = 'CLASSIFIED' AND cluster_id IS NULL AND site_key IS NOT NULL
            """))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) sites.Add(r.GetString(0));
        }

        foreach (var site in sites.Order(StringComparer.Ordinal))
        {
            var (closed, clustered, refreshed) = await database.InTransactionAsync(null, (connection, tx) =>
                ClusterStore.MaintainSiteAsync(connection, tx, site, options.Clustering, options.Scoring, time.GetUtcNow(), ct), ct);
            LogMaintained(logger, site, closed, clustered, refreshed);
        }
    }

    public async Task<bool> RadarEnabledAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = AggregationSql.Command(connection, null, "SELECT COALESCE((SELECT enabled FROM tt.feature_gates WHERE key = @key), false)")
            .WithText("key", FeatureFlagKeys.AggregationRadar);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Thuê tối đa <paramref name="max"/> feed đến hạn (một câu lệnh, tự commit).</summary>
    public async Task<IReadOnlyList<FeedLease>> LeaseAsync(int max, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = AggregationSql.Command(connection, null, """
            WITH due AS (
              SELECT f.id
                FROM tt.agg_feeds f
                JOIN tt.agg_source_profiles p ON p.content_source_id = f.content_source_id
               WHERE f.is_enabled AND f.next_fetch_at <= now() AND (f.lease_until IS NULL OR f.lease_until < now())
                 AND p.status IN ('ENABLED', 'DEGRADED')
               ORDER BY f.next_fetch_at, f.id
               LIMIT @max
               FOR UPDATE OF f SKIP LOCKED)
            UPDATE tt.agg_feeds f
               SET lease_until = now() + @lease_seconds * interval '1 second'
              FROM due
             WHERE f.id = due.id
            RETURNING f.id, f.content_source_id, f.url, f.format, f.schedule_minutes, f.etag, f.last_modified, f.last_success_at IS NULL, f.lease_until
            """).WithInt("max", max).WithInt("lease_seconds", (int)options.LeaseDuration.TotalSeconds);
        var leases = new List<FeedLease>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            leases.Add(new FeedLease(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt32(4), AggregationSql.NullableText(r, 5),
                AggregationSql.NullableText(r, 6), r.GetBoolean(7), r.GetFieldValue<DateTimeOffset>(8)));
        }

        return leases;
    }

    /// <summary>Tải + ghi một feed đã thuê. Không ném lỗi của nguồn (đó là dữ liệu); lỗi hạ tầng bất ngờ → ghi PARSE_ERROR + backoff.</summary>
    public async Task<FeedRunResult> ProcessAsync(FeedLease lease, CancellationToken ct)
    {
        var started = time.GetUtcNow();
        var fetch = await fetcher.FetchAsync(lease.Url, lease.ETag, lease.LastModified, ct);
        var outcome = fetch.Outcome;
        var failure = fetch.Failure;
        ParsedFeed? parsed = null;
        if (outcome == FetchOutcomes.Ok)
        {
            try
            {
                parsed = FeedParser.Parse(fetch.Body!, fetch.Charset, lease.Format, clock.Zone, new Uri(lease.Url));
            }
            catch (FeedParseException e)
            {
                (outcome, failure) = (FetchOutcomes.Failed, FeedFailures.ParseError);
                LogParseFailed(logger, lease.FeedId, e.Message);
            }
        }

        if (fetch.Unsafe) LogUnsafeTarget(logger, lease.FeedId, fetch.Detail);
        CollectorMetrics.Fetches.Add(1, new KeyValuePair<string, object?>("outcome", outcome), new KeyValuePair<string, object?>("failure", failure ?? "none"));
        var attempt = new Attempt(started, outcome, failure, fetch.HttpStatus, fetch.BytesRead, fetch.ETag, fetch.LastModified, fetch.RetryAfter, parsed);
        try
        {
            var result = await RecordAsync(lease, attempt, ct);
            CollectorMetrics.ItemsNew.Add(result.ItemsNew);
            CollectorMetrics.ItemsUpdated.Add(result.ItemsUpdated);
            CollectorMetrics.FetchDuration.Record((time.GetUtcNow() - started).TotalMilliseconds);
            return result;
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or JsonException)
        {
            // Dữ liệu của feed không ghi được (ràng buộc DB…): ghi lỗi + lùi lịch để không lặp lại mỗi vòng quét.
            LogRecordFailed(logger, e, lease.FeedId);
            var fallback = attempt with { Outcome = FetchOutcomes.Failed, Failure = FeedFailures.ParseError, Parsed = null };
            return await RecordAsync(lease, fallback, ct);
        }
    }

    private sealed record Attempt(DateTimeOffset StartedAt, string Outcome, string? Failure, int? HttpStatus, int BytesRead, string? ETag,
        string? LastModified, TimeSpan? RetryAfter, ParsedFeed? Parsed);

    private sealed record LockedFeed(bool Collecting, int ConsecutiveFailures, string Mapping, IReadOnlyList<string> SiteKeys, string Language);

    private Task<FeedRunResult> RecordAsync(FeedLease lease, Attempt attempt, CancellationToken ct) =>
        database.InTransactionAsync(null, async (connection, tx) =>
        {
            var notCommitted = new FeedRunResult(lease.FeedId, attempt.Outcome, attempt.Failure, 0, 0, 0, false);
            await using (var flag = AggregationSql.Command(connection, tx, "SELECT tt.flag_enabled_for_write(@key)").WithText("key", FeatureFlagKeys.AggregationRadar))
            {
                if (await flag.ExecuteScalarAsync(ct) is not true)
                {
                    await ReleaseAsync(connection, tx, lease, ct);
                    return notCommitted;
                }
            }

            var feed = await LockAsync(connection, tx, lease, ct);
            if (feed is null) return notCommitted;   // lease đã mất: worker khác/CMS đã đổi feed — không ghi gì

            var now = time.GetUtcNow();
            var (seen, created, updated) = (0, 0, 0);
            if (feed.Collecting && attempt.Parsed is { } parsed)
            {
                var changedIds = new List<Guid>();
                (seen, created, updated) = await UpsertItemsAsync(connection, tx, lease, feed, parsed, now, changedIds, ct);
                await ClusterStore.ClusterItemsAsync(connection, tx, changedIds, options.Clustering, options.Scoring, now, ct);   // ADR-0008 §7
            }

            await using (var log = AggregationSql.Command(connection, tx, """
                INSERT INTO tt.agg_fetch_log (feed_id, started_at, finished_at, outcome, http_status, failure, bytes_read, items_seen, items_new, items_updated)
                VALUES (@feed, @started, now(), @outcome, @status, @failure, @bytes, @seen, @new, @updated)
                """)
                .WithUuid("feed", lease.FeedId).WithTime("started", attempt.StartedAt).WithText("outcome", attempt.Outcome)
                .WithSmallInt("status", attempt.HttpStatus).WithText("failure", attempt.Failure).WithInt("bytes", attempt.BytesRead)
                .WithInt("seen", seen).WithInt("new", created).WithInt("updated", updated))
            {
                await log.ExecuteNonQueryAsync(ct);
            }

            if (!feed.Collecting)
            {
                // Nguồn bị tạm dừng/gửi lại duyệt hoặc feed bị tắt trong lúc tải: bỏ kết quả, giữ lịch do CMS quản lý.
                await ReleaseAsync(connection, tx, lease, ct);
                return new FeedRunResult(lease.FeedId, attempt.Outcome, attempt.Failure, seen, 0, 0, true);
            }

            if (attempt.Outcome == FetchOutcomes.Failed)
            {
                var failures = feed.ConsecutiveFailures + 1;
                var next = FetchSchedule.AfterFailure(now, lease.ScheduleMinutes, failures, attempt.RetryAfter, Random.Shared.NextDouble());
                await using (var update = AggregationSql.Command(connection, tx, """
                    UPDATE tt.agg_feeds
                       SET health = @health, last_fetched_at = now(), consecutive_failures = @failures, last_failure = @failure,
                           failing_since = COALESCE(failing_since, now()), next_fetch_at = @next, lease_until = NULL
                     WHERE id = @id AND lease_until = @lease
                    """)
                    .WithText("health", FetchSchedule.HealthAfterFailure(attempt.Failure!, failures)).WithInt("failures", failures)
                    .WithText("failure", attempt.Failure).WithTime("next", next).WithUuid("id", lease.FeedId).WithTime("lease", lease.LeaseUntil))
                {
                    await update.ExecuteNonQueryAsync(ct);
                }

                await UpdateSourceHealthAfterFailureAsync(connection, tx, lease, failures, attempt.Failure!, ct);
            }
            else
            {
                var next = FetchSchedule.AfterSuccess(now, lease.ScheduleMinutes, attempt.Parsed?.RefreshMinutesHint, Random.Shared.NextDouble());
                await using (var update = AggregationSql.Command(connection, tx, """
                    UPDATE tt.agg_feeds
                       SET etag = @etag, last_modified = @last_modified, health = 'OK', last_fetched_at = now(), last_success_at = now(),
                           consecutive_failures = 0, last_failure = NULL, failing_since = NULL, next_fetch_at = @next, lease_until = NULL
                     WHERE id = @id AND lease_until = @lease
                    """)
                    .WithText("etag", Cap(attempt.ETag, 500)).WithText("last_modified", Cap(attempt.LastModified, 100)).WithTime("next", next)
                    .WithUuid("id", lease.FeedId).WithTime("lease", lease.LeaseUntil))
                {
                    await update.ExecuteNonQueryAsync(ct);
                }

                await using var recover = AggregationSql.Command(connection, tx, """
                    UPDATE tt.agg_source_profiles SET status = 'ENABLED'
                     WHERE content_source_id = @source AND status = 'DEGRADED'
                       AND NOT EXISTS (SELECT 1 FROM tt.agg_feeds f WHERE f.content_source_id = @source AND f.is_enabled AND f.consecutive_failures >= @limit)
                    """).WithUuid("source", lease.SourceId).WithInt("limit", FetchSchedule.DegradedAfterFailures);
                await recover.ExecuteNonQueryAsync(ct);
            }

            return new FeedRunResult(lease.FeedId, attempt.Outcome, attempt.Failure, seen, created, updated, true);
        }, ct);

    /// <summary>Khóa hồ sơ nguồn rồi feed (thứ tự như CMS); null nếu lease không còn của mình.</summary>
    private static async Task<LockedFeed?> LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, FeedLease lease, CancellationToken ct)
    {
        string status;
        string[] sites;
        string language;
        await using (var profile = AggregationSql.Command(connection, tx,
            "SELECT status, site_keys, language FROM tt.agg_source_profiles WHERE content_source_id = @source FOR UPDATE").WithUuid("source", lease.SourceId))
        await using (var r = await profile.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            (status, sites, language) = (r.GetString(0), r.GetFieldValue<string[]>(1), r.GetString(2));
        }

        await using var feed = AggregationSql.Command(connection, tx, """
            SELECT is_enabled, consecutive_failures, category_mapping::text FROM tt.agg_feeds WHERE id = @id AND lease_until = @lease FOR UPDATE
            """).WithUuid("id", lease.FeedId).WithTime("lease", lease.LeaseUntil);
        await using var f = await feed.ExecuteReaderAsync(ct);
        if (!await f.ReadAsync(ct)) return null;
        return new LockedFeed(f.GetBoolean(0) && SourceStatuses.Collecting.Contains(status), f.GetInt32(1), f.GetString(2), sites, language);
    }

    private static async Task ReleaseAsync(NpgsqlConnection connection, NpgsqlTransaction tx, FeedLease lease, CancellationToken ct)
    {
        await using var command = AggregationSql.Command(connection, tx, "UPDATE tt.agg_feeds SET lease_until = NULL WHERE id = @id AND lease_until = @lease")
            .WithUuid("id", lease.FeedId).WithTime("lease", lease.LeaseUntil);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// BR-06: một feed lỗi ≥ 5 lần liên tiếp ⇒ nguồn ENABLED → DEGRADED; MỌI feed đang bật của nguồn lỗi liên tục ≥ 24 giờ ⇒ tự PAUSED
    /// (lý do ghi vào lịch sử trạng thái, người thao tác = hệ thống). Cần duyệt/tiếp tục bằng tay để thu thập lại.
    /// </summary>
    private async Task UpdateSourceHealthAfterFailureAsync(NpgsqlConnection connection, NpgsqlTransaction tx, FeedLease lease, int failures, string failure,
        CancellationToken ct)
    {
        if (failures >= FetchSchedule.DegradedAfterFailures)
        {
            await using var degrade = AggregationSql.Command(connection, tx,
                "UPDATE tt.agg_source_profiles SET status = 'DEGRADED' WHERE content_source_id = @source AND status = 'ENABLED'").WithUuid("source", lease.SourceId);
            if (await degrade.ExecuteNonQueryAsync(ct) > 0)
            {
                await CollectorOperations.RaiseAsync(connection, tx, lease.SourceId, "SOURCE_DEGRADED",
                    $"Feed {lease.Url} lỗi {failures} lần liên tiếp (gần nhất {failure}); nguồn chuyển DEGRADED.", ct);
            }
        }

        await using (var check = AggregationSql.Command(connection, tx, """
            SELECT NOT EXISTS (SELECT 1 FROM tt.agg_feeds f
                                WHERE f.content_source_id = @source AND f.is_enabled
                                  AND (f.failing_since IS NULL OR f.failing_since > now() - @hours * interval '1 hour'))
            """).WithUuid("source", lease.SourceId).WithInt("hours", (int)FetchSchedule.AutoPauseAfter.TotalHours))
        {
            if (await check.ExecuteScalarAsync(ct) is not true) return;
        }

        await AggregationSql.SetReasonAsync(connection, tx,
            $"Tự động tạm dừng (BR-06): mọi feed đang bật lỗi liên tục ≥ {FetchSchedule.AutoPauseAfter.TotalHours:0} giờ; lỗi gần nhất {failure}.", ct);
        await using var pause = AggregationSql.Command(connection, tx,
            "UPDATE tt.agg_source_profiles SET status = 'PAUSED' WHERE content_source_id = @source AND status IN ('ENABLED', 'DEGRADED')")
            .WithUuid("source", lease.SourceId);
        if (await pause.ExecuteNonQueryAsync(ct) > 0)
        {
            // Cảnh báo ghi cùng transaction với việc tạm dừng (outbox): không mất nếu kênh ngoài lỗi; CMS đọc qua thống kê.
            await CollectorOperations.RaiseAsync(connection, tx, lease.SourceId, "SOURCE_AUTO_PAUSED",
                $"Mọi feed đang bật của nguồn lỗi liên tục ≥ {FetchSchedule.AutoPauseAfter.TotalHours:0} giờ (gần nhất {failure}); nguồn đã tự tạm dừng. "
                + "Kiểm tra feed rồi tiếp tục (resume) trên CMS.", ct);
            LogSourcePaused(logger, lease.SourceId, failure);
        }
    }

    // ---------------------------------------------------------------- mục tin

    private async Task<(int Seen, int Created, int Updated)> UpsertItemsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, FeedLease lease,
        LockedFeed feed, ParsedFeed parsed, DateTimeOffset now, List<Guid> changedIds, CancellationToken ct)
    {
        var classifier = await LoadClassifierAsync(connection, tx, feed, ct);
        var feedUri = new Uri(lease.Url);
        var (created, updated) = (0, 0);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var cutoff = ItemNormalizer.IntakeCutoff(now, lease.FirstFetch, options.TodayOnly, clock);
        foreach (var raw in parsed.Items)
        {
            var item = ItemNormalizer.Normalize(raw, feedUri, now);
            if (item is null || !handled.Add(item.UrlHash)) continue;
            if (item.EffectiveAt < cutoff) continue;   // P-02: chỉ tin trong ngày (hoặc BR-14 khi tắt)

            var (result, id) = await UpsertItemAsync(connection, tx, lease, feed, item, classifier, ct);
            if (result == ItemChange.Created) created++;
            else if (result == ItemChange.Updated) updated++;
            if (result != ItemChange.None && id is { } changedId) changedIds.Add(changedId);
        }

        return (parsed.Items.Count, created, updated);
    }

    private enum ItemChange
    {
        None,
        Created,
        Updated,
    }

    /// <summary>Khử trùng BR-10/13/20: cùng guid HOẶC cùng URL chuẩn hóa ⇒ cùng mục (chịu được guid đổi mỗi lần tải).</summary>
    private static async Task<(ItemChange Change, Guid? Id)> UpsertItemAsync(NpgsqlConnection connection, NpgsqlTransaction tx, FeedLease lease, LockedFeed feed,
        NormalizedItem item, ItemClassifier classifier, CancellationToken ct)
    {
        Guid? existingId = null;
        string? existingHash = null;
        string[] existingCategories = [];
        string? existingStatus = null;
        await using (var find = AggregationSql.Command(connection, tx, """
            SELECT id, content_hash, feed_categories, status FROM tt.agg_items
             WHERE content_source_id = @source AND (guid = @guid OR url_hash = @hash)
             ORDER BY (guid = @guid) IS TRUE DESC
             LIMIT 1 FOR UPDATE
            """).WithUuid("source", lease.SourceId).WithText("guid", item.Guid).WithText("hash", item.UrlHash))
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                (existingId, existingHash, existingCategories, existingStatus) = (r.GetGuid(0), r.GetString(1), r.GetFieldValue<string[]>(2), r.GetString(3));
            }
        }

        var c = classifier.Classify(item);
        if (existingId is null)
        {
            await using var insert = AggregationSql.Command(connection, tx, """
                INSERT INTO tt.agg_items (content_source_id, feed_id, guid, url, url_hash, title, summary, author, feed_categories, language,
                                          published_at, time_reliable, status, site_key, category_id, category_confidence, needs_review, sensitive,
                                          content_hash, source_updated_at, category_ids)
                VALUES (@source, @feed, @guid, @url, @hash, @title, @summary, @author, @categories::varchar(200)[], @language,
                        @published, @reliable, @status, @site, @category, @confidence, @review, @sensitive, @content_hash, @updated, @category_ids)
                ON CONFLICT DO NOTHING
                RETURNING id
                """)
                .WithUuid("source", lease.SourceId).WithUuid("feed", lease.FeedId).WithText("guid", item.Guid).WithText("url", item.Url)
                .WithText("hash", item.UrlHash).WithText("title", item.Title).WithText("summary", item.Summary).WithText("author", item.Author)
                .WithTextArray("categories", item.FeedCategories).WithText("language", feed.Language).WithTime("published", item.PublishedAt)
                .WithBool("reliable", item.TimeReliable).WithText("status", c.Status).WithText("site", c.SiteKey).WithUuid("category", c.CategoryId)
                .WithDecimal("confidence", c.Confidence).WithBool("review", c.NeedsReview).WithBool("sensitive", c.Sensitive)
                .WithText("content_hash", item.ContentHash).WithTime("updated", item.SourceUpdatedAt).WithUuidArray("category_ids", [.. c.CategoryIds]);
            return await insert.ExecuteScalarAsync(ct) is Guid createdId ? (ItemChange.Created, createdId) : (ItemChange.None, null);
        }

        // Cùng bài xuất hiện ở category/feed khác ⇒ gộp category (BR-20).
        var categories = existingCategories.Concat(item.FeedCategories).Distinct(StringComparer.OrdinalIgnoreCase).Take(ItemNormalizer.MaxCategories).ToArray();
        var changed = !string.Equals(existingHash, item.ContentHash, StringComparison.Ordinal);
        if (!changed && categories.Length == existingCategories.Length) return (ItemChange.None, existingId);

        // Mục đã vào cụm/bị đánh trùng giữ phân loại (lát 3 quyết định); mục khác phân loại lại theo nội dung mới.
        var reclassify = changed && existingStatus is "NEW" or ItemClassifier.Classified or ItemClassifier.OutOfScope;
        await using var update = AggregationSql.Command(connection, tx, """
            UPDATE tt.agg_items
               SET feed_categories = @categories::varchar(200)[],
                   title = CASE WHEN @changed THEN @title ELSE title END,
                   summary = CASE WHEN @changed THEN @summary ELSE summary END,
                   author = CASE WHEN @changed THEN COALESCE(@author, author) ELSE author END,
                   content_hash = CASE WHEN @changed THEN @content_hash ELSE content_hash END,
                   version = CASE WHEN @changed THEN version + 1 ELSE version END,
                   source_updated_at = CASE WHEN @changed THEN COALESCE(@updated, now()) ELSE source_updated_at END,
                   status = CASE WHEN @reclassify THEN @status ELSE status END,
                   site_key = CASE WHEN @reclassify THEN @site ELSE site_key END,
                   category_id = CASE WHEN @reclassify THEN @category ELSE category_id END,
                   category_ids = CASE WHEN @reclassify THEN @category_ids ELSE category_ids END,
                   category_confidence = CASE WHEN @reclassify THEN @confidence ELSE category_confidence END,
                   needs_review = CASE WHEN @reclassify THEN @review ELSE needs_review END,
                   sensitive = CASE WHEN @reclassify THEN @sensitive ELSE sensitive END
             WHERE id = @id
            """)
            .WithTextArray("categories", categories).WithBool("changed", changed).WithText("title", item.Title).WithText("summary", item.Summary)
            .WithText("author", item.Author).WithText("content_hash", item.ContentHash).WithTime("updated", item.SourceUpdatedAt)
            .WithBool("reclassify", reclassify).WithText("status", c.Status).WithText("site", c.SiteKey).WithUuid("category", c.CategoryId)
            .WithDecimal("confidence", c.Confidence).WithBool("review", c.NeedsReview).WithBool("sensitive", c.Sensitive).WithUuid("id", existingId)
            .WithUuidArray("category_ids", [.. c.CategoryIds]);
        await update.ExecuteNonQueryAsync(ct);
        return (changed ? ItemChange.Updated : ItemChange.None, existingId);
    }

    /// <summary>Ánh xạ category của feed (chỉ chuyên mục đang bật thuộc site của nguồn) + luật từ khóa của các site đó.</summary>
    private async Task<ItemClassifier> LoadClassifierAsync(NpgsqlConnection connection, NpgsqlTransaction tx, LockedFeed feed, CancellationToken ct)
    {
        var mapping = JsonSerializer.Deserialize<List<AggregationCategoryMapping>>(feed.Mapping, Json) ?? [];
        var categories = new Dictionary<Guid, string>();
        var ids = mapping.Where(m => m.CategoryId is not null).Select(m => m.CategoryId!.Value).Distinct().ToList();
        if (ids.Count > 0)
        {
            await using var command = AggregationSql.Command(connection, tx,
                "SELECT id, site_key FROM tt.categories WHERE id = ANY(@ids) AND is_enabled AND site_key = ANY(@sites)")
                .WithUuidArray("ids", ids).WithTextArray("sites", feed.SiteKeys);
            await using var r = await command.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) categories[r.GetGuid(0)] = r.GetString(1);
        }

        var mapped = new Dictionary<string, (Guid, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mapping)
        {
            if (m.CategoryId is { } id && categories.TryGetValue(id, out var site)) mapped.TryAdd(m.FeedCategory.Trim(), (id, site));
        }

        var rules = new List<CategoryRuleSet>();
        await using (var command = AggregationSql.Command(connection, tx, """
            SELECT r.category_id, c.site_key, r.include_keywords, r.exclude_keywords, r.entity_keywords
              FROM tt.agg_category_rules r JOIN tt.categories c ON c.id = r.category_id
             WHERE c.is_enabled AND c.site_key = ANY(@sites)
             ORDER BY c.site_key, c.name, c.id
            """).WithTextArray("sites", feed.SiteKeys))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                rules.Add(new CategoryRuleSet(r.GetGuid(0), r.GetString(1), r.GetFieldValue<string[]>(2), r.GetFieldValue<string[]>(3), r.GetFieldValue<string[]>(4)));
            }
        }

        return new ItemClassifier(feed.SiteKeys, mapped, rules, sensitiveTopics, options.ThresholdsFor);
    }

    private static string? Cap(string? value, int max) => value is null || value.Length <= max ? value : null;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clusters of site {SiteKey}: {Closed} closed, {Clustered} items clustered, {Refreshed} rescored")]
    private static partial void LogMaintained(ILogger logger, string siteKey, int closed, int clustered, int refreshed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Feed {FeedId}: parse failed ({Reason})")]
    private static partial void LogParseFailed(ILogger logger, Guid feedId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feed {FeedId}: unsafe target refused ({Detail})")]
    private static partial void LogUnsafeTarget(ILogger logger, Guid feedId, string? detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Feed {FeedId}: collected data could not be stored; recorded as PARSE_ERROR with backoff")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, Guid feedId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source {SourceId} paused automatically: all feeds failing for 24 hours (last {Failure})")]
    private static partial void LogSourcePaused(ILogger logger, Guid sourceId, string failure);
}

using Npgsql;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>
/// Feed của nguồn (FR-AGG-001/002). Thêm feed hoặc đổi URL ⇒ hồ sơ nguồn phải duyệt lại (BR-01): xóa người duyệt hiện hành,
/// ENABLED/DEGRADED → PROPOSED (PAUSED giữ PAUSED nhưng không resume được tới khi duyệt lại). Feed không xóa, chỉ tắt.
/// Lịch thu thập: <c>next_fetch_at</c> = now() khi bật/đổi URL, NULL khi tắt; worker (Tinhte.Worker) chỉ lấy feed của nguồn ENABLED/DEGRADED.
/// </summary>
public sealed partial class AggregationSourceService
{
    // ---------------------------------------------------------------- tạo (createAdminAggregationFeed)

    public async Task<AggregationFeed> CreateFeedAsync(Guid actorId, Guid sourceId, AggregationFeedCreate request, CancellationToken ct)
    {
        var format = request.Format ?? FeedFormats.Auto;
        if (!FeedFormats.All.Contains(format)) throw Invalid("format", $"One of {string.Join(", ", FeedFormats.All.Order(StringComparer.Ordinal))}.");
        var mapping = CheckMapping(request.CategoryMapping ?? []);

        // Kiểm SSRF (có DNS) ngoài transaction: không giữ khóa trong lúc chờ mạng.
        var url = await urlPolicy.CheckAsync("url", request.Url, ct);

        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await AggregationSql.RequireRadarEnabledAsync(connection, tx, ct);
            var profile = await LockAsync(connection, tx, sourceId, ct) ?? throw NotFound();
            RequireNotRetired(profile);
            await EnsureUrlFreeAsync(connection, tx, url, null, ct);
            await EnsureCategoriesAsync(connection, tx, mapping, profile.SiteKeys, ct);

            Guid feedId;
            await using (var insert = AggregationSql.Command(connection, tx, """
                INSERT INTO tt.agg_feeds (content_source_id, url, format, schedule_minutes, category_mapping, next_fetch_at, created_by)
                VALUES (@source, @url, @format, @schedule, @mapping, now(), @actor)
                RETURNING id
                """)
                .WithUuid("source", sourceId).WithText("url", url).WithText("format", format)
                .WithInt("schedule", request.ScheduleMinutes ?? AggregationLimits.DefaultScheduleMinutes)
                .WithJsonb("mapping", AggregationSql.MappingJson(mapping)).WithUuid("actor", actorId))
            {
                feedId = (Guid)(await insert.ExecuteScalarAsync(ct))!;
            }

            await SendBackForApprovalAsync(connection, tx, sourceId, ct);
            return (await LoadFeedAsync(connection, tx, feedId, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- sửa (patchAdminAggregationFeed)

    /// <summary>Kiểm cờ + tồn tại feed (404 trước 428/412).</summary>
    public async Task EnsureFeedExistsAsync(Guid feedId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        await using var command = AggregationSql.Command(connection, null, "SELECT 1 FROM tt.agg_feeds WHERE id = @id").WithUuid("id", feedId);
        if (await command.ExecuteScalarAsync(ct) is null) throw FeedNotFound();
    }

    public async Task<AggregationFeed> PatchFeedAsync(Guid actorId, Guid feedId, long expected, AggregationFeedPatch patch, CancellationToken ct)
    {
        var mapping = patch.CategoryMapping is { } m ? CheckMapping(m) : null;
        var url = patch.Url is { } raw ? await urlPolicy.CheckAsync("url", raw, ct) : null;

        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await AggregationSql.RequireRadarEnabledAsync(connection, tx, ct);

            // Khóa theo thứ tự hồ sơ → feed (giống lúc tạo feed) để hai thao tác đồng thời không khóa chéo.
            Guid sourceId;
            await using (var find = AggregationSql.Command(connection, tx, "SELECT content_source_id FROM tt.agg_feeds WHERE id = @id").WithUuid("id", feedId))
            {
                sourceId = await find.ExecuteScalarAsync(ct) as Guid? ?? throw FeedNotFound();
            }

            var profile = (await LockAsync(connection, tx, sourceId, ct))!;
            string currentUrl;
            long rowVersion;
            await using (var lockFeed = AggregationSql.Command(connection, tx, "SELECT url, row_version FROM tt.agg_feeds WHERE id = @id FOR UPDATE")
                .WithUuid("id", feedId))
            await using (var r = await lockFeed.ExecuteReaderAsync(ct))
            {
                await r.ReadAsync(ct);
                (currentUrl, rowVersion) = (r.GetString(0), r.GetInt64(1));
            }

            ETags.EnsureMatches(rowVersion, expected);
            RequireNotRetired(profile);
            var urlChanged = url is not null && !string.Equals(url, currentUrl, StringComparison.Ordinal);
            if (urlChanged) await EnsureUrlFreeAsync(connection, tx, url!, feedId, ct);
            if (mapping is not null) await EnsureCategoriesAsync(connection, tx, mapping, profile.SiteKeys, ct);

            // Đổi URL: feed mới hoàn toàn với bộ thu thập (xóa cache HTTP, sức khỏe, lease) và thu thập lại ngay khi được duyệt.
            await using (var update = AggregationSql.Command(connection, tx, """
                UPDATE tt.agg_feeds
                   SET url = COALESCE(@url, url),
                       format = COALESCE(@format, format),
                       is_enabled = COALESCE(@enabled, is_enabled),
                       schedule_minutes = COALESCE(@schedule, schedule_minutes),
                       category_mapping = COALESCE(@mapping, category_mapping),
                       etag = CASE WHEN @url_changed THEN NULL ELSE etag END,
                       last_modified = CASE WHEN @url_changed THEN NULL ELSE last_modified END,
                       health = CASE WHEN @url_changed THEN 'NEVER_FETCHED' ELSE health END,
                       consecutive_failures = CASE WHEN @url_changed THEN 0 ELSE consecutive_failures END,
                       last_failure = CASE WHEN @url_changed THEN NULL ELSE last_failure END,
                       failing_since = CASE WHEN @url_changed THEN NULL ELSE failing_since END,
                       lease_until = CASE WHEN @url_changed THEN NULL ELSE lease_until END,
                       next_fetch_at = CASE WHEN NOT COALESCE(@enabled, is_enabled) THEN NULL
                                            WHEN @url_changed OR next_fetch_at IS NULL THEN now()
                                            ELSE next_fetch_at END
                 WHERE id = @id AND row_version = @expected
                """)
                .WithText("url", urlChanged ? url : null).WithText("format", patch.Format).WithBool("enabled", patch.IsEnabled)
                .WithInt("schedule", patch.ScheduleMinutes).WithJsonb("mapping", mapping is null ? null : AggregationSql.MappingJson(mapping))
                .WithBool("url_changed", urlChanged).WithUuid("id", feedId).WithLong("expected", expected))
            {
                if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            }

            if (urlChanged) await SendBackForApprovalAsync(connection, tx, sourceId, ct);
            return (await LoadFeedAsync(connection, tx, feedId, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- nội bộ

    /// <summary>feedCategory không trùng (không phân biệt hoa thường) — một category của feed chỉ ánh xạ một chuyên mục.</summary>
    private static List<AggregationCategoryMapping> CheckMapping(IReadOnlyList<AggregationCategoryMapping> mapping)
    {
        if (mapping.Count > AggregationLimits.MaxCategoryMappings) throw Invalid("categoryMapping", $"At most {AggregationLimits.MaxCategoryMappings} mappings.");
        var normalized = mapping.Select(x => new AggregationCategoryMapping(x.FeedCategory.Trim(), x.CategoryId)).ToList();
        var duplicate = normalized.GroupBy(x => x.FeedCategory, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw Invalid("categoryMapping", $"feedCategory '{duplicate.Key}' is mapped more than once.");
        if (normalized.Any(x => x.FeedCategory.Length == 0)) throw Invalid("categoryMapping", "feedCategory must not be blank.");
        return normalized;
    }

    /// <summary>Một URL chỉ thuộc một feed (tránh thu thập trùng) → 409. Ràng buộc UNIQUE ở DB chặn lần nữa.</summary>
    private static async Task EnsureUrlFreeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string url, Guid? exceptFeedId, CancellationToken ct)
    {
        await using var command = AggregationSql.Command(connection, tx, "SELECT content_source_id FROM tt.agg_feeds WHERE url = @url AND id IS DISTINCT FROM @except")
            .WithText("url", url).WithUuid("except", exceptFeedId);
        if (await command.ExecuteScalarAsync(ct) is Guid owner)
        {
            throw Conflict($"This feed URL is already registered (source {owner}).");
        }
    }

    /// <summary>Chuyên mục đích phải tồn tại, đang bật và thuộc một site của nguồn (chuyên mục theo site — ADR-0001).</summary>
    private static async Task EnsureCategoriesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<AggregationCategoryMapping> mapping,
        IReadOnlyList<string> siteKeys, CancellationToken ct)
    {
        var ids = mapping.Select(x => x.CategoryId!.Value).Distinct().ToList();
        if (ids.Count == 0) return;
        var valid = new HashSet<Guid>();
        await using (var command = AggregationSql.Command(connection, tx,
            "SELECT id FROM tt.categories WHERE id = ANY(@ids) AND is_enabled AND site_key = ANY(@sites)")
            .WithUuidArray("ids", ids).WithTextArray("sites", siteKeys))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) valid.Add(r.GetGuid(0));
        }

        if (ids.FirstOrDefault(id => !valid.Contains(id)) is var bad && bad != Guid.Empty)
        {
            throw Invalid("categoryMapping", $"Category {bad} does not exist, is disabled or does not belong to a site of this source.");
        }
    }

    /// <summary>BR-01: hồ sơ quay lại chờ duyệt. Không đổi dòng nếu đã ở trạng thái đó (tránh tăng row_version vô ích).</summary>
    private static async Task SendBackForApprovalAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid sourceId, CancellationToken ct)
    {
        await using var command = AggregationSql.Command(connection, tx, """
            UPDATE tt.agg_source_profiles
               SET approved_by = NULL, approved_at = NULL,
                   status = CASE WHEN status IN ('ENABLED', 'DEGRADED') THEN 'PROPOSED' ELSE status END
             WHERE content_source_id = @id AND (approved_at IS NOT NULL OR status IN ('ENABLED', 'DEGRADED'))
            """).WithUuid("id", sourceId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<AggregationFeed?> LoadFeedAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid feedId, CancellationToken ct)
    {
        await using var command = AggregationSql.Command(connection, tx, AggregationSql.FeedSelect + " WHERE f.id = @id").WithUuid("id", feedId);
        await using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? AggregationSql.ReadFeed(r) : null;
    }

    private static DomainProblemException FeedNotFound() => new(ErrorCodes.NotFound, "Not found", "No aggregation feed with this id.");
}

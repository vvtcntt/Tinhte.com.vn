using System.Data.Common;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Tinhte.Modules.Aggregation.Application;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Flags;

namespace Tinhte.Modules.Aggregation.Infrastructure;

/// <summary>SQL + ánh xạ dòng → DTO của module Aggregation (Npgsql thuần, ADR-0003). Chỉ đọc bảng module khác (content_sources, categories).</summary>
internal static class AggregationSql
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Hồ sơ nguồn + số feed, số mục tin 24 giờ, sức khỏe feed tệ nhất (feed đang bật; tất cả tắt → DISABLED; không có feed → null).
    /// Thứ tự cột khớp <see cref="ReadSource"/>.
    /// </summary>
    public const string SourceSelect = """
        SELECT p.content_source_id, cs.name, p.status, p.usage_mode, p.license_id, p.trust_weight, p.language, p.site_keys,
               (SELECT count(*)::int FROM tt.agg_feeds f WHERE f.content_source_id = p.content_source_id),
               (SELECT count(*)::int FROM tt.agg_items i WHERE i.content_source_id = p.content_source_id AND i.first_seen_at >= now() - interval '24 hours'),
               (SELECT CASE WHEN count(*) = 0 THEN NULL
                            WHEN bool_or(f.is_enabled) THEN (array_agg(f.health ORDER BY CASE f.health WHEN 'FAILING' THEN 5 WHEN 'BLOCKED_BY_ROBOTS' THEN 4
                                 WHEN 'DEGRADED' THEN 3 WHEN 'NEVER_FETCHED' THEN 2 ELSE 1 END DESC) FILTER (WHERE f.is_enabled))[1]
                            ELSE 'DISABLED' END
                  FROM tt.agg_feeds f WHERE f.content_source_id = p.content_source_id),
               p.row_version, p.terms_evidence_ref, p.proposed_by, p.approved_by, p.approved_at,
               h.created_at, COALESCE(h.actor_id IS NULL, false), h.reason
          FROM tt.agg_source_profiles p
          JOIN tt.content_sources cs ON cs.id = p.content_source_id
          LEFT JOIN LATERAL (SELECT sh.created_at, sh.actor_id, sh.reason FROM tt.agg_source_status_history sh
                              WHERE sh.content_source_id = p.content_source_id ORDER BY sh.id DESC LIMIT 1) h ON true
        """;

    /// <summary>Thứ tự cột khớp <see cref="ReadFeed"/>.</summary>
    public const string FeedSelect = """
        SELECT f.id, f.content_source_id, f.url, f.format, f.is_enabled, f.schedule_minutes, f.category_mapping::text, f.health,
               f.last_fetched_at, f.last_success_at, f.next_fetch_at, f.consecutive_failures, f.last_failure, f.row_version
          FROM tt.agg_feeds f
        """;

    public static (AggregationSourceSummary Summary, SourceApprovalState Approval) ReadSource(DbDataReader r) => (
        new AggregationSourceSummary(
            Id: r.GetGuid(0),
            Name: r.GetString(1),
            Status: r.GetString(2),
            UsageMode: r.GetString(3),
            LicenseId: NullableGuid(r, 4),
            TrustWeight: r.GetInt16(5),
            Language: r.GetString(6),
            SiteKeys: r.GetFieldValue<string[]>(7),
            FeedCount: r.GetInt32(8),
            ItemsLast24h: r.GetInt32(9),
            WorstFeedHealth: NullableText(r, 10),
            RowVersion: r.GetInt64(11),
            StatusChangedAt: NullableTime(r, 16),
            StatusChangedBySystem: r.GetBoolean(17),
            StatusReason: NullableText(r, 18)),
        new SourceApprovalState(NullableText(r, 12), r.GetGuid(13), NullableGuid(r, 14), NullableTime(r, 15)));

    /// <summary>
    /// Thời điểm hiệu lực của mục (ADR-0008 §6, BR-11/12): published_at nếu có và không ở tương lai quá 10 phút so với lúc thấy, ngược lại first_seen_at.
    /// </summary>
    public const string ItemEffectiveAt =
        "CASE WHEN i.published_at IS NOT NULL AND i.published_at <= i.first_seen_at + interval '10 minutes' THEN i.published_at ELSE i.first_seen_at END";

    /// <summary>AggregationItem (bí danh i, cs). Thứ tự cột khớp <see cref="ReadItem"/>.</summary>
    public const string ItemSelect = """
        SELECT i.id, i.content_source_id, cs.name, i.feed_id, i.title, i.url, i.summary, i.author, i.published_at, i.first_seen_at, i.time_reliable,
               i.language, i.feed_categories, i.status, i.category_id, i.category_confidence, i.needs_review, i.sensitive, i.version, i.cluster_id
          FROM tt.agg_items i
          JOIN tt.content_sources cs ON cs.id = i.content_source_id
        """;

    public static AggregationItem ReadItem(DbDataReader r) => new(
        Id: r.GetGuid(0),
        SourceId: r.GetGuid(1),
        SourceName: r.GetString(2),
        FeedId: r.GetGuid(3),
        Title: r.GetString(4),
        Url: r.GetString(5),
        Summary: NullableText(r, 6),
        Author: NullableText(r, 7),
        PublishedAt: NullableTime(r, 8),
        FirstSeenAt: r.GetFieldValue<DateTimeOffset>(9),
        TimeReliable: r.GetBoolean(10),
        Language: NullableText(r, 11),
        FeedCategories: r.GetFieldValue<string[]>(12),
        Status: r.GetString(13),
        CategoryId: NullableGuid(r, 14),
        Confidence: r.IsDBNull(15) ? null : r.GetDecimal(15),
        NeedsReview: r.GetBoolean(16),
        Sensitive: r.GetBoolean(17),
        Version: r.GetInt32(18),
        ClusterId: NullableGuid(r, 19));

    public static AggregationFeed ReadFeed(DbDataReader r)
    {
        var enabled = r.GetBoolean(4);
        return new AggregationFeed(
            Id: r.GetGuid(0),
            SourceId: r.GetGuid(1),
            Url: r.GetString(2),
            Format: r.GetString(3),
            IsEnabled: enabled,
            ScheduleMinutes: r.GetInt32(5),
            CategoryMapping: JsonSerializer.Deserialize<List<AggregationCategoryMapping>>(r.GetString(6), Json) ?? [],
            Health: enabled ? r.GetString(7) : FeedHealth.Disabled,
            LastFetchedAt: NullableTime(r, 8),
            LastSuccessAt: NullableTime(r, 9),
            NextFetchAt: NullableTime(r, 10),
            ConsecutiveFailures: r.GetInt32(11),
            LastFailure: NullableText(r, 12),
            RowVersion: r.GetInt64(13));
    }

    public static string MappingJson(IReadOnlyList<AggregationCategoryMapping> mapping) => JsonSerializer.Serialize(mapping, Json);

    /// <summary>
    /// Cờ <c>aggregation.radar</c> (ADR-0008 §8) đọc từ registry tt.feature_gates (0300/0341) — nguồn sự thật của production.
    /// Trong transaction ghi: <c>tt.flag_enabled_for_write</c> (FOR SHARE, giữ tới COMMIT) để tắt cờ không lọt giữa chừng.
    /// </summary>
    public static async Task RequireRadarEnabledAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var sql = tx is null
            ? "SELECT COALESCE((SELECT enabled FROM tt.feature_gates WHERE key = @key), false)"
            : "SELECT tt.flag_enabled_for_write(@key)";
        await using var command = Command(connection, tx, sql).WithText("key", FeatureFlagKeys.AggregationRadar);
        if (await command.ExecuteScalarAsync(ct) is not true)
        {
            throw new DomainProblemException(ErrorCodes.FeatureDisabled, "Feature disabled",
                $"{FeatureFlagKeys.AggregationRadar} is OFF: the news radar is not enabled in this environment.");
        }
    }

    /// <summary>Lý do của thao tác đổi trạng thái → trigger 0341 ghi vào tt.agg_source_status_history.</summary>
    public static async Task SetReasonAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string reason, CancellationToken ct)
    {
        await using var command = Command(connection, tx, "SELECT set_config('tt.reason', @reason, true)").WithText("reason", reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static string? NullableText(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static Guid? NullableGuid(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    public static DateTimeOffset? NullableTime(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    /// <summary>Mẫu ILIKE an toàn: thoát \, %, _ của người dùng.</summary>
    public static string LikePattern(string q) => "%" + q.Trim().Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";

    // ---------------------------------------------------------------- tham số

    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql) => new(sql, connection, tx);

    public static NpgsqlCommand WithUuid(this NpgsqlCommand command, string name, Guid? value) =>
        command.With(name, NpgsqlDbType.Uuid, value);

    public static NpgsqlCommand WithText(this NpgsqlCommand command, string name, string? value) =>
        command.With(name, NpgsqlDbType.Text, value);

    public static NpgsqlCommand WithInt(this NpgsqlCommand command, string name, int? value) =>
        command.With(name, NpgsqlDbType.Integer, value);

    public static NpgsqlCommand WithLong(this NpgsqlCommand command, string name, long? value) =>
        command.With(name, NpgsqlDbType.Bigint, value);

    public static NpgsqlCommand WithBool(this NpgsqlCommand command, string name, bool? value) =>
        command.With(name, NpgsqlDbType.Boolean, value);

    public static NpgsqlCommand WithTextArray(this NpgsqlCommand command, string name, IReadOnlyList<string>? value) =>
        command.With(name, NpgsqlDbType.Array | NpgsqlDbType.Text, value?.ToArray());

    public static NpgsqlCommand WithUuidArray(this NpgsqlCommand command, string name, IReadOnlyList<Guid> value) =>
        command.With(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid, value.ToArray());

    public static NpgsqlCommand WithJsonb(this NpgsqlCommand command, string name, string? value) =>
        command.With(name, NpgsqlDbType.Jsonb, value);

    public static NpgsqlCommand WithTime(this NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.With(name, NpgsqlDbType.TimestampTz, value?.ToUniversalTime());

    public static NpgsqlCommand WithSmallInt(this NpgsqlCommand command, string name, int? value) =>
        command.With(name, NpgsqlDbType.Smallint, value is { } v ? (short)Math.Clamp(v, short.MinValue, short.MaxValue) : null);

    public static NpgsqlCommand WithIntArray(this NpgsqlCommand command, string name, IReadOnlyList<int>? value) =>
        command.With(name, NpgsqlDbType.Array | NpgsqlDbType.Integer, value?.ToArray());

    public static NpgsqlCommand WithLongArray(this NpgsqlCommand command, string name, IReadOnlyList<long>? value) =>
        command.With(name, NpgsqlDbType.Array | NpgsqlDbType.Bigint, value?.ToArray());

    public static NpgsqlCommand WithDecimal(this NpgsqlCommand command, string name, decimal? value) =>
        command.With(name, NpgsqlDbType.Numeric, value);

    private static NpgsqlCommand With(this NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
        return command;
    }
}

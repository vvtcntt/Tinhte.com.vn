using System.Text;
using Npgsql;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>
/// Quản lý nguồn thu thập (FR-AGG-001, ADR-0008 §2): hồ sơ thu thập 1–1 với tt.content_sources, duyệt chế độ khai thác/giấy phép,
/// tạm dừng/tiếp tục/ngừng. Feed: AggregationSourceService.Feeds.cs. Mọi thao tác kiểm cờ aggregation.radar phía server;
/// thao tác ghi kiểm trong chính transaction (ADR-0008 §8). Ràng buộc nghiệp vụ được DB (0341) chặn lại lần nữa.
/// </summary>
public sealed partial class AggregationSourceService(ITinhteDatabase database, FeedUrlPolicy urlPolicy)
{
    // ---------------------------------------------------------------- đọc

    public async Task<IReadOnlyList<AggregationSourceSummary>> ListAsync(AggregationSourceFilter filter, AggregationSourcePosition? after, int limitPlusOne,
        CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);

        var sql = new StringBuilder(AggregationSql.SourceSelect).Append(" WHERE TRUE");
        await using var command = AggregationSql.Command(connection, null, "");
        if (filter.Statuses.Count > 0)
        {
            sql.Append(" AND p.status = ANY(@statuses)");
            command.WithTextArray("statuses", filter.Statuses);
        }

        if (filter.Q is { } q)
        {
            sql.Append(@" AND cs.name ILIKE @q ESCAPE '\'");
            command.WithText("q", AggregationSql.LikePattern(q));
        }

        // Keyset theo (tên, id): ổn định khi có nguồn mới chèn vào (ADR-0003 §4).
        if (after is not null)
        {
            sql.Append(" AND (cs.name, p.content_source_id) > (@after_name, @after_id)");
            command.WithText("after_name", after.Name).WithUuid("after_id", after.Id);
        }

        sql.Append(" ORDER BY cs.name, p.content_source_id LIMIT @limit");
        command.WithInt("limit", limitPlusOne);
        command.CommandText = sql.ToString();

        var rows = new List<AggregationSourceSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(AggregationSql.ReadSource(reader).Summary);
        return rows;
    }

    public async Task<AggregationSource> GetAsync(Guid sourceId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        return await LoadAsync(connection, null, sourceId, ct) ?? throw NotFound();
    }

    /// <summary>Kiểm cờ + tồn tại (404 đánh giá trước 428/412 — controller gọi trước RequireIfMatch).</summary>
    public async Task EnsureExistsAsync(Guid sourceId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        await using var command = AggregationSql.Command(connection, null, "SELECT 1 FROM tt.agg_source_profiles WHERE content_source_id = @id")
            .WithUuid("id", sourceId);
        if (await command.ExecuteScalarAsync(ct) is null) throw NotFound();
    }

    // ---------------------------------------------------------------- tạo (createAdminAggregationSource)

    public async Task<AggregationSource> CreateAsync(Guid actorId, AggregationSourceCreate request, CancellationToken ct)
    {
        if (AggregationLimits.SiteKeysError(request.SiteKeys) is { } siteError) throw Invalid("siteKeys", siteError);
        var sourceId = request.ContentSourceId!.Value;

        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await AggregationSql.RequireRadarEnabledAsync(connection, tx, ct);

            // Danh mục nguồn thuộc News/taxonomy (ADR-0007): chỉ đọc, khóa chia sẻ để nguồn không bị tắt giữa chừng.
            await using (var source = AggregationSql.Command(connection, tx, "SELECT is_enabled FROM tt.content_sources WHERE id = @id FOR SHARE")
                .WithUuid("id", sourceId))
            {
                switch (await source.ExecuteScalarAsync(ct))
                {
                    case null: throw Invalid("contentSourceId", "No content source with this id in the source catalogue.");
                    case false: throw Invalid("contentSourceId", "The content source is disabled in the source catalogue.");
                }
            }

            await using (var insert = AggregationSql.Command(connection, tx, """
                INSERT INTO tt.agg_source_profiles (content_source_id, trust_weight, language, site_keys, proposed_by)
                VALUES (@id, @trust, @language, @sites::varchar(31)[], @actor)
                ON CONFLICT (content_source_id) DO NOTHING
                """)
                .WithUuid("id", sourceId)
                .WithInt("trust", request.TrustWeight ?? AggregationLimits.DefaultTrustWeight)
                .WithText("language", request.Language)
                .WithTextArray("sites", request.SiteKeys)
                .WithUuid("actor", actorId))
            {
                if (await insert.ExecuteNonQueryAsync(ct) == 0)
                {
                    throw new DomainProblemException(ErrorCodes.StateConflict, "Profile exists",
                        "This content source already has an aggregation profile. Edit it instead.");
                }
            }

            return (await LoadAsync(connection, tx, sourceId, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- sửa (patchAdminAggregationSource)

    public Task<AggregationSource> PatchAsync(Guid actorId, Guid sourceId, long expected, AggregationSourcePatch patch, CancellationToken ct) =>
        database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await AggregationSql.RequireRadarEnabledAsync(connection, tx, ct);
            var profile = await LockAsync(connection, tx, sourceId, ct) ?? throw NotFound();
            ETags.EnsureMatches(profile.RowVersion, expected);
            RequireNotRetired(profile);

            await using var update = AggregationSql.Command(connection, tx, """
                UPDATE tt.agg_source_profiles
                   SET trust_weight = COALESCE(@trust, trust_weight), language = COALESCE(@language, language),
                       site_keys = COALESCE(@sites::varchar(31)[], site_keys)
                 WHERE content_source_id = @id AND row_version = @expected
                """)
                .WithInt("trust", patch.TrustWeight)
                .WithText("language", patch.Language)
                .WithTextArray("sites", patch.SiteKeys)
                .WithUuid("id", sourceId)
                .WithLong("expected", expected);
            if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            return (await LoadAsync(connection, tx, sourceId, ct))!;
        }, ct);

    // ---------------------------------------------------------------- duyệt (approveAdminAggregationSource)

    /// <summary>
    /// Ghi bản ghi duyệt (chỉ thêm) rồi bật nguồn. Thứ tự lỗi: 404 → 403 SoD (cần dữ liệu hồ sơ) → 412 → 409 RETIRED → 422 giấy phép.
    /// SoD chỉ áp cho M2/M3 (ADR-0008 §8): người đề xuất hồ sơ không tự duyệt.
    /// </summary>
    public Task<AggregationSource> ApproveAsync(Guid actorId, Guid sourceId, long expected, AggregationSourceApprovalRequest request, CancellationToken ct)
    {
        var mode = request.UsageMode!;
        if (UsageModes.NeedsLicence(mode) && request.LicenseId is null) throw Invalid("licenseId", $"Usage mode {mode} requires a licence.");
        var terms = request.TermsEvidenceRef!.Trim();
        if (terms.Length == 0) throw Invalid("termsEvidenceRef", "Required.");

        return database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await AggregationSql.RequireRadarEnabledAsync(connection, tx, ct);
            var profile = await LockAsync(connection, tx, sourceId, ct) ?? throw NotFound();
            if (UsageModes.NeedsLicence(mode) && profile.ProposedBy == actorId)
            {
                throw new DomainProblemException(ErrorCodes.SodViolation, "Separation of duties",
                    "The person who proposed this source cannot approve usage mode M2/M3 for it.");
            }

            ETags.EnsureMatches(profile.RowVersion, expected);
            RequireNotRetired(profile);

            if (request.LicenseId is { } licenseId)
            {
                await using var licence = AggregationSql.Command(connection, tx, "SELECT tt.license_active(@id) FROM tt.licenses WHERE id = @id FOR SHARE")
                    .WithUuid("id", licenseId);
                var active = await licence.ExecuteScalarAsync(ct);
                if (active is null) throw Invalid("licenseId", "No licence with this id.");
                if (UsageModes.NeedsLicence(mode) && active is not true)
                {
                    throw Invalid("licenseId", "The licence is disabled, not yet valid or expired. M2/M3 need an active licence.");
                }
            }

            await using (var approval = AggregationSql.Command(connection, tx, """
                INSERT INTO tt.agg_source_approvals (content_source_id, usage_mode, license_id, terms_evidence_ref, note, approved_by)
                VALUES (@id, @mode, @licence, @terms, @note, @actor)
                """)
                .WithUuid("id", sourceId).WithText("mode", mode).WithUuid("licence", request.LicenseId).WithText("terms", terms)
                .WithText("note", string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()).WithUuid("actor", actorId))
            {
                await approval.ExecuteNonQueryAsync(ct);
            }

            await using var update = AggregationSql.Command(connection, tx, """
                UPDATE tt.agg_source_profiles
                   SET usage_mode = @mode, license_id = @licence, terms_evidence_ref = @terms, approved_by = @actor, approved_at = now(),
                       status = 'ENABLED'
                 WHERE content_source_id = @id AND row_version = @expected
                """)
                .WithText("mode", mode).WithUuid("licence", request.LicenseId).WithText("terms", terms).WithUuid("actor", actorId)
                .WithUuid("id", sourceId).WithLong("expected", expected);
            if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            return (await LoadAsync(connection, tx, sourceId, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- trạng thái (pause/resume/retire)

    public Task<AggregationSource> PauseAsync(Guid actorId, Guid sourceId, long expected, string? reason, CancellationToken ct) =>
        ChangeStatusAsync(actorId, sourceId, expected, ct, profile =>
        {
            if (!SourceStatuses.Pausable.Contains(profile.Status)) throw Conflict($"A {profile.Status} source cannot be paused.");
            return (SourceStatuses.Paused, AggregationLimits.CheckReason(reason));
        });

    /// <summary>Chỉ nguồn PAUSED đã được duyệt (approved_at còn hiệu lực) mới tiếp tục; chưa từng duyệt / bị gửi lại duyệt → 409.</summary>
    public Task<AggregationSource> ResumeAsync(Guid actorId, Guid sourceId, long expected, CancellationToken ct) =>
        ChangeStatusAsync(actorId, sourceId, expected, ct, profile =>
        {
            if (profile.Status != SourceStatuses.Paused) throw Conflict($"Only a PAUSED source can resume; this one is {profile.Status}.");
            if (profile.ApprovedAt is null) throw Conflict("The source has no current approval: approve its usage mode before resuming.");
            return (SourceStatuses.Enabled, null);
        });

    /// <summary>Ngừng vĩnh viễn (không xóa, BR-07): mục tin đã thu thập giữ lại để truy vết; RETIRED không quay lại được.</summary>
    public Task<AggregationSource> RetireAsync(Guid actorId, Guid sourceId, long expected, string? reason, CancellationToken ct) =>
        ChangeStatusAsync(actorId, sourceId, expected, ct, profile =>
        {
            RequireNotRetired(profile);
            return (SourceStatuses.Retired, AggregationLimits.CheckReason(reason));
        });

    private Task<AggregationSource> ChangeStatusAsync(Guid actorId, Guid sourceId, long expected, CancellationToken ct,
        Func<LockedProfile, (string Status, string? Reason)> decide) =>
        database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await AggregationSql.RequireRadarEnabledAsync(connection, tx, ct);
            var profile = await LockAsync(connection, tx, sourceId, ct) ?? throw NotFound();
            ETags.EnsureMatches(profile.RowVersion, expected);
            var (status, reason) = decide(profile);
            if (reason is not null) await AggregationSql.SetReasonAsync(connection, tx, reason, ct);

            await using var update = AggregationSql.Command(connection, tx,
                "UPDATE tt.agg_source_profiles SET status = @status WHERE content_source_id = @id AND row_version = @expected")
                .WithText("status", status).WithUuid("id", sourceId).WithLong("expected", expected);
            if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            return (await LoadAsync(connection, tx, sourceId, ct))!;
        }, ct);

    // ---------------------------------------------------------------- nội bộ

    private sealed record LockedProfile(string Status, long RowVersion, Guid ProposedBy, DateTimeOffset? ApprovedAt, IReadOnlyList<string> SiteKeys);

    private static async Task<LockedProfile?> LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid sourceId, CancellationToken ct)
    {
        await using var command = AggregationSql.Command(connection, tx, """
            SELECT status, row_version, proposed_by, approved_at, site_keys FROM tt.agg_source_profiles WHERE content_source_id = @id FOR UPDATE
            """).WithUuid("id", sourceId);
        await using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new LockedProfile(r.GetString(0), r.GetInt64(1), r.GetGuid(2), AggregationSql.NullableTime(r, 3), r.GetFieldValue<string[]>(4))
            : null;
    }

    private static async Task<AggregationSource?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid sourceId, CancellationToken ct)
    {
        (AggregationSourceSummary Summary, SourceApprovalState Approval) row;
        await using (var command = AggregationSql.Command(connection, tx, AggregationSql.SourceSelect + " WHERE p.content_source_id = @id")
            .WithUuid("id", sourceId))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            row = AggregationSql.ReadSource(r);
        }

        var feeds = new List<AggregationFeed>();
        await using (var command = AggregationSql.Command(connection, tx, AggregationSql.FeedSelect + " WHERE f.content_source_id = @id ORDER BY f.created_at, f.id")
            .WithUuid("id", sourceId))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) feeds.Add(AggregationSql.ReadFeed(r));
        }

        var approvals = new List<AggregationSourceApprovalRecord>();
        await using (var command = AggregationSql.Command(connection, tx, """
            SELECT usage_mode, license_id, terms_evidence_ref, note, approved_by, approved_at
              FROM tt.agg_source_approvals WHERE content_source_id = @id ORDER BY approved_at DESC, id DESC
            """).WithUuid("id", sourceId))
        await using (var r = await command.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                approvals.Add(new AggregationSourceApprovalRecord(r.GetString(0), AggregationSql.NullableGuid(r, 1), r.GetString(2),
                    AggregationSql.NullableText(r, 3), r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        return AggregationSource.From(row.Summary, row.Approval, feeds, approvals);
    }

    private static void RequireNotRetired(LockedProfile profile)
    {
        if (profile.Status == SourceStatuses.Retired) throw Conflict("The source is RETIRED and can no longer change.");
    }

    private static DomainProblemException NotFound() => new(ErrorCodes.NotFound, "Not found", "No aggregation source with this id.");

    private static DomainProblemException Conflict(string detail) => new(ErrorCodes.StateConflict, "State conflict", detail);

    private static DomainProblemException Invalid(string field, string message) =>
        new(ErrorCodes.ValidationFailed, "Validation failed", message, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}

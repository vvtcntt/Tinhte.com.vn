using Npgsql;
using NpgsqlTypes;
using Tinhte.Modules.Ai.Application;

namespace Tinhte.Modules.Ai.Infrastructure;

/// <summary>SQL của module AI (Npgsql thuần, ADR-0003). Bảng tt.ai_requests: migration 0353.</summary>
internal static class AiSql
{
    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql) => new(sql, connection, transaction);

    /// <summary>Cờ đọc trong transaction ghi (FOR SHARE — hàm của 0307), cùng nguồn với trigger chặn INSERT.</summary>
    public static async Task<bool> FlagEnabledForWriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string flag, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT tt.flag_enabled_for_write(@flag)");
        command.Parameters.AddWithValue("flag", flag);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Cờ cho thao tác đọc (cùng cách đọc với GET /ads/placements).</summary>
    public static async Task<bool> FlagEnabledAsync(NpgsqlConnection connection, string flag, CancellationToken ct)
    {
        await using var command = Command(connection, null, "SELECT COALESCE((SELECT g.enabled FROM tt.feature_gates g WHERE g.key = @flag), false)");
        command.Parameters.AddWithValue("flag", flag);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Bài hoặc video theo id: revision working, nếu không có thì public (thân video: cột body của 0355). null = không có.</summary>
    public static async Task<AiContentSource?> LoadContentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid contentId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT 'ARTICLE', COALESCE(r.title, ''), r.summary, r.body::text
              FROM tt.articles a
              LEFT JOIN tt.article_revisions r ON r.id = COALESCE(a.working_revision_id, a.public_revision_id)
             WHERE a.id = @id
            UNION ALL
            SELECT 'VIDEO', COALESCE(r.title, ''), r.summary, r.body::text
              FROM tt.videos v
              LEFT JOIN tt.video_revisions r ON r.id = COALESCE(v.working_revision_id, v.public_revision_id)
             WHERE v.id = @id
             LIMIT 1
            """);
        command.Parameters.AddWithValue("id", contentId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new AiContentSource(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>
    /// Số lần gọi của người trong 60 phút trượt + số giây tới khi lần cũ nhất ra khỏi cửa sổ. Khóa advisory theo người trong transaction để
    /// hai request song song không cùng lọt qua hạn mức.
    /// </summary>
    public static async Task<(int Count, double RetryAfterSeconds)> RecentUsageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid actorId, CancellationToken ct)
    {
        await using (var @lock = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended('tt.ai_requests:' || @actor::text, 0))"))
        {
            @lock.Parameters.AddWithValue("actor", actorId);
            await @lock.ExecuteNonQueryAsync(ct);
        }

        await using var command = Command(connection, transaction, """
            SELECT count(*)::int,
                   COALESCE(EXTRACT(EPOCH FROM (min(created_at) + interval '1 hour' - now()))::float8, 0)
              FROM tt.ai_requests
             WHERE actor_id = @actor AND created_at > now() - interval '1 hour'
            """);
        command.Parameters.AddWithValue("actor", actorId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetDouble(1));
    }

    public static async Task InsertPendingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid actorId, string action,
        string objectType, Guid objectId, string providerKey, string model, int inputChars, string inputSha256, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO tt.ai_requests (id, actor_id, action, object_type, object_id, provider_key, model, input_chars, input_sha256)
            VALUES (@id, @actor, @action, @type, @object, @provider, @model, @chars, @sha)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("type", objectType);
        command.Parameters.AddWithValue("object", objectId);
        command.Parameters.AddWithValue("provider", providerKey);
        command.Parameters.AddWithValue("model", model);
        command.Parameters.AddWithValue("chars", inputChars);
        command.Parameters.AddWithValue("sha", inputSha256);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task CompleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string? errorCode,
        int? promptTokens, int? completionTokens, int latencyMs, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            UPDATE tt.ai_requests
               SET status = CASE WHEN @error IS NULL THEN 'SUCCEEDED' ELSE 'FAILED' END,
                   error_code = @error, prompt_tokens = @prompt, completion_tokens = @completion, latency_ms = @latency, completed_at = now()
             WHERE id = @id AND status = 'PENDING'
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = (object?)errorCode ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("prompt", NpgsqlDbType.Integer) { Value = (object?)promptTokens ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("completion", NpgsqlDbType.Integer) { Value = (object?)completionTokens ?? DBNull.Value });
        command.Parameters.AddWithValue("latency", latencyMs);
        await command.ExecuteNonQueryAsync(ct);
    }

    public sealed record AuditPosition(DateTimeOffset At, Guid Id);

    public sealed record AuditRow(Guid Id, AiAuditItem Item);

    public static async Task<IReadOnlyList<AuditRow>> ListAuditAsync(NpgsqlConnection connection, Guid? actorId, DateTimeOffset? from, DateTimeOffset? to,
        AuditPosition? after, int take, CancellationToken ct)
    {
        await using var command = Command(connection, null, """
            SELECT id, created_at, actor_id, action, object_id
              FROM tt.ai_requests
             WHERE (@actor::uuid IS NULL OR actor_id = @actor)
               AND (@from::timestamptz IS NULL OR created_at >= @from)
               AND (@to::timestamptz IS NULL OR created_at < @to)
               AND (@after_at::timestamptz IS NULL OR (created_at, id) < (@after_at, @after_id))
             ORDER BY created_at DESC, id DESC
             LIMIT @take
            """);
        command.Parameters.Add(new NpgsqlParameter("actor", NpgsqlDbType.Uuid) { Value = (object?)actorId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = (object?)from?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = (object?)to?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("after_at", NpgsqlDbType.TimestampTz) { Value = (object?)after?.At.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Uuid) { Value = (object?)after?.Id ?? DBNull.Value });
        command.Parameters.AddWithValue("take", take);

        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            rows.Add(new AuditRow(id, new AiAuditItem(reader.GetFieldValue<DateTimeOffset>(1), reader.GetGuid(2), reader.GetString(3), reader.GetGuid(4).ToString())));
        }

        return rows;
    }
}

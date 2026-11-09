using Npgsql;
using NpgsqlTypes;

namespace Tinhte.Platform.Data;

/// <summary>Khóa idempotency: phạm vi actor + method + route (components.parameters.IdempotencyKey; migration 0308).</summary>
public sealed record IdempotencyScope(string ActorKey, string HttpMethod, string RouteTemplate, string Key);

public abstract record IdempotencyBegin
{
    /// <summary>Lần đầu (hoặc khóa đã hết hạn / bản ghi treo quá hạn khóa): được phép xử lý.</summary>
    public sealed record Started : IdempotencyBegin;

    /// <summary>Đã có response: phát lại nguyên status/body.</summary>
    public sealed record Replay(int StatusCode, string ResponseJson) : IdempotencyBegin;

    /// <summary>Cùng khóa nhưng payload khác → 409 IDEMPOTENCY_KEY_REUSED.</summary>
    public sealed record Mismatch : IdempotencyBegin;

    /// <summary>Request cùng khóa đang được xử lý.</summary>
    public sealed record InProgress : IdempotencyBegin;
}

public interface IIdempotencyStore
{
    Task<IdempotencyBegin> BeginAsync(IdempotencyScope scope, string requestHash, CancellationToken cancellationToken = default);

    Task CompleteAsync(IdempotencyScope scope, int statusCode, string responseJson, CancellationToken cancellationToken = default);

    /// <summary>Xóa bản ghi đang xử lý (lỗi 5xx/ngoại lệ) để client thử lại được.</summary>
    Task AbandonAsync(IdempotencyScope scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lưu ở <c>tt.idempotency_keys</c> (24h). INSERT trước khi xử lý với <c>status_code = NULL</c> (đánh dấu đang xử lý),
/// cập nhật response khi xong. Một câu lệnh INSERT … ON CONFLICT nên hai request song song không cùng được "Started".
/// Bản ghi treo (tiến trình chết giữa chừng) được nhận lại sau <see cref="InProgressTimeout"/>.
/// </summary>
public sealed class PostgresIdempotencyStore(ITinhteDatabase database) : IIdempotencyStore
{
    public static readonly TimeSpan InProgressTimeout = TimeSpan.FromSeconds(60);

    private const string BeginSql = """
        INSERT INTO tt.idempotency_keys (actor_key, http_method, route_template, idempotency_key, request_hash)
        VALUES (@actor, @method, @route, @key, @hash)
        ON CONFLICT (actor_key, http_method, route_template, idempotency_key) DO UPDATE
          SET request_hash = EXCLUDED.request_hash, status_code = NULL, response_body = NULL,
              created_at = now(), expires_at = now() + interval '24 hours'
          WHERE tt.idempotency_keys.expires_at < now()
             OR (tt.idempotency_keys.status_code IS NULL
                 AND tt.idempotency_keys.created_at < now() - @timeout
                 AND tt.idempotency_keys.request_hash = EXCLUDED.request_hash)
        RETURNING true
        """;

    private const string ReadSql = """
        SELECT request_hash, status_code, response_body::text FROM tt.idempotency_keys
        WHERE actor_key = @actor AND http_method = @method AND route_template = @route AND idempotency_key = @key
        """;

    public async Task<IdempotencyBegin> BeginAsync(IdempotencyScope scope, string requestHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using (var begin = Command(connection, BeginSql, scope))
        {
            begin.Parameters.AddWithValue("hash", requestHash);
            begin.Parameters.AddWithValue("timeout", InProgressTimeout);
            if (await begin.ExecuteScalarAsync(cancellationToken) is true) return new IdempotencyBegin.Started();
        }

        await using var read = Command(connection, ReadSql, scope);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new IdempotencyBegin.InProgress(); // vừa bị xóa bởi request khác
        if (reader.GetString(0) != requestHash) return new IdempotencyBegin.Mismatch();
        if (reader.IsDBNull(1)) return new IdempotencyBegin.InProgress();
        return new IdempotencyBegin.Replay(reader.GetInt16(1), reader.IsDBNull(2) ? "{}" : reader.GetString(2));
    }

    public async Task CompleteAsync(IdempotencyScope scope, int statusCode, string responseJson, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, """
            UPDATE tt.idempotency_keys SET status_code = @status, response_body = @body
            WHERE actor_key = @actor AND http_method = @method AND route_template = @route AND idempotency_key = @key
            """, scope);
        command.Parameters.AddWithValue("status", (short)statusCode);
        command.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Jsonb) { Value = responseJson });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AbandonAsync(IdempotencyScope scope, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, """
            DELETE FROM tt.idempotency_keys
            WHERE actor_key = @actor AND http_method = @method AND route_template = @route AND idempotency_key = @key AND status_code IS NULL
            """, scope);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, IdempotencyScope scope)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("actor", scope.ActorKey);
        command.Parameters.AddWithValue("method", scope.HttpMethod);
        command.Parameters.AddWithValue("route", scope.RouteTemplate);
        command.Parameters.AddWithValue("key", scope.Key);
        return command;
    }
}

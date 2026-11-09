using Npgsql;
using NpgsqlTypes;
using Tinhte.Platform.Data;

namespace Tinhte.Modules.Identity.Infrastructure;

/// <summary>Dòng tài khoản cho màn quản trị (schema AdminUser của hợp đồng v0.3).</summary>
public sealed record AdminUserRow(Guid Id, string DisplayName, string Status, IReadOnlyList<string> Roles, long RowVersion, DateTimeOffset CreatedAt);

public sealed record AdminUserFilter(string? Status, string? Role, string? Query);

/// <summary>Vị trí cursor của danh sách tài khoản: sắp theo (created_at DESC, id DESC).</summary>
public sealed record AdminUserPosition(DateTimeOffset CreatedAt, Guid Id);

/// <summary>Đọc/ghi tài khoản cho WBS-115 trên tt.users + bảng của migration 0313/0314.</summary>
public sealed class AdminUserStore(ITinhteDatabase database)
{
    private const string Select = """
        SELECT u.id, u.display_name, u.status, u.row_version, u.created_at,
               COALESCE(array_agg(r.role ORDER BY r.role) FILTER (WHERE r.role IS NOT NULL), '{}')
        FROM tt.users u
        LEFT JOIN tt.user_roles r ON r.user_id = u.id
        """;

    public async Task<IReadOnlyList<AdminUserRow>> ListAsync(AdminUserFilter filter, AdminUserPosition? after, int take, CancellationToken cancellationToken)
    {
        await using var c = await database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Select + """

            WHERE (@status IS NULL OR u.status = @status)
              AND (@role IS NULL OR EXISTS (SELECT 1 FROM tt.user_roles x WHERE x.user_id = u.id AND x.role = @role))
              AND (@q IS NULL OR u.display_name ILIKE @like ESCAPE '\' OR u.id::text = @q)
              AND (@afterAt IS NULL OR (u.created_at, u.id) < (@afterAt, @afterId))
            GROUP BY u.id
            ORDER BY u.created_at DESC, u.id DESC
            LIMIT @take
            """, c);
        command.Parameters.Add(Text("status", filter.Status));
        command.Parameters.Add(Text("role", filter.Role));
        command.Parameters.Add(Text("q", filter.Query));
        command.Parameters.Add(Text("like", filter.Query is null ? null : "%" + EscapeLike(filter.Query) + "%"));
        command.Parameters.Add(new NpgsqlParameter("afterAt", NpgsqlDbType.TimestampTz) { Value = (object?)after?.CreatedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("afterId", NpgsqlDbType.Uuid) { Value = (object?)after?.Id ?? DBNull.Value });
        command.Parameters.AddWithValue("take", take);
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<AdminUserRow?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var c = await database.OpenConnectionAsync(cancellationToken);
        return await GetAsync(c, null, id, cancellationToken);
    }

    public static async Task<AdminUserRow?> GetAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Select + "\nWHERE u.id = @id GROUP BY u.id", c, tx);
        command.Parameters.AddWithValue("id", id);
        var rows = await ReadAllAsync(command, cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Khóa dòng user trong transaction ghi (đổi trạng thái/vai trò tuần tự, If-Match chính xác).</summary>
    public static async Task<(string Status, long RowVersion)?> LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT status, row_version FROM tt.users WHERE id = @id FOR UPDATE", c, tx);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    /// <summary>Lý do cho trigger lịch sử (0313 vai trò, 0314 trạng thái) — chỉ trong transaction hiện tại.</summary>
    public static async Task SetReasonAsync(NpgsqlConnection c, NpgsqlTransaction tx, string reason, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('tt.reason', @reason, true)", c, tx);
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, c, tx);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    private static async Task<List<AdminUserRow>> ReadAllAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<AdminUserRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AdminUserRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<string[]>(5),
                reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }
}

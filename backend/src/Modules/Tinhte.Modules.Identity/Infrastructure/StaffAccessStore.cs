using Npgsql;
using Tinhte.Platform.Data;

namespace Tinhte.Modules.Identity.Infrastructure;

public sealed record StaffAccount(Guid UserId, string Status, IReadOnlyList<string> Roles);

public sealed record ActiveStaffSession(string IdHash, Guid UserId, string CsrfHash, bool MfaSatisfied, IReadOnlyList<string> Roles);

public sealed record NewStaffSession(string IdHash, Guid UserId, string Issuer, string CsrfHash, bool MfaSatisfied,
    IReadOnlyList<string> Amr, DateTimeOffset? AuthTime, DateTimeOffset ExpiresAt, string? UserAgent);

/// <summary>Danh tính OIDC, vai trò, phiên staff trên các bảng của migration 0313.</summary>
public sealed class StaffAccessStore(ITinhteDatabase database)
{
    /// <summary>Tìm user theo (issuer, subject); chưa có thì tạo user + liên kết (provision lần đầu, chưa có vai trò).</summary>
    public Task<StaffAccount> FindOrProvisionAsync(string issuer, string subject, string displayName, CancellationToken cancellationToken) =>
        database.InTransactionAsync(null, async (c, tx) =>
        {
            Guid userId;
            await using (var find = new NpgsqlCommand(
                "SELECT user_id FROM tt.user_identities WHERE issuer = @iss AND subject = @sub FOR UPDATE", c, tx))
            {
                find.Parameters.AddWithValue("iss", issuer);
                find.Parameters.AddWithValue("sub", subject);
                userId = await find.ExecuteScalarAsync(cancellationToken) is Guid existing ? existing : Guid.Empty;
            }

            if (userId == Guid.Empty)
            {
                await using var createUser = new NpgsqlCommand("INSERT INTO tt.users (display_name) VALUES (@name) RETURNING id", c, tx);
                createUser.Parameters.AddWithValue("name", displayName);
                userId = (Guid)(await createUser.ExecuteScalarAsync(cancellationToken))!;
                await using var link = new NpgsqlCommand(
                    "INSERT INTO tt.user_identities (issuer, subject, user_id) VALUES (@iss, @sub, @uid) ON CONFLICT DO NOTHING", c, tx);
                link.Parameters.AddWithValue("iss", issuer);
                link.Parameters.AddWithValue("sub", subject);
                link.Parameters.AddWithValue("uid", userId);
                await link.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var touch = new NpgsqlCommand(
                "UPDATE tt.user_identities SET last_login_at = now() WHERE issuer = @iss AND subject = @sub", c, tx))
            {
                touch.Parameters.AddWithValue("iss", issuer);
                touch.Parameters.AddWithValue("sub", subject);
                await touch.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var account = new NpgsqlCommand("""
                SELECT u.status, COALESCE(array_agg(r.role ORDER BY r.role) FILTER (WHERE r.role IS NOT NULL), '{}')
                FROM tt.users u LEFT JOIN tt.user_roles r ON r.user_id = u.id
                WHERE u.id = @uid GROUP BY u.status
                """, c, tx);
            account.Parameters.AddWithValue("uid", userId);
            await using var reader = await account.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new StaffAccount(userId, reader.GetString(0), reader.GetFieldValue<string[]>(1));
        }, cancellationToken);

    public async Task CreateSessionAsync(NewStaffSession session, string? rotatedFromHash, CancellationToken cancellationToken)
    {
        await database.InTransactionAsync(session.UserId, async (c, tx) =>
        {
            if (rotatedFromHash is not null)
            {
                await RevokeAsync(c, tx, rotatedFromHash, "ROTATED", cancellationToken);
            }

            await using var insert = new NpgsqlCommand("""
                INSERT INTO tt.staff_sessions (id_hash, user_id, issuer, csrf_hash, mfa_satisfied, amr, auth_time, expires_at, user_agent)
                VALUES (@id, @uid, @iss, @csrf, @mfa, @amr, @auth, @exp, @ua)
                """, c, tx);
            insert.Parameters.AddWithValue("id", session.IdHash);
            insert.Parameters.AddWithValue("uid", session.UserId);
            insert.Parameters.AddWithValue("iss", session.Issuer);
            insert.Parameters.AddWithValue("csrf", session.CsrfHash);
            insert.Parameters.AddWithValue("mfa", session.MfaSatisfied);
            insert.Parameters.AddWithValue("amr", session.Amr.ToArray());
            insert.Parameters.AddWithValue("auth", (object?)session.AuthTime ?? DBNull.Value);
            insert.Parameters.AddWithValue("exp", session.ExpiresAt);
            insert.Parameters.AddWithValue("ua", (object?)session.UserAgent ?? DBNull.Value);
            return await insert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Phiên còn hiệu lực: chưa thu hồi, chưa quá hạn tuyệt đối/idle, user ACTIVE. Vai trò đọc mới mỗi request.</summary>
    public async Task<ActiveStaffSession?> FindActiveAsync(string idHash, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        await using var c = await database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT s.user_id, s.csrf_hash, s.mfa_satisfied,
                   COALESCE(array_agg(r.role ORDER BY r.role) FILTER (WHERE r.role IS NOT NULL), '{}')
            FROM tt.staff_sessions s
            JOIN tt.users u ON u.id = s.user_id
            LEFT JOIN tt.user_roles r ON r.user_id = s.user_id
            WHERE s.id_hash = @id AND s.revoked_at IS NULL AND s.expires_at > now()
              AND s.last_seen_at > now() - @idle AND u.status = 'ACTIVE'
            GROUP BY s.id_hash
            """, c);
        command.Parameters.AddWithValue("id", idHash);
        command.Parameters.AddWithValue("idle", idleTimeout);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var session = new ActiveStaffSession(idHash, reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetFieldValue<string[]>(3));
        await reader.CloseAsync();

        // Gia hạn idle tối đa mỗi phút một lần để không ghi DB ở mọi request.
        await using var touch = new NpgsqlCommand(
            "UPDATE tt.staff_sessions SET last_seen_at = now() WHERE id_hash = @id AND last_seen_at < now() - interval '60 seconds'", c);
        touch.Parameters.AddWithValue("id", idHash);
        await touch.ExecuteNonQueryAsync(cancellationToken);
        return session;
    }

    public Task RevokeAsync(string idHash, string reason, Guid? actorId, CancellationToken cancellationToken) =>
        database.InTransactionAsync(actorId, (c, tx) => RevokeAsync(c, tx, idHash, reason, cancellationToken), cancellationToken);

    /// <summary>Thu hồi mọi phiên của user (đình chỉ, đổi vai trò — FR-ID-004).</summary>
    public Task<int> RevokeAllForUserAsync(Guid userId, string reason, Guid? actorId, CancellationToken cancellationToken) =>
        database.InTransactionAsync(actorId, async (c, tx) =>
        {
            await using var command = new NpgsqlCommand(
                "UPDATE tt.staff_sessions SET revoked_at = now(), revoked_reason = @reason WHERE user_id = @uid AND revoked_at IS NULL", c, tx);
            command.Parameters.AddWithValue("uid", userId);
            command.Parameters.AddWithValue("reason", reason);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private static async Task<int> RevokeAsync(NpgsqlConnection c, NpgsqlTransaction tx, string idHash, string reason, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE tt.staff_sessions SET revoked_at = now(), revoked_reason = @reason WHERE id_hash = @id AND revoked_at IS NULL", c, tx);
        command.Parameters.AddWithValue("id", idHash);
        command.Parameters.AddWithValue("reason", reason);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

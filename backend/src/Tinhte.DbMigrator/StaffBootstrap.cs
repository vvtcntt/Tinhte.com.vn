using Npgsql;
using Tinhte.Platform.Data.Migrations;
using Tinhte.Platform.Security;

namespace Tinhte.DbMigrator;

/// <summary>
/// Cấp vai trò cho một danh tính OIDC từ dòng lệnh — dùng để tạo ADMIN đầu tiên (chưa ai có quyền dùng PUT /admin/users/{id}/roles).
/// Mọi lần cấp đều vào tt.user_role_history (actor NULL = hệ thống) kèm lý do. Sau đó cấp/thu hồi qua API admin (WBS-115).
/// </summary>
internal static class StaffBootstrap
{
    public static async Task<string> GrantRoleAsync(NpgsqlDataSource dataSource, string? issuer, string? subject, string? role, string? reason, string? name)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(reason))
        {
            throw new MigrationException("grant-role requires --issuer, --subject, --role and --reason.");
        }

        if (role is null || !StaffRoles.All.Contains(role))
        {
            throw new MigrationException($"--role must be one of: {string.Join(", ", StaffRoles.All)}.");
        }

        await using var c = await dataSource.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        Guid userId;
        await using (var find = new NpgsqlCommand("SELECT user_id FROM tt.user_identities WHERE issuer = @i AND subject = @s", c, tx))
        {
            find.Parameters.AddWithValue("i", issuer);
            find.Parameters.AddWithValue("s", subject);
            userId = await find.ExecuteScalarAsync() is Guid id ? id : Guid.Empty;
        }

        if (userId == Guid.Empty)
        {
            await using var user = new NpgsqlCommand("INSERT INTO tt.users (display_name) VALUES (@n) RETURNING id", c, tx);
            user.Parameters.AddWithValue("n", string.IsNullOrWhiteSpace(name) ? subject[..Math.Min(80, subject.Length)] : name);
            userId = (Guid)(await user.ExecuteScalarAsync())!;
            await using var link = new NpgsqlCommand("INSERT INTO tt.user_identities (issuer, subject, user_id) VALUES (@i, @s, @u)", c, tx);
            link.Parameters.AddWithValue("i", issuer);
            link.Parameters.AddWithValue("s", subject);
            link.Parameters.AddWithValue("u", userId);
            await link.ExecuteNonQueryAsync();
        }

        await using var grant = new NpgsqlCommand(
            "INSERT INTO tt.user_roles (user_id, role, reason) VALUES (@u, @r, @why) ON CONFLICT (user_id, role) DO NOTHING", c, tx);
        grant.Parameters.AddWithValue("u", userId);
        grant.Parameters.AddWithValue("r", role);
        grant.Parameters.AddWithValue("why", reason);
        var granted = await grant.ExecuteNonQueryAsync() == 1;
        await tx.CommitAsync();
        return granted ? $"granted {role} to user {userId}" : $"user {userId} already has {role}";
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Tinhte.Platform.Data.Migrations;

/// <summary>Một file migration <c>NNNN_ten.up.sql</c>. Checksum tính trên nội dung đã chuẩn hóa xuống dòng (LF) để không lệch theo autocrlf.</summary>
public sealed record MigrationScript(string Version, string Name, string Path, string Sql, string Checksum);

public enum MigrationState
{
    Pending,
    Applied,
    ChecksumMismatch,
    MissingOnDisk,
}

public sealed record MigrationStatus(string Version, string Name, MigrationState State, DateTimeOffset? AppliedAt);

public sealed class MigrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Chạy migration SQL theo thứ tự số (WBS-013). Nguyên tắc (db/README §2, CLAUDE.md §3):
/// <list type="bullet">
///   <item>Mỗi file chạy trong MỘT transaction; file không tự chứa BEGIN/COMMIT.</item>
///   <item>Nhật ký <c>tt_meta.schema_history</c> nằm ngoài <c>tt_migration</c> (0299 down xóa schema đó).</item>
///   <item>File đã chạy mà nội dung đổi (checksum lệch) → dừng, không tự chạy lại. Sửa bằng migration mới.</item>
///   <item><c>pg_advisory_xact_lock</c> + kiểm tra lại nhật ký trong cùng transaction → hai tiến trình migrate cùng lúc không chạy trùng.</item>
/// </list>
/// Rollback (.down.sql) KHÔNG tự động: chạy tay theo db/README §7 sau khi có phê duyệt.
/// </summary>
public sealed partial class MigrationRunner(NpgsqlDataSource dataSource)
{
    private const long LockKey = 0x7474_6D69_6772; // "ttmigr"

    private const string JournalDdl = """
        CREATE SCHEMA IF NOT EXISTS tt_meta;
        CREATE TABLE IF NOT EXISTS tt_meta.schema_history (
          version      varchar(10) PRIMARY KEY,
          name         varchar(200) NOT NULL,
          checksum     char(64) NOT NULL,
          applied_at   timestamptz NOT NULL DEFAULT now(),
          execution_ms integer NOT NULL
        );
        """;

    /// <summary>Đọc các file <c>*.up.sql</c> trong thư mục, sắp theo version. Version trùng → lỗi.</summary>
    public static IReadOnlyList<MigrationScript> Discover(string directory, string? devBaselinePath = null)
    {
        if (!Directory.Exists(directory)) throw new MigrationException($"Migration directory '{directory}' not found.");
        var scripts = new List<MigrationScript>();
        if (devBaselinePath is not null)
        {
            if (!File.Exists(devBaselinePath)) throw new MigrationException($"Dev baseline '{devBaselinePath}' not found.");
            scripts.Add(Load(devBaselinePath, "0000", "dev_baseline_04_stub"));
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.up.sql", SearchOption.TopDirectoryOnly))
        {
            var match = FileNamePattern().Match(System.IO.Path.GetFileName(file));
            if (!match.Success) throw new MigrationException($"Migration file '{file}' must be named NNNN_name.up.sql.");
            scripts.Add(Load(file, match.Groups["version"].Value, match.Groups["name"].Value));
        }

        var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new MigrationException($"Duplicate migration version {duplicate.Key}.");
        return scripts.OrderBy(s => s.Version, StringComparer.Ordinal).ToList();
    }

    public static string ComputeChecksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));

    public async Task<IReadOnlyList<MigrationStatus>> StatusAsync(IReadOnlyList<MigrationScript> scripts, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, null, JournalDdl, cancellationToken);
        var applied = await ReadJournalAsync(connection, null, cancellationToken);
        var result = new List<MigrationStatus>();
        foreach (var script in scripts)
        {
            result.Add(applied.TryGetValue(script.Version, out var row)
                ? new MigrationStatus(script.Version, script.Name, row.Checksum == script.Checksum ? MigrationState.Applied : MigrationState.ChecksumMismatch, row.AppliedAt)
                : new MigrationStatus(script.Version, script.Name, MigrationState.Pending, null));
        }

        foreach (var (version, row) in applied.Where(a => scripts.All(s => s.Version != a.Key)))
        {
            result.Add(new MigrationStatus(version, row.Name, MigrationState.MissingOnDisk, row.AppliedAt));
        }

        return result.OrderBy(s => s.Version, StringComparer.Ordinal).ToList();
    }

    /// <summary>Áp mọi migration còn thiếu. Trả về các version vừa chạy.</summary>
    public async Task<IReadOnlyList<string>> UpAsync(IReadOnlyList<MigrationScript> scripts, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var status = await StatusAsync(scripts, cancellationToken);
        var drift = status.Where(s => s.State == MigrationState.ChecksumMismatch).Select(s => s.Version).ToList();
        if (drift.Count > 0)
        {
            throw new MigrationException($"Applied migrations changed on disk: {string.Join(", ", drift)}. Never edit an applied migration; add a new one.");
        }

        var applied = new List<string>();
        foreach (var script in scripts.Where(s => status.Any(x => x.Version == s.Version && x.State == MigrationState.Pending)))
        {
            if (await ApplyAsync(script, cancellationToken))
            {
                applied.Add(script.Version);
                log?.Invoke($"applied {script.Version}_{script.Name}");
            }
        }

        return applied;
    }

    /// <summary>Chạy một đoạn SQL trong transaction riêng (ví dụ SQL đồng bộ tt.sites sau migration).</summary>
    public async Task RunInTransactionAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, sql, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> ApplyAsync(MigrationScript script, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);
        var journal = await ReadJournalAsync(connection, transaction, cancellationToken);
        if (journal.ContainsKey(script.Version))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false; // tiến trình khác vừa chạy xong
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await ExecuteAsync(connection, transaction, script.Sql, cancellationToken);
        }
        catch (PostgresException e)
        {
            throw new MigrationException($"{script.Version}_{script.Name} failed: {e.MessageText}{(e.Detail is null ? "" : " — " + e.Detail)}", e);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO tt_meta.schema_history (version, name, checksum, execution_ms) VALUES (@v, @n, @c, @ms)", connection, transaction))
        {
            insert.Parameters.AddWithValue("v", script.Version);
            insert.Parameters.AddWithValue("n", script.Name);
            insert.Parameters.AddWithValue("c", script.Checksum);
            insert.Parameters.AddWithValue("ms", (int)stopwatch.ElapsedMilliseconds);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static MigrationScript Load(string path, string version, string name)
    {
        var sql = File.ReadAllText(path, Encoding.UTF8);
        return new MigrationScript(version, name, path, sql, ComputeChecksum(sql));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, (string Name, string Checksum, DateTimeOffset AppliedAt)>> ReadJournalAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (string, string, DateTimeOffset)>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("SELECT version, name, checksum, applied_at FROM tt_meta.schema_history", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
        }

        return result;
    }

    [GeneratedRegex(@"^(?<version>\d{4})_(?<name>[a-z0-9_]+)\.up\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

}

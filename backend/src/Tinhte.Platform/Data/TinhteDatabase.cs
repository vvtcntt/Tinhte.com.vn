using Microsoft.Extensions.Configuration;
using Npgsql;
using Tinhte.Platform.Errors;

namespace Tinhte.Platform.Data;

/// <summary>
/// Truy cập PostgreSQL (WBS-013). Một <see cref="NpgsqlDataSource"/> cho cả tiến trình.
/// Transaction ghi luôn đặt <c>tt.actor_id</c> (SET LOCAL) để trigger/nhật ký ghi nhận người thao tác (db/README §6).
/// </summary>
public interface ITinhteDatabase
{
    /// <summary>false khi chưa cấu hình <c>ConnectionStrings:Tinhte</c> (ví dụ chạy khung local không có DB).</summary>
    bool IsConfigured { get; }

    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Chạy <paramref name="work"/> trong MỘT transaction; commit khi xong, rollback khi có ngoại lệ.</summary>
    Task<T> InTransactionAsync<T>(Guid? actorId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken cancellationToken = default);

    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}

public sealed class TinhteDatabase : ITinhteDatabase, IAsyncDisposable
{
    public const string ConnectionStringName = "Tinhte";

    private readonly NpgsqlDataSource? _dataSource;

    public TinhteDatabase(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        // DETAIL của tt.raise_domain_error là thông điệp nghiệp vụ trả cho client (db/README §6) → cần IncludeErrorDetail.
        // PostgresErrorTranslator chỉ trả DETAIL của lỗi P0001; lỗi ràng buộc chỉ nêu tên ràng buộc, không lộ giá trị dữ liệu.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { IncludeErrorDetail = true };
        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public bool IsConfigured => _dataSource is not null;

    public async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_dataSource is null)
        {
            throw new DomainProblemException(ErrorCodes.DependencyUnavailable, "Database unavailable", "The database is not configured.");
        }

        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    public async Task<T> InTransactionAsync<T>(Guid? actorId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (actorId is { } actor)
        {
            await using var set = new NpgsqlCommand("SELECT set_config('tt.actor_id', @actor, true)", connection, transaction);
            set.Parameters.AddWithValue("actor", actor.ToString());
            await set.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = await work(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        if (_dataSource is null) return false;
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            return await command.ExecuteScalarAsync(cancellationToken) is 1;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => _dataSource?.DisposeAsync() ?? ValueTask.CompletedTask;
}

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace Tinhte.Modules.Ads.Infrastructure;

/// <summary>Helper ADO.NET nhỏ của module Ads (module chỉ tham chiếu Platform, không dùng helper của News/Video).</summary>
internal static class AdSql
{
    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql) => new(sql, connection, transaction);

    public static NpgsqlCommand WithText(this NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
        return command;
    }

    public static NpgsqlCommand WithUuid(this NpgsqlCommand command, string name, Guid? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = (object?)value ?? DBNull.Value });
        return command;
    }

    public static NpgsqlCommand WithInt(this NpgsqlCommand command, string name, int value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = value });
        return command;
    }

    public static NpgsqlCommand WithLong(this NpgsqlCommand command, string name, long value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bigint) { Value = value });
        return command;
    }

    public static NpgsqlCommand WithBool(this NpgsqlCommand command, string name, bool value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Boolean) { Value = value });
        return command;
    }

    /// <summary>timestamptz: Npgsql chỉ nhận DateTimeOffset offset 0.</summary>
    public static NpgsqlCommand WithTime(this NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = (object?)value?.ToUniversalTime() ?? DBNull.Value });
        return command;
    }

    public static NpgsqlCommand WithArray<T>(this NpgsqlCommand command, string name, T[] values)
    {
        command.Parameters.AddWithValue(name, values);
        return command;
    }

    public static string? NullableString(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static Guid? NullableGuid(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    public static DateTimeOffset? NullableTime(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
}

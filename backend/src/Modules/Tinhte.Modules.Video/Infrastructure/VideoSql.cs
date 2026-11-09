using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Tinhte.Modules.Video.Application;

namespace Tinhte.Modules.Video.Infrastructure;

/// <summary>SQL + ánh xạ dòng → DTO cho video/media (Npgsql thuần, ADR-0003/ADR-0005).</summary>
internal static class VideoSql
{
    /// <summary>Video + revision hiển thị trong CMS (working, nếu chưa có thì public). Thứ tự cột khớp <see cref="ReadAdmin"/>.</summary>
    public const string AdminSelect = """
        SELECT v.id, COALESCE(w.title, ''), v.status, COALESCE(w.content_kind, 'EDITORIAL'),
               w.sponsor_name, w.sponsor_reference, w.sponsorship_evidence_ref, w.sponsorship_valid_from, w.sponsorship_valid_to,
               w.sponsorship_approved_by, w.sponsorship_approved_at,
               v.working_revision_id, v.public_revision_id, v.owner_id, v.assignee_id, v.rights_status, v.rights_expires_at,
               v.scheduled_at, v.updated_at, v.row_version, v.media_id, w.summary, v.created_at
          FROM tt.videos v
          LEFT JOIN tt.video_revisions w ON w.id = COALESCE(v.working_revision_id, v.public_revision_id)
        """;

    /// <summary>Thứ tự cột khớp <see cref="ReadMedia"/>.</summary>
    public const string MediaSelect = """
        SELECT m.id, m.status, m.generation, m.error_code, m.media_type, m.visibility, m.rights_status, m.license_id, m.rights_expires_at,
               m.created_at, m.file_name, m.content_type, m.size_bytes, m.created_by, m.updated_at
          FROM tt.media_assets m
        """;

    public sealed record AdminRow(AdminVideo Video, DateTimeOffset CreatedAt);

    public static AdminRow ReadAdmin(DbDataReader r) => new(
        new AdminVideo(
            Id: r.GetGuid(0),
            Title: r.GetString(1),
            Status: r.GetString(2),
            ContentKind: r.GetString(3),
            Sponsorship: ReadSponsorship(r, 4),
            WorkingRevisionId: NullableGuid(r, 11),
            PublicRevisionId: NullableGuid(r, 12),
            OwnerId: NullableGuid(r, 13),
            AssigneeId: NullableGuid(r, 14),
            RightsStatus: r.GetString(15),
            RightsExpiresAt: NullableTime(r, 16),
            ScheduledAt: NullableTime(r, 17),
            UpdatedAt: Time(r, 18),
            RowVersion: r.GetInt64(19),
            MediaId: NullableGuid(r, 20),
            Summary: r.IsDBNull(21) ? null : r.GetString(21)),
        Time(r, 22));

    public static MediaStatus ReadMedia(DbDataReader r) => new(
        MediaId: r.GetGuid(0),
        Status: r.GetString(1),
        Generation: r.GetInt32(2),
        ErrorCode: r.IsDBNull(3) ? null : r.GetString(3),
        MediaType: r.IsDBNull(4) ? null : r.GetString(4),
        Visibility: r.GetString(5),
        RightsStatus: r.GetString(6),
        LicenseId: NullableGuid(r, 7),
        RightsExpiresAt: NullableTime(r, 8),
        CreatedAt: Time(r, 9),
        FileName: r.IsDBNull(10) ? null : r.GetString(10),
        ContentType: r.IsDBNull(11) ? null : r.GetString(11),
        SizeBytes: r.IsDBNull(12) ? null : r.GetInt64(12),
        CreatedBy: NullableGuid(r, 13),
        UpdatedAt: Time(r, 14));

    /// <summary>5 cột sponsorship bắt buộc + 2 cột duyệt, bắt đầu từ <paramref name="o"/>. Thiếu phần bắt buộc → null.</summary>
    private static VideoSponsorship? ReadSponsorship(DbDataReader r, int o) =>
        r.IsDBNull(o) || r.IsDBNull(o + 1) || r.IsDBNull(o + 2) || r.IsDBNull(o + 3) || r.IsDBNull(o + 4)
            ? null
            : new VideoSponsorship(r.GetString(o), r.GetString(o + 1), r.GetString(o + 2), Time(r, o + 3), Time(r, o + 4),
                NullableGuid(r, o + 5), NullableTime(r, o + 6));

    public static Guid? NullableGuid(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    public static DateTimeOffset Time(DbDataReader r, int i) => r.GetFieldValue<DateTimeOffset>(i);

    public static DateTimeOffset? NullableTime(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    /// <summary>Mẫu ILIKE an toàn: thoát \, %, _ của người dùng.</summary>
    public static string LikePattern(string q) => "%" + q.Trim().Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";

    // ---------------------------------------------------------------- tham số

    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql) => new(sql, connection, tx);

    public static NpgsqlCommand WithUuid(this NpgsqlCommand command, string name, Guid? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = (object?)value ?? DBNull.Value });
        return command;
    }

    public static NpgsqlCommand WithTime(this NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = (object?)value?.ToUniversalTime() ?? DBNull.Value });
        return command;
    }

    public static NpgsqlCommand WithText(this NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
        return command;
    }

    public static NpgsqlCommand WithLong(this NpgsqlCommand command, string name, long? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bigint) { Value = (object?)value ?? DBNull.Value });
        return command;
    }

    public static NpgsqlCommand WithInterval(this NpgsqlCommand command, string name, TimeSpan value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Interval) { Value = value });
        return command;
    }
}

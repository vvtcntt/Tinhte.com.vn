using System.Data.Common;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Tinhte.Modules.News.Application;

namespace Tinhte.Modules.News.Infrastructure;

/// <summary>SQL + ánh xạ dòng → DTO cho bài viết (Npgsql thuần, ADR-0003/ADR-0005).</summary>
internal static class ArticleSql
{
    /// <summary>
    /// Bài + revision hiển thị trong CMS (working, nếu chưa có thì public). Thứ tự cột khớp <see cref="ReadAdmin"/>.
    /// </summary>
    public const string AdminSelect = """
        SELECT a.id, COALESCE(w.title, ''), a.status, COALESCE(w.content_kind, 'EDITORIAL'),
               w.sponsor_name, w.sponsor_reference, w.sponsorship_evidence_ref, w.sponsorship_valid_from, w.sponsorship_valid_to,
               w.sponsorship_approved_by, w.sponsorship_approved_at,
               a.working_revision_id, a.public_revision_id, a.owner_id, a.assignee_id, a.rights_status, a.rights_expires_at,
               a.scheduled_at, a.updated_at, a.row_version, a.slug, a.created_at
          FROM tt.articles a
          LEFT JOIN tt.article_revisions w ON w.id = COALESCE(a.working_revision_id, a.public_revision_id)
        """;

    /// <summary>Thứ tự cột khớp <see cref="ReadRevision"/>.</summary>
    public const string RevisionSelect = """
        SELECT r.id, r.article_id, r.number, r.author_id, r.status, r.created_at, r.body::text, r.title, r.content_kind,
               r.sponsor_name, r.sponsor_reference, r.sponsorship_evidence_ref, r.sponsorship_valid_from, r.sponsorship_valid_to,
               r.sponsorship_approved_by, r.sponsorship_approved_at,
               r.change_reason, r.summary, r.category_id, r.updated_at, r.published_at, r.published_by,
               r.submitted_by, r.submitted_at, r.reviewed_by, r.reviewed_at, r.review_note
          FROM tt.article_revisions r
        """;

    public sealed record AdminRow(AdminArticle Article, DateTimeOffset CreatedAt);

    public static AdminRow ReadAdmin(DbDataReader r) => new(
        new AdminArticle(
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
            Slug: r.GetString(20)),
        Time(r, 21));

    public static ArticleRevision ReadRevision(DbDataReader r, bool withBody) => new(
        RevisionId: r.GetGuid(0),
        ArticleId: r.GetGuid(1),
        Number: r.GetInt32(2),
        AuthorId: NullableGuid(r, 3),
        Status: r.GetString(4),
        CreatedAt: Time(r, 5),
        BodyBlocks: withBody ? ParseBody(r.IsDBNull(6) ? null : r.GetString(6)) : null,
        Title: r.GetString(7),
        ContentKind: r.GetString(8),
        Sponsorship: ReadSponsorship(r, 9),
        ChangeReason: r.IsDBNull(16) ? null : r.GetString(16),
        Summary: r.IsDBNull(17) ? null : r.GetString(17),
        CategoryId: NullableGuid(r, 18),
        UpdatedAt: Time(r, 19),
        PublishedAt: NullableTime(r, 20),
        PublishedBy: NullableGuid(r, 21),
        SubmittedBy: NullableGuid(r, 22),
        SubmittedAt: NullableTime(r, 23),
        ReviewedBy: NullableGuid(r, 24),
        ReviewedAt: NullableTime(r, 25),
        ReviewNote: r.IsDBNull(26) ? null : r.GetString(26));

    /// <summary>body NULL (dữ liệu cũ) đọc thành mảng rỗng.</summary>
    public static JsonElement ParseBody(string? json)
    {
        using var doc = JsonDocument.Parse(json ?? "[]");
        return doc.RootElement.Clone();
    }

    /// <summary>5 cột sponsorship bắt buộc + 2 cột duyệt, bắt đầu từ <paramref name="o"/>. Thiếu phần bắt buộc → null.</summary>
    private static Sponsorship? ReadSponsorship(DbDataReader r, int o) =>
        r.IsDBNull(o) || r.IsDBNull(o + 1) || r.IsDBNull(o + 2) || r.IsDBNull(o + 3) || r.IsDBNull(o + 4)
            ? null
            : new Sponsorship(r.GetString(o), r.GetString(o + 1), r.GetString(o + 2), Time(r, o + 3), Time(r, o + 4),
                NullableGuid(r, o + 5), NullableTime(r, o + 6));

    public static Guid? NullableGuid(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    public static DateTimeOffset Time(DbDataReader r, int i) => r.GetFieldValue<DateTimeOffset>(i);

    public static DateTimeOffset? NullableTime(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

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

    public static NpgsqlCommand WithJson(this NpgsqlCommand command, string name, JsonElement value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = value.GetRawText() });
        return command;
    }
}

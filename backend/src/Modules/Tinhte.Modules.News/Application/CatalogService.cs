using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;

namespace Tinhte.Modules.News.Application;

/// <summary>ContentSource (ADR-0007 §1).</summary>
public sealed record ContentSource(Guid Id, string Name, string Kind, string? Url, bool IsEnabled, long RowVersion);

/// <summary>License (ADR-0007 §1). <c>active</c> = bật và đang trong thời hạn (cùng quy tắc tt.license_active).</summary>
public sealed record License(Guid Id, string Code, string Name, string UsageScope, bool RequiresAttribution, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo,
    string? EvidenceRef, bool IsEnabled, bool Active, long RowVersion);

public sealed record CatalogFilter(string? Q, bool? IsEnabled)
{
    public string ScopeKey() => $"{Q}|{IsEnabled}";
}

public sealed record CatalogPosition(string Name, Guid Id);

/// <summary>
/// Danh mục nguồn nội dung và giấy phép (ADR-0007 §1, PROPOSED; FR-NEWS-006). Bảng có từ migration 0340; module Aggregation chỉ đọc.
/// Không xóa — chỉ tắt. Tên nguồn không trùng (không phân biệt hoa thường) → 409; mã giấy phép không đổi được và không trùng → 409.
/// Có bản ghi giấy phép KHÔNG có nghĩa là đã được phép khai thác: Legal nhập và chịu trách nhiệm (CLAUDE.md §3).
/// Body PATCH đọc thủ công để phân biệt trường vắng mặt với <c>null</c> (url, validFrom, validTo) và trả 422 cho trường lạ.
/// </summary>
public sealed partial class CatalogService(ITinhteDatabase database)
{
    public static readonly IReadOnlySet<string> SourceKinds = new HashSet<string>(StringComparer.Ordinal) { "ORIGINAL", "AGENCY", "PARTNER", "PRESS_RELEASE", "OTHER" };

    private const string SourceSelect = "SELECT id, name, kind, url, is_enabled, row_version FROM tt.content_sources";
    private const string LicenseSelect = """
        SELECT id, code, name, usage_scope, requires_attribution, valid_from, valid_to, evidence_ref, is_enabled, tt.license_active(id), row_version
          FROM tt.licenses
        """;

    // ---------------------------------------------------------------- nguồn

    public Task<IReadOnlyList<ContentSource>> ListSourcesAsync(CatalogFilter filter, CatalogPosition? after, int limitPlusOne, CancellationToken ct) =>
        ListAsync(SourceSelect, "name", "name", filter, after, limitPlusOne, ReadSource, ct);

    public async Task<ContentSource> CreateSourceAsync(Guid actorId, JsonElement body, CancellationToken ct)
    {
        var errors = Begin(body, allowEmpty: false);
        var name = Text(body, "name", 200, required: true, errors);
        var kind = Kind(body, required: true, errors);
        var url = Url(body, errors, out _);
        Unknown(body, errors, "name", "kind", "url");
        End(errors);
        return await database.InTransactionAsync(actorId, async (c, tx) =>
        {
            await EnsureSourceNameFreeAsync(c, tx, name!, null, ct);
            await using var insert = Cmd(c, tx, "INSERT INTO tt.content_sources (name, kind, url) VALUES (@name, @kind, @url) RETURNING id")
                .P("name", NpgsqlDbType.Text, name).P("kind", NpgsqlDbType.Text, kind).P("url", NpgsqlDbType.Text, url);
            var id = (Guid)(await insert.ExecuteScalarAsync(ct))!;
            return (await LoadSourceAsync(c, tx, id, ct))!;
        }, ct);
    }

    public async Task EnsureSourceExistsAsync(Guid id, CancellationToken ct)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        if (await LoadSourceAsync(c, null, id, ct) is null) throw NotFound("content source");
    }

    public async Task<ContentSource> PatchSourceAsync(Guid actorId, Guid id, long expected, JsonElement body, CancellationToken ct)
    {
        var errors = Begin(body, allowEmpty: false);
        var name = Text(body, "name", 200, required: false, errors);
        var kind = Kind(body, required: false, errors);
        var url = Url(body, errors, out var urlPresent);
        var enabled = Bool(body, "isEnabled", errors);
        Unknown(body, errors, "name", "kind", "url", "isEnabled");
        End(errors);
        return await database.InTransactionAsync(actorId, async (c, tx) =>
        {
            var current = await LoadSourceAsync(c, tx, id, ct, forUpdate: true) ?? throw NotFound("content source");
            ETags.EnsureMatches(current.RowVersion, expected);
            if (name is not null) await EnsureSourceNameFreeAsync(c, tx, name, id, ct);
            await using var update = Cmd(c, tx, """
                UPDATE tt.content_sources
                   SET name = COALESCE(@name, name), kind = COALESCE(@kind, kind), url = CASE WHEN @url_present THEN @url ELSE url END,
                       is_enabled = COALESCE(@enabled, is_enabled)
                 WHERE id = @id AND row_version = @expected
                """).P("name", NpgsqlDbType.Text, name).P("kind", NpgsqlDbType.Text, kind).P("url_present", NpgsqlDbType.Boolean, urlPresent)
                .P("url", NpgsqlDbType.Text, url).P("enabled", NpgsqlDbType.Boolean, enabled).P("id", NpgsqlDbType.Uuid, id).P("expected", NpgsqlDbType.Bigint, expected);
            if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            return (await LoadSourceAsync(c, tx, id, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- giấy phép

    public Task<IReadOnlyList<License>> ListLicensesAsync(CatalogFilter filter, CatalogPosition? after, int limitPlusOne, CancellationToken ct) =>
        ListAsync(LicenseSelect, "name", "(name || ' ' || code)", filter, after, limitPlusOne, ReadLicense, ct);

    /// <summary>Bằng chứng (evidenceRef) bắt buộc khi tạo — giả định chặt hơn trong lúc Legal chưa chốt ADR-0007 Q2.</summary>
    public async Task<License> CreateLicenseAsync(Guid actorId, JsonElement body, CancellationToken ct)
    {
        var errors = Begin(body, allowEmpty: false);
        var code = Code(body, errors);
        var name = Text(body, "name", 200, required: true, errors);
        var scope = Text(body, "usageScope", 4000, required: true, errors);
        var attribution = Bool(body, "requiresAttribution", errors) ?? true;
        var from = Time(body, "validFrom", errors, out _);
        var to = Time(body, "validTo", errors, out _);
        var evidence = Text(body, "evidenceRef", 500, required: true, errors);
        Validity(from, to, errors);
        Unknown(body, errors, "code", "name", "usageScope", "requiresAttribution", "validFrom", "validTo", "evidenceRef");
        End(errors);
        return await database.InTransactionAsync(actorId, async (c, tx) =>
        {
            await using (var exists = Cmd(c, tx, "SELECT 1 FROM tt.licenses WHERE code = @code").P("code", NpgsqlDbType.Text, code))
            {
                if (await exists.ExecuteScalarAsync(ct) is not null) throw Conflict($"A licence with code {code} already exists.");
            }

            await using var insert = Cmd(c, tx, """
                INSERT INTO tt.licenses (code, name, usage_scope, requires_attribution, valid_from, valid_to, evidence_ref)
                VALUES (@code, @name, @scope, @attribution, @from, @to, @evidence) RETURNING id
                """).P("code", NpgsqlDbType.Text, code).P("name", NpgsqlDbType.Text, name).P("scope", NpgsqlDbType.Text, scope)
                .P("attribution", NpgsqlDbType.Boolean, attribution).P("from", NpgsqlDbType.TimestampTz, from).P("to", NpgsqlDbType.TimestampTz, to)
                .P("evidence", NpgsqlDbType.Text, evidence);
            var id = (Guid)(await insert.ExecuteScalarAsync(ct))!;
            return (await LoadLicenseAsync(c, tx, id, ct))!;
        }, ct);
    }

    public async Task EnsureLicenseExistsAsync(Guid id, CancellationToken ct)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        if (await LoadLicenseAsync(c, null, id, ct) is null) throw NotFound("licence");
    }

    public async Task<License> PatchLicenseAsync(Guid actorId, Guid id, long expected, JsonElement body, CancellationToken ct)
    {
        var errors = Begin(body, allowEmpty: false);
        if (body.TryGetProperty("code", out _)) errors["code"] = ["The licence code cannot change (other records refer to it)."];
        var name = Text(body, "name", 200, required: false, errors);
        var scope = Text(body, "usageScope", 4000, required: false, errors);
        var attribution = Bool(body, "requiresAttribution", errors);
        var from = Time(body, "validFrom", errors, out var fromPresent);
        var to = Time(body, "validTo", errors, out var toPresent);
        var evidence = Text(body, "evidenceRef", 500, required: false, errors);
        var enabled = Bool(body, "isEnabled", errors);
        Unknown(body, errors, "code", "name", "usageScope", "requiresAttribution", "validFrom", "validTo", "evidenceRef", "isEnabled");
        End(errors);
        return await database.InTransactionAsync(actorId, async (c, tx) =>
        {
            var current = await LoadLicenseAsync(c, tx, id, ct, forUpdate: true) ?? throw NotFound("licence");
            ETags.EnsureMatches(current.RowVersion, expected);
            var newFrom = fromPresent ? from : current.ValidFrom;
            var newTo = toPresent ? to : current.ValidTo;
            var validity = new Dictionary<string, string[]>(StringComparer.Ordinal);
            Validity(newFrom, newTo, validity);
            End(validity);
            await using var update = Cmd(c, tx, """
                UPDATE tt.licenses
                   SET name = COALESCE(@name, name), usage_scope = COALESCE(@scope, usage_scope), requires_attribution = COALESCE(@attribution, requires_attribution),
                       valid_from = @from, valid_to = @to, evidence_ref = COALESCE(@evidence, evidence_ref), is_enabled = COALESCE(@enabled, is_enabled)
                 WHERE id = @id AND row_version = @expected
                """).P("name", NpgsqlDbType.Text, name).P("scope", NpgsqlDbType.Text, scope).P("attribution", NpgsqlDbType.Boolean, attribution)
                .P("from", NpgsqlDbType.TimestampTz, newFrom).P("to", NpgsqlDbType.TimestampTz, newTo).P("evidence", NpgsqlDbType.Text, evidence)
                .P("enabled", NpgsqlDbType.Boolean, enabled).P("id", NpgsqlDbType.Uuid, id).P("expected", NpgsqlDbType.Bigint, expected);
            if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            return (await LoadLicenseAsync(c, tx, id, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- nội bộ

    private async Task<IReadOnlyList<T>> ListAsync<T>(string select, string sortColumn, string searchExpression, CatalogFilter filter, CatalogPosition? after,
        int limitPlusOne, Func<DbDataReader, T> read, CancellationToken ct)
    {
        await using var c = await database.OpenConnectionAsync(ct);
        var sql = new StringBuilder(select).Append(" WHERE TRUE");
        await using var command = Cmd(c, null, "");
        if (filter.Q is { } q)
        {
            sql.Append($@" AND {searchExpression} ILIKE @q ESCAPE '\'");
            command.P("q", NpgsqlDbType.Text, "%" + q.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%");
        }

        if (filter.IsEnabled is { } enabled)
        {
            sql.Append(" AND is_enabled = @enabled");
            command.P("enabled", NpgsqlDbType.Boolean, enabled);
        }

        if (after is not null)
        {
            sql.Append($" AND ({sortColumn}, id) > (@after_name, @after_id)");
            command.P("after_name", NpgsqlDbType.Text, after.Name).P("after_id", NpgsqlDbType.Uuid, after.Id);
        }

        sql.Append($" ORDER BY {sortColumn}, id LIMIT @limit");
        command.P("limit", NpgsqlDbType.Integer, limitPlusOne);
        command.CommandText = sql.ToString();
        var rows = new List<T>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(read(r));
        return rows;
    }

    private static async Task EnsureSourceNameFreeAsync(NpgsqlConnection c, NpgsqlTransaction tx, string name, Guid? except, CancellationToken ct)
    {
        await using var exists = Cmd(c, tx, "SELECT 1 FROM tt.content_sources WHERE lower(btrim(name)) = lower(btrim(@name)) AND id IS DISTINCT FROM @except")
            .P("name", NpgsqlDbType.Text, name).P("except", NpgsqlDbType.Uuid, except);
        if (await exists.ExecuteScalarAsync(ct) is not null) throw Conflict($"A content source named '{name}' already exists.");
    }

    private static async Task<ContentSource?> LoadSourceAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct, bool forUpdate = false)
    {
        await using var command = Cmd(c, tx, SourceSelect + " WHERE id = @id" + (forUpdate ? " FOR UPDATE" : "")).P("id", NpgsqlDbType.Uuid, id);
        await using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSource(r) : null;
    }

    private static async Task<License?> LoadLicenseAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct, bool forUpdate = false)
    {
        await using var command = Cmd(c, tx, LicenseSelect + " WHERE id = @id" + (forUpdate ? " FOR UPDATE" : "")).P("id", NpgsqlDbType.Uuid, id);
        await using var r = await command.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadLicense(r) : null;
    }

    private static ContentSource ReadSource(DbDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.GetInt64(5));

    private static License ReadLicense(DbDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8), r.GetBoolean(9), r.GetInt64(10));

    private static NpgsqlCommand Cmd(NpgsqlConnection c, NpgsqlTransaction? tx, string sql) => new(sql, c, tx);

    // ---------------------------------------------------------------- đọc body

    private static Dictionary<string, string[]> Begin(JsonElement body, bool allowEmpty)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new DomainProblemException(ErrorCodes.BadRequest, "Malformed request", "The body must be a JSON object.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!allowEmpty && !body.EnumerateObject().Any()) errors["$"] = ["At least one property is required."];
        return errors;
    }

    private static void End(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0) throw new DomainProblemException(ErrorCodes.ValidationFailed, "Validation failed", "One or more fields are invalid.", errors);
    }

    private static void Unknown(JsonElement body, Dictionary<string, string[]> errors, params string[] known)
    {
        foreach (var p in body.EnumerateObject())
        {
            if (!known.Contains(p.Name, StringComparer.Ordinal)) errors[p.Name] = ["Unknown property (additionalProperties: false)."];
        }
    }

    private static string? Text(JsonElement body, string field, int max, bool required, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var v))
        {
            if (required) errors[field] = ["Required."];
            return null;
        }

        var text = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;
        if (string.IsNullOrEmpty(text) || text.Length > max)
        {
            errors[field] = [$"A non-blank string of at most {max} characters."];
            return null;
        }

        return text;
    }

    private static string? Kind(JsonElement body, bool required, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("kind", out var v))
        {
            if (required) errors["kind"] = ["Required."];
            return null;
        }

        if (v.ValueKind == JsonValueKind.String && SourceKinds.Contains(v.GetString()!)) return v.GetString();
        errors["kind"] = [$"One of {string.Join(", ", SourceKinds)}."];
        return null;
    }

    private static string? Url(JsonElement body, Dictionary<string, string[]> errors, out bool present)
    {
        present = body.TryGetProperty("url", out var v);
        if (!present || v.ValueKind == JsonValueKind.Null) return null;
        var text = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        if (text.Length is 0 or > 2048 || !UrlPattern().IsMatch(text) || !Uri.TryCreate(text, UriKind.Absolute, out _))
        {
            errors["url"] = ["An absolute http(s) URL of at most 2048 characters, or null."];
            return null;
        }

        return text;
    }

    private static bool? Bool(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var v)) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        errors[field] = ["A boolean."];
        return null;
    }

    private static DateTimeOffset? Time(JsonElement body, string field, Dictionary<string, string[]> errors, out bool present)
    {
        present = body.TryGetProperty(field, out var v);
        if (!present || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var t)) return t.ToUniversalTime();
        errors[field] = ["An RFC 3339 date-time, or null."];
        return null;
    }

    private static string? Code(JsonElement body, Dictionary<string, string[]> errors)
    {
        if (body.TryGetProperty("code", out var v) && v.ValueKind == JsonValueKind.String && CodePattern().IsMatch(v.GetString()!)) return v.GetString();
        errors["code"] = ["Uppercase letters, digits, '_', '.', '-' (1–60 characters), starting with a letter or digit."];
        return null;
    }

    private static void Validity(DateTimeOffset? from, DateTimeOffset? to, Dictionary<string, string[]> errors)
    {
        if (from is { } f && to is { } t && t <= f) errors["validTo"] = ["validTo must be after validFrom."];
    }

    private static DomainProblemException NotFound(string what) => new(ErrorCodes.NotFound, "Not found", $"No {what} with this id.");

    private static DomainProblemException Conflict(string detail) => new(ErrorCodes.StateConflict, "State conflict", detail);

    [GeneratedRegex(@"^https?://\S+\z", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_.-]{0,59}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

internal static class CatalogSqlExtensions
{
    public static NpgsqlCommand P(this NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
        return command;
    }
}

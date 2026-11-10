using System.Text;
using System.Text.Json;
using Npgsql;
using Tinhte.Modules.Aggregation.Domain;
using Tinhte.Modules.Aggregation.Domain.Collection;
using Tinhte.Modules.Aggregation.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Sites;

namespace Tinhte.Modules.Aggregation.Application;

/// <summary>
/// Luật phân loại theo chuyên mục (FR-AGG-004) và danh sách mục tin (FR-AGG-003). Cờ aggregation.radar kiểm phía server (ghi: trong
/// transaction). Chuyên mục thuộc News/taxonomy: chỉ đọc (khóa chia sẻ khi sửa luật). Site phải là site public-web của Site Registry (404).
/// </summary>
public sealed class AggregationRadarService(ITinhteDatabase database, SiteRegistry sites, NewsroomClock clock, TimeProvider time)
{
    public const int MaxKeywords = 200;
    public const int MaxKeywordLength = 100;

    // ---------------------------------------------------------------- site

    /// <summary>siteKey của Radar: site public-web chưa retired trong Site Registry, ngược lại 404 (AggregationSiteKeyQuery).</summary>
    public string RequireContentSite(string? siteKey)
    {
        if (!AggregationLimits.IsSiteKey(siteKey))
        {
            throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid siteKey", $"siteKey is required and must match {AggregationLimits.SiteKeyPattern}.");
        }

        if (!sites.Document.Sites.Any(s => s.Key == siteKey && s.Kind == "public-web" && s.Status != "retired"))
        {
            throw new DomainProblemException(ErrorCodes.NotFound, "Not found", $"'{siteKey}' is not a public-web site of the Site Registry.");
        }

        return siteKey!;
    }

    // ---------------------------------------------------------------- luật chuyên mục

    public async Task<AggregationCategoryRuleList> ListRulesAsync(string siteKey, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        await using var command = AggregationSql.Command(connection, null, RuleSelect + " WHERE c.site_key = @site AND c.is_enabled ORDER BY c.name, c.id")
            .WithText("site", siteKey);
        var items = new List<AggregationCategoryRule>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(ReadRule(r));
        return new AggregationCategoryRuleList(items);
    }

    /// <summary>Kiểm cờ + chuyên mục tồn tại (404 trước 428/412).</summary>
    public async Task EnsureCategoryExistsAsync(Guid categoryId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        await using var command = AggregationSql.Command(connection, null, "SELECT 1 FROM tt.categories WHERE id = @id").WithUuid("id", categoryId);
        if (await command.ExecuteScalarAsync(ct) is null) throw CategoryNotFound();
    }

    /// <summary>
    /// Sửa từ khóa (If-Match). Chuyên mục chưa có luật có rowVersion ảo 1: lần sửa đầu tạo dòng với row_version 2. Chuyên mục đã tắt → 409.
    /// Từ khóa mới chỉ áp cho mục thu thập sau đó (không phân loại lại cụm cũ).
    /// </summary>
    public Task<AggregationCategoryRule> PatchRuleAsync(Guid actorId, Guid categoryId, long expected, AggregationCategoryRulePatch patch, CancellationToken ct) =>
        database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            await AggregationSql.RequireRadarEnabledAsync(connection, tx, ct);
            await using (var category = AggregationSql.Command(connection, tx, "SELECT is_enabled FROM tt.categories WHERE id = @id FOR SHARE")
                .WithUuid("id", categoryId))
            {
                switch (await category.ExecuteScalarAsync(ct))
                {
                    case null: throw CategoryNotFound();
                    case false: throw new DomainProblemException(ErrorCodes.StateConflict, "Category disabled", "The category is disabled: enable it before editing its rule.");
                }
            }

            long? current;
            await using (var rule = AggregationSql.Command(connection, tx, "SELECT row_version FROM tt.agg_category_rules WHERE category_id = @id FOR UPDATE")
                .WithUuid("id", categoryId))
            {
                current = await rule.ExecuteScalarAsync(ct) as long?;
            }

            ETags.EnsureMatches(current ?? 1, expected);
            var sql = current is null
                ? """
                  INSERT INTO tt.agg_category_rules (category_id, include_keywords, exclude_keywords, entity_keywords, updated_by, row_version)
                  VALUES (@id, COALESCE(@include::varchar(100)[], '{}'), COALESCE(@exclude::varchar(100)[], '{}'), COALESCE(@entity::varchar(100)[], '{}'), @actor, 2)
                  ON CONFLICT (category_id) DO NOTHING
                  """
                : """
                  UPDATE tt.agg_category_rules
                     SET include_keywords = COALESCE(@include::varchar(100)[], include_keywords),
                         exclude_keywords = COALESCE(@exclude::varchar(100)[], exclude_keywords),
                         entity_keywords = COALESCE(@entity::varchar(100)[], entity_keywords),
                         updated_by = @actor
                   WHERE category_id = @id AND row_version = @expected
                  """;
            await using (var write = AggregationSql.Command(connection, tx, sql)
                .WithUuid("id", categoryId).WithTextArray("include", patch.IncludeKeywords).WithTextArray("exclude", patch.ExcludeKeywords)
                .WithTextArray("entity", patch.EntityKeywords).WithUuid("actor", actorId).WithLong("expected", expected))
            {
                if (await write.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            }

            await using var read = AggregationSql.Command(connection, tx, RuleSelect + " WHERE c.id = @id").WithUuid("id", categoryId);
            await using var r = await read.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return ReadRule(r);
        }, ct);

    private const string RuleSelect = """
        SELECT c.id, c.site_key, c.name, COALESCE(r.include_keywords, '{}'), COALESCE(r.exclude_keywords, '{}'), COALESCE(r.entity_keywords, '{}'),
               r.updated_by, r.updated_at, COALESCE(r.row_version, 1)
          FROM tt.categories c
          LEFT JOIN tt.agg_category_rules r ON r.category_id = c.id
        """;

    private static AggregationCategoryRule ReadRule(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetFieldValue<string[]>(4), r.GetFieldValue<string[]>(5),
        AggregationSql.NullableGuid(r, 6), AggregationSql.NullableTime(r, 7), r.GetInt64(8));

    private static DomainProblemException CategoryNotFound() => new(ErrorCodes.NotFound, "Not found", "No category with this id.");

    // ---------------------------------------------------------------- mục tin

    /// <summary>
    /// Khoảng thời gian của bộ lọc theo giờ tòa soạn: DAY = ngày lịch <paramref name="date"/> (mặc định hôm nay); LAST_24H = 24 giờ trước
    /// bây giờ (bỏ qua date).
    /// </summary>
    /// <summary>
    /// Đọc <c>date</c> (YYYY-MM-DD) và <c>window</c> (DAY | LAST_24H, mặc định DAY) của AggregationDate/AggregationWindow; sai → 400 BAD_REQUEST.
    /// <c>Rolling</c> = cửa sổ trượt theo giờ hiện tại (cursor phải mang mốc bắt đầu).
    /// </summary>
    public (DateTimeOffset From, DateTimeOffset? To, bool Rolling) ParseWindow(string? date, string? window)
    {
        var day = date is null ? (DateOnly?)null : ParseDate(date);
        var w = window ?? "DAY";
        if (w is not ("DAY" or "LAST_24H")) throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid window", "window must be DAY or LAST_24H.");
        var (from, to) = Window(day, w);
        return (from, to, w == "LAST_24H");
    }

    public static DateOnly ParseDate(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new DomainProblemException(ErrorCodes.BadRequest, "Invalid date", "date must be a calendar date (YYYY-MM-DD).");

    public (DateTimeOffset From, DateTimeOffset? To) Window(DateOnly? date, string window)
    {
        if (window == "LAST_24H") return (time.GetUtcNow() - TimeSpan.FromHours(24), null);
        var (from, to) = clock.DayRange(date ?? clock.Today(time.GetUtcNow()));
        return (from, to);
    }

    /// <summary>
    /// Thời điểm hiệu lực của mục (ADR-0008 §6, BR-11/12): published_at nếu có và không ở tương lai quá 10 phút so với lúc thấy lần đầu,
    /// ngược lại first_seen_at. Sắp xếp theo lúc thấy lần đầu (mới trước) — ổn định khi có mục mới.
    /// </summary>
    public async Task<IReadOnlyList<AggregationItem>> ListItemsAsync(AggregationItemFilter filter, AggregationItemPosition? after, int limitPlusOne, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await AggregationSql.RequireRadarEnabledAsync(connection, null, ct);
        const string effective = AggregationSql.ItemEffectiveAt;
        var sql = new StringBuilder(AggregationSql.ItemSelect).Append(" WHERE i.site_key = @site AND i.url IS NOT NULL");
        await using var command = AggregationSql.Command(connection, null, "").WithText("site", filter.SiteKey);
        sql.Append($" AND {effective} >= @from");
        command.WithTime("from", filter.From);
        if (filter.To is { } to)
        {
            sql.Append($" AND {effective} < @to");
            command.WithTime("to", to);
        }

        if (filter.SourceId is { } sourceId)
        {
            sql.Append(" AND i.content_source_id = @source");
            command.WithUuid("source", sourceId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            sql.Append(" AND i.category_id = @category");
            command.WithUuid("category", categoryId);
        }

        if (filter.Statuses.Count > 0)
        {
            sql.Append(" AND i.status = ANY(@statuses)");
            command.WithTextArray("statuses", filter.Statuses);
        }

        if (filter.Q is { } q)
        {
            sql.Append(@" AND (i.title ILIKE @q ESCAPE '\' OR i.summary ILIKE @q ESCAPE '\')");
            command.WithText("q", AggregationSql.LikePattern(q));
        }

        if (after is not null)
        {
            sql.Append(" AND (i.first_seen_at, i.id) < (@after_at, @after_id)");
            command.WithTime("after_at", after.FirstSeenAt).WithUuid("after_id", after.Id);
        }

        sql.Append(" ORDER BY i.first_seen_at DESC, i.id DESC LIMIT @limit");
        command.WithInt("limit", limitPlusOne);
        command.CommandText = sql.ToString();

        var items = new List<AggregationItem>();
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(AggregationSql.ReadItem(r));

        return items;
    }
}

/// <summary>
/// AggregationCategoryRulePatch (additionalProperties: false, minProperties: 1, uniqueItems). Mỗi danh sách ≤ 200 từ khóa 1–100 ký tự
/// (sau trim); trùng nhau (không phân biệt hoa thường) → 422.
/// </summary>
public sealed class AggregationCategoryRulePatch
{
    public IReadOnlyList<string>? IncludeKeywords { get; private init; }

    public IReadOnlyList<string>? ExcludeKeywords { get; private init; }

    public IReadOnlyList<string>? EntityKeywords { get; private init; }

    public static AggregationCategoryRulePatch Parse(JsonElement body)
    {
        var errors = PatchReader.Begin(body);
        IReadOnlyList<string>? include = null, exclude = null, entity = null;
        foreach (var p in body.EnumerateObject())
        {
            switch (p.Name)
            {
                case "includeKeywords":
                    include = Keywords(p.Name, p.Value, errors);
                    break;
                case "excludeKeywords":
                    exclude = Keywords(p.Name, p.Value, errors);
                    break;
                case "entityKeywords":
                    entity = Keywords(p.Name, p.Value, errors);
                    break;
                default:
                    errors[p.Name] = ["Unknown property (additionalProperties: false)."];
                    break;
            }
        }

        PatchReader.End(errors);
        return new AggregationCategoryRulePatch { IncludeKeywords = include, ExcludeKeywords = exclude, EntityKeywords = entity };
    }

    private static List<string>? Keywords(string field, JsonElement value, Dictionary<string, string[]> errors)
    {
        var list = PatchReader.StringArray(value)?.Select(k => k.Trim()).ToList();
        if (list is null || list.Count > AggregationRadarService.MaxKeywords)
        {
            errors[field] = [$"An array of at most {AggregationRadarService.MaxKeywords} strings."];
            return null;
        }

        if (list.Any(k => k.Length is 0 or > AggregationRadarService.MaxKeywordLength))
        {
            errors[field] = [$"Each keyword must have 1–{AggregationRadarService.MaxKeywordLength} characters."];
            return null;
        }

        if (list.Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
        {
            errors[field] = ["Keywords must be unique (uniqueItems, case-insensitive)."];
            return null;
        }

        return list;
    }
}

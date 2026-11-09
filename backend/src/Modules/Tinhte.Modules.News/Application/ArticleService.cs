using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Tinhte.Modules.News.Domain;
using Tinhte.Modules.News.Infrastructure;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;
using Tinhte.Platform.Security;

namespace Tinhte.Modules.News.Application;

/// <summary>
/// Bài viết trong CMS (WBS-022; FR-NEWS-001/002/004/005, FR-CMS-001/004/006):
/// tạo bản nháp + working revision, sửa working revision, publish pointer, lịch sử revision.
/// <list type="bullet">
///   <item>Revision DRAFT chưa public: sửa tại chỗ. Revision đã rời DRAFT hoặc đang public: PATCH tạo revision mới
///         (trigger 0320 chặn mọi đường ghi khác) → bản công khai không đổi cho tới khi publish revision mới.</item>
///   <item>Publish chỉ đổi <c>public_revision_id</c> sang revision APPROVED; trigger 0307 chặn revision SPONSORED khi cờ
///         <c>ads.sponsored_publication</c> OFF / chưa duyệt tài trợ / ngoài cửa sổ hiệu lực.</item>
///   <item>Mọi lệnh ghi khóa dòng bài (<c>FOR UPDATE</c>) rồi UPDATE <c>WHERE row_version = @expected</c> (ADR-0003 §5).</item>
/// </list>
/// </summary>
public sealed class ArticleService(ITinhteDatabase database)
{
    // ---------------------------------------------------------------- đọc

    public async Task<AdminArticle> GetAsync(Guid articleId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        return await LoadAsync(connection, null, articleId, ct) ?? throw NotFound();
    }

    public async Task<IReadOnlyList<(AdminArticle Article, DateTimeOffset CreatedAt)>> ListAsync(
        AdminArticleFilter filter, AdminArticlePosition? after, int limitPlusOne, CancellationToken ct)
    {
        var sql = new StringBuilder(ArticleSql.AdminSelect).Append(" WHERE TRUE");
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = ArticleSql.Command(connection, null, "");
        if (filter.Statuses.Count > 0)
        {
            sql.Append(" AND a.status = ANY(@statuses)");
            command.Parameters.AddWithValue("statuses", filter.Statuses.ToArray());
        }

        if (filter.OwnerId is { } owner) { sql.Append(" AND a.owner_id = @owner"); command.WithUuid("owner", owner); }
        if (filter.AssigneeId is { } assignee) { sql.Append(" AND a.assignee_id = @assignee"); command.WithUuid("assignee", assignee); }
        if (filter.UpdatedFrom is { } from) { sql.Append(" AND a.updated_at >= @from"); command.WithTime("from", from); }
        if (filter.UpdatedTo is { } to) { sql.Append(" AND a.updated_at <= @to"); command.WithTime("to", to); }
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            sql.Append(@" AND (w.title ILIKE @q ESCAPE '\' OR a.slug ILIKE @q ESCAPE '\')");
            command.WithText("q", "%" + filter.Q.Trim().Replace(@"\", @"\\", StringComparison.Ordinal)
                .Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%");
        }

        // Keyset theo (khóa sắp xếp, id): ổn định khi có bài mới chèn vào (ADR-0003 §4).
        var (order, compare, key) = filter.Sort switch
        {
            "updatedAt_asc" => ("a.updated_at ASC, a.id ASC", ">", "a.updated_at"),
            "createdAt_desc" => ("a.created_at DESC, a.id DESC", "<", "a.created_at"),
            "title_asc" => ("COALESCE(w.title, '') ASC, a.id ASC", ">", "COALESCE(w.title, '')"),
            _ => ("a.updated_at DESC, a.id DESC", "<", "a.updated_at"),
        };
        if (after is not null)
        {
            var byTitle = filter.Sort == "title_asc";
            if (byTitle ? after.Title is null : after.At is null)
            {
                throw new DomainProblemException(ErrorCodes.InvalidCursor, "Invalid cursor", "The cursor is not valid for this list. Restart from the first page.");
            }

            sql.Append($" AND ({key}, a.id) {compare} (@after_key, @after_id)");
            if (byTitle) command.WithText("after_key", after.Title);
            else command.WithTime("after_key", after.At);
            command.WithUuid("after_id", after.Id);
        }

        sql.Append(" ORDER BY ").Append(order).Append(" LIMIT @limit");
        command.Parameters.AddWithValue("limit", limitPlusOne);
        command.CommandText = sql.ToString();

        var rows = new List<(AdminArticle, DateTimeOffset)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = ArticleSql.ReadAdmin(reader);
            rows.Add((row.Article, row.CreatedAt));
        }

        return rows;
    }

    public async Task<ArticleRevisionList> ListRevisionsAsync(Guid articleId, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        if (!await ExistsAsync(connection, articleId, ct)) throw NotFound();
        await using var command = ArticleSql.Command(connection, null, ArticleSql.RevisionSelect + " WHERE r.article_id = @id ORDER BY r.number DESC")
            .WithUuid("id", articleId);
        var items = new List<ArticleRevision>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(ArticleSql.ReadRevision(reader, withBody: false));
        return new ArticleRevisionList(items);
    }

    public async Task<ArticleRevision> GetRevisionAsync(Guid articleId, Guid revisionId, Guid? compareTo, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        var revision = await LoadRevisionAsync(connection, null, articleId, revisionId, ct) ?? throw NotFound("Revision not found");
        if (compareTo is not { } otherId) return revision;
        var other = await LoadRevisionAsync(connection, null, articleId, otherId, ct) ?? throw NotFound("compareTo revision not found");
        return revision with { Diff = RevisionDiffer.Compare(other, revision) };
    }

    // ---------------------------------------------------------------- tạo bài (createArticleDraft)

    public async Task<AdminArticle> CreateAsync(Guid actorId, CreateArticleRequest request, CancellationToken ct)
    {
        var kind = request.ContentKind ?? ContentKinds.Editorial;
        var sponsorship = CheckSponsorship(kind, request.Sponsorship);
        var body = request.InitialBodyBlocks is { ValueKind: not JsonValueKind.Null } blocks ? blocks : ArticleSql.ParseBody(null);
        if (ArticlePatch.BodyBlocksError(body) is { } bodyError) throw Invalid("initialBodyBlocks", bodyError);

        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var siteKey = await CategorySiteAsync(connection, tx, request.CategoryId!.Value, ct);
            var articleId = Guid.CreateVersion7();
            var revisionId = Guid.CreateVersion7();
            var slug = await UniqueSlugAsync(connection, tx, ArticleSlug.FromTitle(request.Title), ct);

            await using (var insert = ArticleSql.Command(connection, tx, """
                INSERT INTO tt.articles (id, slug, status, category_id, owner_id, primary_site_key)
                VALUES (@id, @slug, 'DRAFT', @category, @actor, @site)
                """).WithUuid("id", articleId).WithText("slug", slug).WithUuid("category", request.CategoryId).WithUuid("actor", actorId).WithText("site", siteKey))
            {
                await insert.ExecuteNonQueryAsync(ct);
            }

            await InsertRevisionAsync(connection, tx, new RevisionContent(articleId, revisionId, 1, request.Title, request.Summary,
                request.CategoryId!.Value, body, kind, sponsorship, null), actorId, ct);

            await using (var link = ArticleSql.Command(connection, tx, "UPDATE tt.articles SET working_revision_id = @rev WHERE id = @id")
                .WithUuid("rev", revisionId).WithUuid("id", articleId))
            {
                await link.ExecuteNonQueryAsync(ct);
            }

            return (await LoadAsync(connection, tx, articleId, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- sửa (patchAdminArticlesArticleid)

    /// <summary>Quyền sửa phụ thuộc dữ liệu: bài của mình cần content.write_own, bài người khác cần content.write_any.</summary>
    public static Guid RequireEditPermission(ClaimsPrincipal user, Guid? ownerId) =>
        user.RequirePermission(ownerId is { } owner && owner == user.GetUserId() ? Permissions.ContentWriteOwn : Permissions.ContentWriteAny);

    public async Task<AdminArticle> PatchAsync(ClaimsPrincipal user, Guid articleId, long expectedRowVersion, ArticlePatch patch, CancellationToken ct)
    {
        var actorId = user.GetUserId() ?? throw new DomainProblemException(ErrorCodes.Unauthenticated, "Authentication required", "Sign in to the CMS.");
        return await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var article = await LockAsync(connection, tx, articleId, ct) ?? throw NotFound();
            RequireEditPermission(user, article.OwnerId);
            ETags.EnsureMatches(article.RowVersion, expectedRowVersion);
            if (!ArticleStatuses.Editable.Contains(article.Status))
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict",
                    $"An article in status {article.Status} cannot be edited; finish or withdraw the editorial workflow first.");
            }

            if (patch.Slug.IsSet && patch.Slug.Value != article.Slug) await EnsureSlugFreeAsync(connection, tx, articleId, patch.Slug.Value, ct);

            var workingRevisionId = article.WorkingRevisionId;
            var status = article.Status;
            var categoryForArticle = (Guid?)null;
            if (patch.TouchesRevision)
            {
                var baseRevisionId = article.WorkingRevisionId ?? article.PublicRevisionId ?? await LatestRevisionAsync(connection, tx, articleId, ct)
                    ?? throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict", "The article has no revision to edit.");
                var current = (await LoadRevisionAsync(connection, tx, articleId, baseRevisionId, ct))!;
                var next = await ApplyAsync(connection, tx, article, current, patch, ct);

                var editInPlace = current.Status == RevisionStatuses.Draft && current.RevisionId == article.WorkingRevisionId
                    && current.RevisionId != article.PublicRevisionId;
                if (editInPlace)
                {
                    await UpdateRevisionAsync(connection, tx, next, sponsorshipChanged: !SameSponsorship(current.Sponsorship, next.Sponsorship), actorId, ct);
                }
                else
                {
                    // Revision đã đóng băng (đang duyệt/đã duyệt/đang public): tạo revision mới, bản công khai giữ nguyên.
                    var number = await NextRevisionNumberAsync(connection, tx, articleId, ct);
                    next = next with { RevisionId = Guid.CreateVersion7(), Number = number, ChangeReason = patch.ChangeReason.IsSet ? patch.ChangeReason.Value : null };
                    await InsertRevisionAsync(connection, tx, next, actorId, ct);
                    workingRevisionId = next.RevisionId;
                    if (status is ArticleStatuses.ChangesRequested or ArticleStatuses.Rejected) status = ArticleStatuses.Draft;
                }

                // Chuyên mục của bài chưa từng public đi theo bản nháp; bài đã public chỉ đổi chuyên mục khi publish.
                if (article.PublicRevisionId is null) categoryForArticle = next.CategoryId;
            }

            await using var update = ArticleSql.Command(connection, tx, """
                UPDATE tt.articles
                   SET slug = COALESCE(@slug, slug),
                       rights_expires_at = CASE WHEN @set_rights THEN @rights_expires_at ELSE rights_expires_at END,
                       working_revision_id = @working,
                       status = @status,
                       category_id = COALESCE(@category, category_id)
                 WHERE id = @id AND row_version = @expected
                """)
                .WithText("slug", patch.Slug.IsSet ? patch.Slug.Value : null)
                .WithTime("rights_expires_at", patch.RightsExpiresAt.IsSet ? patch.RightsExpiresAt.Value : null)
                .WithUuid("working", workingRevisionId)
                .WithText("status", status)
                .WithUuid("category", categoryForArticle)
                .WithUuid("id", articleId);
            update.Parameters.AddWithValue("set_rights", patch.RightsExpiresAt.IsSet);
            update.Parameters.AddWithValue("expected", expectedRowVersion);
            if (await update.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();

            return (await LoadAsync(connection, tx, articleId, ct))!;
        }, ct);
    }

    // ---------------------------------------------------------------- publish pointer (publishArticle)

    public async Task<AdminArticle> PublishAsync(Guid actorId, Guid articleId, long expectedRowVersion, Guid revisionId, CancellationToken ct) =>
        await database.InTransactionAsync(actorId, async (connection, tx) =>
        {
            var article = await LockAsync(connection, tx, articleId, ct) ?? throw NotFound();
            ETags.EnsureMatches(article.RowVersion, expectedRowVersion);
            var revision = await LoadRevisionAsync(connection, tx, articleId, revisionId, ct)
                ?? throw Invalid("revisionId", "The revision does not belong to this article.");

            if (article.Status == ArticleStatuses.Retracted)
            {
                throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict", "A retracted article cannot be published again.");
            }

            if (revision.Status != RevisionStatuses.Approved)
            {
                // APPROVED do workflow duyệt (WBS-024, SoD) đặt — publish không tự duyệt thay.
                throw new DomainProblemException(ErrorCodes.StateConflict, "State conflict",
                    $"Only an APPROVED revision can be published; revision {revision.Number} is {revision.Status}.");
            }

            if (!article.RightsValid)
            {
                throw new DomainProblemException(ErrorCodes.RightsInvalid, "Rights invalid",
                    $"Rights status is {article.RightsStatus}{(article.RightsExpiresAt is { } e ? $", expiring {e:O}" : "")}: publishing requires CLEARED, unexpired rights.");
            }

            // Đổi con trỏ trước: trigger 0307 kiểm SPONSORED (cờ, duyệt tài trợ, cửa sổ hiệu lực) trong chính transaction này.
            await using (var pointer = ArticleSql.Command(connection, tx, """
                UPDATE tt.articles
                   SET public_revision_id = @rev, status = 'PUBLISHED', published_at = COALESCE(published_at, now()),
                       category_id = COALESCE(@category, category_id), scheduled_at = NULL
                 WHERE id = @id AND row_version = @expected
                """).WithUuid("rev", revisionId).WithUuid("category", revision.CategoryId).WithUuid("id", articleId))
            {
                pointer.Parameters.AddWithValue("expected", expectedRowVersion);
                if (await pointer.ExecuteNonQueryAsync(ct) == 0) throw ETags.RevisionConflict();
            }

            await using (var revisions = ArticleSql.Command(connection, tx, """
                UPDATE tt.article_revisions SET status = 'SUPERSEDED' WHERE id = @old AND id <> @rev;
                UPDATE tt.article_revisions SET status = 'PUBLISHED', published_at = now(), published_by = @actor WHERE id = @rev;
                """).WithUuid("old", article.PublicRevisionId).WithUuid("rev", revisionId).WithUuid("actor", actorId))
            {
                await revisions.ExecuteNonQueryAsync(ct);
            }

            // TODO(WBS-020): phát sự kiện ContentPublished qua outbox trong cùng transaction.
            return (await LoadAsync(connection, tx, articleId, ct))!;
        }, ct);

    // ---------------------------------------------------------------- nội bộ

    private sealed record LockedArticle(
        string Status, long RowVersion, Guid? OwnerId, Guid? WorkingRevisionId, Guid? PublicRevisionId, string Slug,
        string SiteKey, string RightsStatus, DateTimeOffset? RightsExpiresAt, bool RightsValid);

    /// <summary>Nội dung một revision sắp ghi.</summary>
    private sealed record RevisionContent(
        Guid ArticleId, Guid RevisionId, int Number, string Title, string? Summary, Guid CategoryId, JsonElement Body,
        string ContentKind, SponsorshipInput? Sponsorship, string? ChangeReason);

    private static DomainProblemException NotFound(string title = "Article not found") =>
        new(ErrorCodes.NotFound, title, "No article/revision with this id.");

    private static DomainProblemException Invalid(string field, string message) =>
        new(ErrorCodes.ValidationFailed, "Validation failed", message, new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>SponsorshipRequiredRule: SPONSORED cần sponsorship đầy đủ (422 SPONSORSHIP_LABEL_REQUIRED); EDITORIAL không mang sponsorship.</summary>
    private static SponsorshipInput? CheckSponsorship(string kind, SponsorshipInput? sponsorship)
    {
        if (kind == ContentKinds.Sponsored && sponsorship is null)
        {
            throw new DomainProblemException(ErrorCodes.SponsorshipLabelRequired, "Sponsorship label required",
                "contentKind SPONSORED requires a complete sponsorship (sponsorName, sponsorReference, evidenceRef, validFrom, validTo).");
        }

        if (kind == ContentKinds.Editorial && sponsorship is not null) throw Invalid("sponsorship", "Only SPONSORED content carries sponsorship.");
        if (sponsorship is { ValidFrom: { } from, ValidTo: { } to } && to <= from) throw Invalid("sponsorship.validTo", "validTo must be after validFrom.");
        return sponsorship;
    }

    private static bool SameSponsorship(Sponsorship? current, SponsorshipInput? next) =>
        current is null ? next is null
        : next is not null && current.SponsorName == next.SponsorName && current.SponsorReference == next.SponsorReference
          && current.EvidenceRef == next.EvidenceRef && current.ValidFrom == next.ValidFrom && current.ValidTo == next.ValidTo;

    private static SponsorshipInput? ToInput(Sponsorship? s) =>
        s is null ? null : new SponsorshipInput(s.SponsorName, s.SponsorReference, s.EvidenceRef, s.ValidFrom, s.ValidTo);

    private async Task<RevisionContent> ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction tx, LockedArticle article, ArticleRevision current, ArticlePatch patch, CancellationToken ct)
    {
        var categoryId = patch.CategoryId.IsSet ? patch.CategoryId.Value : current.CategoryId;
        if (categoryId is not { } category) throw Invalid("categoryId", "The article has no category; set categoryId.");
        if (patch.CategoryId.IsSet && await CategorySiteAsync(connection, tx, category, ct) != article.SiteKey)
        {
            throw Invalid("categoryId", "The category belongs to another site.");
        }

        var kind = patch.ContentKind.IsSet ? patch.ContentKind.Value : current.ContentKind;
        var sponsorship = patch.Sponsorship.IsSet ? patch.Sponsorship.Value
            : kind == ContentKinds.Editorial ? null   // chuyển sang EDITORIAL: bỏ sponsorship cũ
            : ToInput(current.Sponsorship);
        CheckSponsorship(kind, sponsorship);

        return new RevisionContent(
            current.ArticleId, current.RevisionId, current.Number,
            patch.Title.IsSet ? patch.Title.Value : current.Title,
            patch.Summary.IsSet ? patch.Summary.Value : current.Summary,
            category,
            patch.BodyBlocks.IsSet ? patch.BodyBlocks.Value : current.BodyBlocks ?? ArticleSql.ParseBody(null),
            kind, sponsorship,
            patch.ChangeReason.IsSet ? patch.ChangeReason.Value : current.ChangeReason);
    }

    private static async Task InsertRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, RevisionContent c, Guid actorId, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx, """
            INSERT INTO tt.article_revisions (id, article_id, number, title, summary, body, category_id, author_id, updated_by, status,
                content_kind, sponsor_name, sponsor_reference, sponsorship_evidence_ref, sponsorship_valid_from, sponsorship_valid_to, change_reason)
            VALUES (@id, @article, @number, @title, @summary, @body, @category, @actor, @actor, 'DRAFT',
                @kind, @sponsor_name, @sponsor_reference, @evidence, @valid_from, @valid_to, @reason)
            """);
        command.Parameters.AddWithValue("number", c.Number);
        await BindContent(command, c).WithUuid("id", c.RevisionId).WithUuid("article", c.ArticleId).WithUuid("actor", actorId).ExecuteNonQueryAsync(ct);
    }

    /// <summary>Sửa tại chỗ revision DRAFT. Đổi sponsorship → xóa duyệt tài trợ cũ (phải duyệt lại).</summary>
    private static async Task UpdateRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, RevisionContent c, bool sponsorshipChanged, Guid actorId, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx, """
            UPDATE tt.article_revisions
               SET title = @title, summary = @summary, body = @body, category_id = @category, content_kind = @kind,
                   sponsor_name = @sponsor_name, sponsor_reference = @sponsor_reference, sponsorship_evidence_ref = @evidence,
                   sponsorship_valid_from = @valid_from, sponsorship_valid_to = @valid_to, change_reason = @reason, updated_by = @actor,
                   sponsorship_approved_by = CASE WHEN @reset_approval THEN NULL ELSE sponsorship_approved_by END,
                   sponsorship_approved_at = CASE WHEN @reset_approval THEN NULL ELSE sponsorship_approved_at END
             WHERE id = @id
            """);
        command.Parameters.AddWithValue("reset_approval", sponsorshipChanged);
        await BindContent(command, c).WithUuid("id", c.RevisionId).WithUuid("actor", actorId).ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand BindContent(NpgsqlCommand command, RevisionContent c) => command
        .WithText("title", c.Title)
        .WithText("summary", c.Summary)
        .WithJson("body", c.Body)
        .WithUuid("category", c.CategoryId)
        .WithText("kind", c.ContentKind)
        .WithText("sponsor_name", c.Sponsorship?.SponsorName)
        .WithText("sponsor_reference", c.Sponsorship?.SponsorReference)
        .WithText("evidence", c.Sponsorship?.EvidenceRef)
        .WithTime("valid_from", c.Sponsorship?.ValidFrom)
        .WithTime("valid_to", c.Sponsorship?.ValidTo)
        .WithText("reason", c.ChangeReason);

    private static async Task<AdminArticle?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid articleId, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx, ArticleSql.AdminSelect + " WHERE a.id = @id").WithUuid("id", articleId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ArticleSql.ReadAdmin(reader).Article : null;
    }

    private static async Task<ArticleRevision?> LoadRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid articleId, Guid revisionId, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx, ArticleSql.RevisionSelect + " WHERE r.id = @rev AND r.article_id = @id")
            .WithUuid("rev", revisionId).WithUuid("id", articleId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ArticleSql.ReadRevision(reader, withBody: true) : null;
    }

    private static async Task<LockedArticle?> LockAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid articleId, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx, """
            SELECT status, row_version, owner_id, working_revision_id, public_revision_id, slug, primary_site_key, rights_status, rights_expires_at,
                   rights_status = 'CLEARED' AND (rights_expires_at IS NULL OR rights_expires_at > now())
              FROM tt.articles WHERE id = @id FOR UPDATE
            """).WithUuid("id", articleId);
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new LockedArticle(r.GetString(0), r.GetInt64(1), ArticleSql.NullableGuid(r, 2), ArticleSql.NullableGuid(r, 3),
            ArticleSql.NullableGuid(r, 4), r.GetString(5), r.GetString(6), r.GetString(7), ArticleSql.NullableTime(r, 8), r.GetBoolean(9));
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, Guid articleId, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, null, "SELECT EXISTS (SELECT 1 FROM tt.articles WHERE id = @id)").WithUuid("id", articleId);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<Guid?> LatestRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid articleId, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx, "SELECT id FROM tt.article_revisions WHERE article_id = @id ORDER BY number DESC LIMIT 1")
            .WithUuid("id", articleId);
        return await command.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task<int> NextRevisionNumberAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid articleId, CancellationToken ct)
    {
        // Dòng bài đã bị khóa FOR UPDATE → không có hai transaction cùng cấp số.
        await using var command = ArticleSql.Command(connection, tx, "SELECT COALESCE(max(number), 0) + 1 FROM tt.article_revisions WHERE article_id = @id")
            .WithUuid("id", articleId);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Chuyên mục phải tồn tại, đang bật; trả site_key (bài thuộc site của chuyên mục — ADR-0001 D8).</summary>
    private static async Task<string> CategorySiteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid categoryId, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx, "SELECT site_key FROM tt.categories WHERE id = @id AND is_enabled")
            .WithUuid("id", categoryId);
        return await command.ExecuteScalarAsync(ct) as string ?? throw Invalid("categoryId", "Unknown or disabled category.");
    }

    private static async Task<bool> SlugTakenAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid? exceptArticleId, string slug, CancellationToken ct)
    {
        await using var command = ArticleSql.Command(connection, tx, """
            SELECT EXISTS (SELECT 1 FROM tt.articles WHERE slug = @slug AND id IS DISTINCT FROM @except)
                OR EXISTS (SELECT 1 FROM tt.article_slug_redirects WHERE old_slug = @slug AND article_id IS DISTINCT FROM @except)
            """).WithText("slug", slug).WithUuid("except", exceptArticleId);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>FR-NEWS-002: slug (kể cả slug cũ đang là redirect của bài khác) không bị chiếm → 409 STATE_CONFLICT.</summary>
    private static async Task EnsureSlugFreeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid articleId, string slug, CancellationToken ct)
    {
        if (await SlugTakenAsync(connection, tx, articleId, slug, ct))
        {
            throw new DomainProblemException(ErrorCodes.StateConflict, "Slug taken", $"Slug \"{slug}\" is used by another article (or reserved as its redirect).");
        }
    }

    private static async Task<string> UniqueSlugAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!await SlugTakenAsync(connection, tx, null, slug, ct)) return slug;
            slug = $"{baseSlug}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
        }

        throw new DomainProblemException(ErrorCodes.StateConflict, "Slug taken", "Could not allocate a unique slug; retry the request.");
    }
}

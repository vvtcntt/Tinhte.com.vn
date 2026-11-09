using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.News.Application;
using Tinhte.Modules.News.Domain;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>Gửi request admin/public tới API bài viết (WBS-022) với phiên staff giả.</summary>
internal static class NewsHttp
{
    public const string Csrf = "csrf-token-for-tests";

    public static HttpRequestMessage Admin(HttpMethod method, string path, Guid user, string roles, bool mfa = false, string? json = null,
        string? ifMatch = null, string? idempotencyKey = null)
    {
        var request = TestHost.Write(method, path, json, user.ToString(), idempotencyKey, ifMatch, TestHost.CmsOrigin);
        request.Headers.Add(TestUserStartupFilter.RolesHeader, roles);
        request.Headers.Add(TestUserStartupFilter.CsrfHeader, Csrf);
        request.Headers.Add(CsrfMiddleware.HeaderName, Csrf);
        if (mfa) request.Headers.Add(TestUserStartupFilter.MfaHeader, "true");
        return request;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await JsonAsync(response, status);
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }
}

/// <summary>WBS-022 — phần không cần DB: slug, PATCH, diff, bề mặt API, quyền.</summary>
public sealed class NewsArticleUnitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("Đánh giá iPhone 17 Pro: pin “trâu”, giá 30 triệu!", "danh-gia-iphone-17-pro-pin-trau-gia-30-trieu")]
    [InlineData("  Ưu đãi ĐẶC BIỆT -- tháng 11  ", "uu-dai-dac-biet-thang-11")]
    [InlineData("!!! ??? ...", "bai-viet")]
    [InlineData("Tiếng Việt", "tieng-viet")]   // NFD (dấu tổ hợp)
    public void Slug_is_generated_from_vietnamese_title(string title, string slug)
    {
        Assert.Equal(slug, ArticleSlug.FromTitle(title));
        Assert.True(ArticleSlug.IsValid(ArticleSlug.FromTitle(title)));
    }

    [Fact]
    public void Slug_folds_every_vietnamese_letter_and_stays_within_limit()
    {
        const string all = "àáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵđ";
        Assert.Matches("^[adeiouy]+$", ArticleSlug.FromTitle(all));
        Assert.Equal(all.Length, ArticleSlug.FromTitle(all).Length);
        Assert.Equal(ArticleSlug.FromTitle(all.ToUpperInvariant()), ArticleSlug.FromTitle(all));
        var longSlug = ArticleSlug.FromTitle(string.Join(' ', Enumerable.Repeat("công nghệ", 100)));
        Assert.True(longSlug.Length <= 200 && ArticleSlug.IsValid(longSlug));
        Assert.False(ArticleSlug.IsValid("Bai-Mot"));
        Assert.False(ArticleSlug.IsValid("bai--mot"));
        Assert.False(ArticleSlug.IsValid(new string('a', 251)));
    }

    private static ArticlePatch Parse(string json) => ArticlePatch.Parse(JsonDocument.Parse(json).RootElement);

    private static IDictionary<string, string[]> PatchErrors(string json) =>
        Assert.Throws<DomainProblemException>(() => Parse(json)) is { Code: ErrorCodes.ValidationFailed, Errors: { } e } ? e : throw new Xunit.Sdk.XunitException("no errors");

    [Fact]
    public void Patch_distinguishes_absent_from_null_and_validates_fields()
    {
        var p = Parse("""{"summary":null,"rightsExpiresAt":null,"title":"Mới"}""");
        Assert.True(p.Summary.IsSet);
        Assert.Null(p.Summary.Value);
        Assert.True(p.RightsExpiresAt.IsSet);
        Assert.False(p.Slug.IsSet);
        Assert.True(p.TouchesRevision);
        Assert.False(Parse("""{"slug":"bai-moi"}""").TouchesRevision);

        Assert.Contains("$", PatchErrors("{}").Keys);
        Assert.Contains("$", PatchErrors("""{"unknown":1}""").Keys);
        Assert.Contains("title", PatchErrors("""{"title":"   "}""").Keys);
        Assert.Contains("slug", PatchErrors("""{"slug":"Có Dấu"}""").Keys);
        Assert.Contains("bodyBlocks", PatchErrors("""{"bodyBlocks":[1]}""").Keys);
        Assert.Contains("contentKind", PatchErrors("""{"contentKind":"ADVERTORIAL"}""").Keys);
        Assert.Contains("sponsorship.validTo", PatchErrors("""{"sponsorship":{"sponsorName":"A","sponsorReference":"B","evidenceRef":"C","validFrom":"2026-01-01T00:00:00Z"}}""").Keys);
        // Taxonomy nguồn/giấy phép (WBS-023) chưa có: từ chối rõ ràng, không bỏ qua im lặng.
        Assert.Contains("sourceId", PatchErrors($$"""{"sourceId":"{{Guid.NewGuid()}}"}""").Keys);
    }

    [Fact]
    public void Diff_lists_changed_fields_and_blocks()
    {
        static ArticleRevision Rev(string title, string body) => new(Guid.NewGuid(), Guid.NewGuid(), 1, null, "DRAFT", DateTimeOffset.UnixEpoch,
            JsonDocument.Parse(body).RootElement, title, ContentKinds.Editorial, null, null, null, null, DateTimeOffset.UnixEpoch, null, null);
        var a = Rev("A", """[{"t":"p","x":1},{"t":"p","x":2}]""");
        var b = Rev("B", """[{"x":1,"t":"p"},{"t":"p","x":3},{"t":"img"}]""");
        var diff = RevisionDiffer.Compare(a, b);
        Assert.Equal(a.RevisionId, diff.CompareTo);
        Assert.Equal(["/title", "/bodyBlocks/1", "/bodyBlocks/2"], diff.Changes.Select(c => c.Path));
        Assert.Null(diff.Changes[2].Before);
        Assert.Empty(RevisionDiffer.Compare(a, a).Changes);
    }

    [Fact]
    public async Task Admin_article_api_requires_a_staff_session_and_is_only_on_cms()
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        using (var anonymous = await cms.GetAsync("/api/v1/admin/articles"))
        {
            await NewsHttp.ProblemAsync(anonymous, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        }

        using var www = factory.WithProbes().Client(TestHost.Www);
        using var onPublicSite = await www.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/articles", Guid.NewGuid(), StaffRoles.Editor));
        Assert.Equal(HttpStatusCode.NotFound, onPublicSite.StatusCode);
        using var publicOnCms = await cms.GetAsync("/api/v1/articles/bai-mot");
        Assert.Equal(HttpStatusCode.NotFound, publicOnCms.StatusCode);
    }

    [Theory]
    [InlineData(StaffRoles.Editor, true, HttpStatusCode.Forbidden)]     // không có content.publish
    [InlineData(StaffRoles.Approver, false, HttpStatusCode.Forbidden)]  // content.publish cần MFA
    [InlineData(StaffRoles.Admin, true, HttpStatusCode.Forbidden)]      // quản trị tài khoản không đụng nội dung
    public async Task Publish_requires_content_publish_with_mfa(string roles, bool mfa, HttpStatusCode status)
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, $"/api/v1/admin/articles/{Guid.NewGuid()}/publish", Guid.NewGuid(), roles, mfa,
            $$"""{"revisionId":"{{Guid.NewGuid()}}"}""", "W/\"1\"", "publish-key-1"));
        await NewsHttp.ProblemAsync(response, status, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Without_database_article_endpoints_return_503()
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        using var list = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/articles", Guid.NewGuid(), StaffRoles.Author));
        await NewsHttp.ProblemAsync(list, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
        using var www = factory.WithProbes().Client(TestHost.Www);
        using var article = await www.GetAsync("/api/v1/articles/bai-mot");
        await NewsHttp.ProblemAsync(article, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
    }

    [Fact]
    public async Task Bad_query_parameters_return_400_before_touching_the_database()
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        foreach (var query in new[] { "status=LIVE", "sort=random", "ownerId=abc", "limit=0" })
        {
            using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/articles?" + query, Guid.NewGuid(), StaffRoles.Author));
            await NewsHttp.ProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
        }
    }
}

/// <summary>WBS-022 trên PostgreSQL thật: tạo → sửa → publish pointer → đọc công khai, revision bất biến, redirect slug, sponsored gate.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class NewsArticleDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly Guid _author = Guid.NewGuid();
    private readonly Guid _otherAuthor = Guid.NewGuid();
    private readonly Guid _editor = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly Guid _category = Guid.NewGuid();
    private readonly string _categorySlug = "cat-" + Guid.NewGuid().ToString("N")[..8];

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) return;
        await ExecAsync($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_author}', 'Tác giả'), ('{_otherAuthor}', 'Tác giả khác'), ('{_editor}', 'Biên tập'), ('{_approver}', 'Người duyệt');
            INSERT INTO tt.categories (id, slug, name) VALUES ('{_category}', '{_categorySlug}', 'Công nghệ');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Giả lập workflow chưa có (WBS-023 rights, WBS-024 duyệt): quyền CLEARED + revision APPROVED.
    /// Đổi quyền là sửa bài → row_version tăng: trả ETag mới.
    /// </summary>
    private async Task<string> ApproveAsync(Guid articleId, Guid revisionId, bool sponsorshipApproved = false)
    {
        await ExecAsync($"""
            UPDATE tt.articles SET rights_status = 'CLEARED' WHERE id = '{articleId}' AND rights_status <> 'CLEARED';
            UPDATE tt.article_revisions SET status = 'IN_REVIEW' WHERE id = '{revisionId}' AND status = 'DRAFT';
            UPDATE tt.article_revisions SET status = 'APPROVED'
              {(sponsorshipApproved ? $", sponsorship_approved_by = '{_approver}', sponsorship_approved_at = now()" : "")} WHERE id = '{revisionId}';
            """);
        return await ETagAsync(articleId);
    }

    private async Task<string> ETagAsync(Guid articleId) =>
        $"W/\"{await db.ScalarAsync<long>($"SELECT row_version FROM tt.articles WHERE id = '{articleId}'")}\"";

    private HttpClient Cms() => factory.WithProbes(db.ConnectionString).Client(TestHost.Cms);

    private HttpClient Www() => factory.WithProbes(db.ConnectionString).Client(TestHost.Www);

    private async Task<(JsonElement Article, string ETag)> CreateAsync(HttpClient cms, string title, Guid? owner = null, string? extra = null)
    {
        var json = $$"""{"title":"{{title}}","summary":"Sapo","categoryId":"{{_category}}","initialBodyBlocks":[{"type":"paragraph","text":"Xin chào"}]{{extra}}}""";
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", owner ?? _author, StaffRoles.Author, json: json,
            idempotencyKey: "create-" + Guid.NewGuid()));
        var body = await NewsHttp.JsonAsync(response, HttpStatusCode.Created);
        return (body, response.Headers.ETag!.ToString());
    }

    private async Task<(JsonElement Article, string ETag)> PatchAsync(HttpClient cms, Guid articleId, string etag, string json, Guid? user = null, string roles = StaffRoles.Author)
    {
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/articles/{articleId}", user ?? _author, roles, json: json, ifMatch: etag));
        var body = await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
        return (body, response.Headers.ETag!.ToString());
    }

    private async Task<HttpResponseMessage> PublishAsync(HttpClient cms, Guid articleId, Guid revisionId, string etag, bool mfa = true) =>
        await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, $"/api/v1/admin/articles/{articleId}/publish", _approver, StaffRoles.Approver, mfa,
            $$"""{"revisionId":"{{revisionId}}"}""", etag, "publish-" + Guid.NewGuid()));

    [DbFact]
    public async Task Admin_categories_lists_categories_for_staff_with_content_read()
    {
        using var cms = Cms();
        using var ok = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/categories", _author, StaffRoles.Author));
        var list = await NewsHttp.JsonAsync(ok, HttpStatusCode.OK);
        var mine = list.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == _category);
        Assert.Equal(_categorySlug, mine.GetProperty("slug").GetString());
        Assert.True(mine.GetProperty("isEnabled").GetBoolean());
        Assert.True(mine.GetProperty("rowVersion").GetInt64() >= 1);

        using var moderator = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/categories", _author, StaffRoles.Moderator));
        Assert.Equal(HttpStatusCode.Forbidden, moderator.StatusCode);
    }

    [DbFact]
    public async Task Create_returns_draft_with_working_revision_etag_and_location()
    {
        using var cms = Cms();
        var (article, etag) = await CreateAsync(cms, "Đánh giá Galaxy S26 Ultra");
        var id = article.GetProperty("id").GetGuid();
        Assert.Equal("DRAFT", article.GetProperty("status").GetString());
        Assert.Equal("EDITORIAL", article.GetProperty("contentKind").GetString());
        Assert.StartsWith("danh-gia-galaxy-s26-ultra", article.GetProperty("slug").GetString(), StringComparison.Ordinal);
        Assert.Equal(_author, article.GetProperty("ownerId").GetGuid());
        Assert.Equal(JsonValueKind.Null, article.GetProperty("publicRevisionId").ValueKind);
        Assert.Equal("UNVERIFIED", article.GetProperty("rightsStatus").GetString());
        Assert.Equal($"W/\"{article.GetProperty("rowVersion").GetInt64()}\"", etag);

        using var get = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles/{id}", _approver, StaffRoles.Approver));
        Assert.Equal(id, (await NewsHttp.JsonAsync(get, HttpStatusCode.OK)).GetProperty("id").GetGuid());
        Assert.Equal(etag, get.Headers.ETag!.ToString());

        using var revisions = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles/{id}/revisions", _author, StaffRoles.Author));
        var items = (await NewsHttp.JsonAsync(revisions, HttpStatusCode.OK)).GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(article.GetProperty("workingRevisionId").GetGuid(), items[0].GetProperty("revisionId").GetGuid());
        Assert.Equal("DRAFT", items[0].GetProperty("status").GetString());
        Assert.Equal(_category, items[0].GetProperty("categoryId").GetGuid());
        Assert.False(items[0].TryGetProperty("bodyBlocks", out _));   // danh sách không kèm nội dung

        // Slug trùng tiêu đề → hậu tố, không chiếm slug của bài khác.
        var (second, _) = await CreateAsync(cms, "Đánh giá Galaxy S26 Ultra");
        Assert.NotEqual(article.GetProperty("slug").GetString(), second.GetProperty("slug").GetString());
    }

    [DbFact]
    public async Task Create_is_idempotent_and_validates_input()
    {
        using var cms = Cms();
        var json = $$"""{"title":"Idempotent","categoryId":"{{_category}}"}""";
        var key = "create-" + Guid.NewGuid();
        using var first = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", _author, StaffRoles.Author, json: json, idempotencyKey: key));
        using var replay = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", _author, StaffRoles.Author, json: json, idempotencyKey: key));
        Assert.Equal((await NewsHttp.JsonAsync(first, HttpStatusCode.Created)).GetProperty("id").GetGuid(),
            (await NewsHttp.JsonAsync(replay, HttpStatusCode.Created)).GetProperty("id").GetGuid());
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());

        async Task Rejected(string body, HttpStatusCode status, string code)
        {
            using var r = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", _author, StaffRoles.Author, json: body, idempotencyKey: "bad-" + Guid.NewGuid()));
            await NewsHttp.ProblemAsync(r, status, code);
        }

        await Rejected($$"""{"title":"x","categoryId":"{{Guid.NewGuid()}}"}""", HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        await Rejected("""{"title":"x"}""", HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        await Rejected($$"""{"title":"x","categoryId":"{{_category}}","initialBodyBlocks":"text"}""", HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        await Rejected($$"""{"title":"x","categoryId":"{{_category}}","contentKind":"SPONSORED"}""", HttpStatusCode.UnprocessableEntity, ErrorCodes.SponsorshipLabelRequired);

        // MODERATOR không có content.write_own.
        using var moderator = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", _author, StaffRoles.Moderator, json: json, idempotencyKey: "mod-" + Guid.NewGuid()));
        await NewsHttp.ProblemAsync(moderator, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [DbFact]
    public async Task Patch_requires_if_match_and_edits_the_draft_in_place()
    {
        using var cms = Cms();
        var (article, etag) = await CreateAsync(cms, "Bản nháp");
        var id = article.GetProperty("id").GetGuid();
        var path = $"/api/v1/admin/articles/{id}";

        using (var missing = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, path, _author, StaffRoles.Author, json: """{"title":"x"}""")))
        {
            await NewsHttp.ProblemAsync(missing, (HttpStatusCode)428, ErrorCodes.PreconditionRequired);
        }

        using (var stale = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, path, _author, StaffRoles.Author, json: """{"title":"x"}""", ifMatch: "W/\"999\"")))
        {
            await NewsHttp.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
        }

        using (var unknown = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/articles/{Guid.NewGuid()}", _author, StaffRoles.Author, json: """{"title":"x"}""", ifMatch: etag)))
        {
            await NewsHttp.ProblemAsync(unknown, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        var (patched, newEtag) = await PatchAsync(cms, id, etag, """{"title":"Bản nháp đã sửa","summary":null,"bodyBlocks":[{"type":"paragraph","text":"Mới"}],"changeReason":"Sửa lỗi chính tả"}""");
        Assert.Equal("Bản nháp đã sửa", patched.GetProperty("title").GetString());
        Assert.Equal(article.GetProperty("workingRevisionId").GetGuid(), patched.GetProperty("workingRevisionId").GetGuid());   // sửa tại chỗ
        Assert.True(patched.GetProperty("rowVersion").GetInt64() > article.GetProperty("rowVersion").GetInt64());
        Assert.NotEqual(etag, newEtag);

        using var revision = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"{path}/revisions/{patched.GetProperty("workingRevisionId").GetGuid()}", _author, StaffRoles.Author));
        var rev = await NewsHttp.JsonAsync(revision, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, rev.GetProperty("summary").ValueKind);
        Assert.Equal("Mới", rev.GetProperty("bodyBlocks")[0].GetProperty("text").GetString());
        Assert.Equal("Sửa lỗi chính tả", rev.GetProperty("changeReason").GetString());

        // Quyền theo dữ liệu: AUTHOR khác không sửa được bài người khác; EDITOR (content.write_any) thì được.
        using (var other = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, path, _otherAuthor, StaffRoles.Author, json: """{"title":"y"}""", ifMatch: newEtag)))
        {
            await NewsHttp.ProblemAsync(other, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }

        await PatchAsync(cms, id, newEtag, """{"title":"Biên tập viên sửa"}""", _editor, StaffRoles.Editor);
    }

    [DbFact]
    public async Task Publish_pointer_keeps_public_revision_immutable_until_next_publish()
    {
        using var cms = Cms();
        using var www = Www();
        var (article, etag) = await CreateAsync(cms, "Bài sẽ xuất bản");
        var id = article.GetProperty("id").GetGuid();
        var slug = article.GetProperty("slug").GetString()!;
        var rev1 = article.GetProperty("workingRevisionId").GetGuid();

        using (var draft = await www.GetAsync($"/api/v1/articles/{slug}"))
        {
            await NewsHttp.ProblemAsync(draft, HttpStatusCode.NotFound, ErrorCodes.NotFound);   // bản nháp không công khai
        }

        using (var notApproved = await PublishAsync(cms, id, rev1, etag))
        {
            await NewsHttp.ProblemAsync(notApproved, HttpStatusCode.Conflict, ErrorCodes.StateConflict);   // chưa qua duyệt (WBS-024)
        }

        await ExecAsync($"UPDATE tt.article_revisions SET status = 'IN_REVIEW' WHERE id = '{rev1}'; UPDATE tt.article_revisions SET status = 'APPROVED' WHERE id = '{rev1}';");
        using (var noRights = await PublishAsync(cms, id, rev1, etag))
        {
            await NewsHttp.ProblemAsync(noRights, HttpStatusCode.UnprocessableEntity, ErrorCodes.RightsInvalid);   // quyền UNVERIFIED
        }

        etag = await ApproveAsync(id, rev1);
        using (var foreign = await PublishAsync(cms, id, Guid.NewGuid(), etag))
        {
            await NewsHttp.ProblemAsync(foreign, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        }

        using var published = await PublishAsync(cms, id, rev1, etag);
        var pub = await NewsHttp.JsonAsync(published, HttpStatusCode.OK);
        Assert.Equal("PUBLISHED", pub.GetProperty("status").GetString());
        Assert.Equal(rev1, pub.GetProperty("publicRevisionId").GetGuid());
        etag = published.Headers.ETag!.ToString();

        using (var replay = await PublishAsync(cms, id, rev1, etag))
        {
            await NewsHttp.ProblemAsync(replay, HttpStatusCode.Conflict, ErrorCodes.StateConflict);   // đã PUBLISHED
        }

        using (var publicV1 = await www.GetAsync($"/api/v1/articles/{slug}"))
        {
            var body = await NewsHttp.JsonAsync(publicV1, HttpStatusCode.OK);
            Assert.Equal("Bài sẽ xuất bản", body.GetProperty("title").GetString());
            Assert.Equal(rev1, body.GetProperty("revisionId").GetGuid());
            Assert.Equal(_categorySlug, body.GetProperty("categorySlug").GetString());
            Assert.Equal("Tác giả", body.GetProperty("authorDisplayName").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("sponsorLabel").ValueKind);
            Assert.Equal($"W/\"{rev1:N}\"", publicV1.Headers.ETag!.ToString());
        }

        // Sửa bài đang public → revision mới; bản công khai không đổi (FR-NEWS-005).
        var (edited, editedEtag) = await PatchAsync(cms, id, etag, """{"title":"Tiêu đề mới chưa duyệt","changeReason":"Cập nhật giá"}""");
        var rev2 = edited.GetProperty("workingRevisionId").GetGuid();
        Assert.NotEqual(rev1, rev2);
        Assert.Equal(rev1, edited.GetProperty("publicRevisionId").GetGuid());
        Assert.Equal("PUBLISHED", edited.GetProperty("status").GetString());
        using (var publicStill = await www.GetAsync($"/api/v1/articles/{slug}"))
        {
            Assert.Equal("Bài sẽ xuất bản", (await NewsHttp.JsonAsync(publicStill, HttpStatusCode.OK)).GetProperty("title").GetString());
        }

        // DB cũng chặn mọi đường ghi khác vào public revision (trigger 0320).
        var direct = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync($"UPDATE tt.article_revisions SET title = 'lén' WHERE id = '{rev1}'"));
        Assert.Equal(ErrorCodes.StateConflict, direct.MessageText);

        using (var diff = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles/{id}/revisions/{rev2}?compareTo={rev1}", _author, StaffRoles.Author)))
        {
            var detail = await NewsHttp.JsonAsync(diff, HttpStatusCode.OK);
            Assert.Equal(2, detail.GetProperty("number").GetInt32());
            Assert.Equal(rev1, detail.GetProperty("diff").GetProperty("compareTo").GetGuid());
            var paths = detail.GetProperty("diff").GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("path").GetString()).ToList();
            Assert.Equal(["/title", "/changeReason"], paths);
        }

        // Publish revision 2 → revision 1 SUPERSEDED, công khai đổi sang bản mới.
        editedEtag = await ApproveAsync(id, rev2);
        using (var second = await PublishAsync(cms, id, rev2, editedEtag))
        {
            Assert.Equal(rev2, (await NewsHttp.JsonAsync(second, HttpStatusCode.OK)).GetProperty("publicRevisionId").GetGuid());
        }

        using (var publicV2 = await www.GetAsync($"/api/v1/articles/{slug}"))
        {
            Assert.Equal("Tiêu đề mới chưa duyệt", (await NewsHttp.JsonAsync(publicV2, HttpStatusCode.OK)).GetProperty("title").GetString());
        }

        using var history = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles/{id}/revisions", _author, StaffRoles.Author));
        var statuses = (await NewsHttp.JsonAsync(history, HttpStatusCode.OK)).GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("number").GetInt32(), i.GetProperty("status").GetString())).ToList();
        Assert.Equal([(2, "PUBLISHED"), (1, "SUPERSEDED")], statuses);
    }

    [DbFact]
    public async Task Slug_change_redirects_308_and_withdrawn_content_is_410()
    {
        using var cms = Cms();
        using var www = Www();
        var (article, etag) = await CreateAsync(cms, "Bài đổi slug");
        var id = article.GetProperty("id").GetGuid();
        var oldSlug = article.GetProperty("slug").GetString()!;
        etag = await ApproveAsync(id, article.GetProperty("workingRevisionId").GetGuid());
        using var published = await PublishAsync(cms, id, article.GetProperty("workingRevisionId").GetGuid(), etag);
        etag = published.Headers.ETag!.ToString();

        var newSlug = "bai-moi-" + Guid.NewGuid().ToString("N")[..8];
        var (renamed, _) = await PatchAsync(cms, id, etag, $$"""{"slug":"{{newSlug}}"}""");
        Assert.Equal(newSlug, renamed.GetProperty("slug").GetString());
        Assert.Equal(article.GetProperty("workingRevisionId").GetGuid(), renamed.GetProperty("workingRevisionId").GetGuid());   // chỉ đổi slug: không tạo revision

        using (var redirect = await www.GetAsync($"/api/v1/articles/{oldSlug}"))
        {
            Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
            Assert.Equal($"/api/v1/articles/{newSlug}", redirect.Headers.Location!.OriginalString);
        }

        // Bài khác không được chiếm slug cũ (FR-NEWS-002).
        var (other, otherEtag) = await CreateAsync(cms, "Bài khác");
        using (var steal = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/articles/{other.GetProperty("id").GetGuid()}", _author, StaffRoles.Author,
            json: $$"""{"slug":"{{oldSlug}}"}""", ifMatch: otherEtag)))
        {
            await NewsHttp.ProblemAsync(steal, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        }

        // Quyền hết hạn sau khi đã public → 410 CONTENT_WITHDRAWN (CHECK 0306 không cho bài PUBLISHED mang quyền khác CLEARED).
        await ExecAsync($"UPDATE tt.articles SET rights_expires_at = now() - interval '1 minute' WHERE id = '{id}'");
        using (var revoked = await www.GetAsync($"/api/v1/articles/{newSlug}"))
        {
            await NewsHttp.ProblemAsync(revoked, HttpStatusCode.Gone, ErrorCodes.ContentWithdrawn);
        }

        using (var redirectToWithdrawn = await www.GetAsync($"/api/v1/articles/{oldSlug}"))
        {
            await NewsHttp.ProblemAsync(redirectToWithdrawn, HttpStatusCode.NotFound, ErrorCodes.NotFound);   // không redirect tới bài đã gỡ
        }
    }

    [DbFact]
    public async Task Sponsored_revision_cannot_be_published_while_the_flag_is_off()
    {
        using var cms = Cms();
        var sponsorship = $$""","contentKind":"SPONSORED","sponsorship":{"sponsorName":"ACME","sponsorReference":"HD-01","evidenceRef":"dms://hd-01","validFrom":"{{DateTimeOffset.UtcNow.AddDays(-1):O}}","validTo":"{{DateTimeOffset.UtcNow.AddDays(30):O}}"}""";
        var (article, etag) = await CreateAsync(cms, "Bài tài trợ", extra: sponsorship);
        Assert.Equal("SPONSORED", article.GetProperty("contentKind").GetString());
        Assert.Equal("ACME", article.GetProperty("sponsorship").GetProperty("sponsorName").GetString());
        var id = article.GetProperty("id").GetGuid();
        var rev = article.GetProperty("workingRevisionId").GetGuid();
        etag = await ApproveAsync(id, rev, sponsorshipApproved: true);

        // Cờ ads.sponsored_publication OFF (mặc định production) → trigger 0307 chặn trong transaction publish.
        using var response = await PublishAsync(cms, id, rev, etag);
        await NewsHttp.ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.FeatureDisabled);
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.articles WHERE id = '{id}' AND public_revision_id IS NULL"));

        // Chuyển về EDITORIAL bằng PATCH (revision đã APPROVED → tạo revision mới, sponsorship bị bỏ).
        var (editorial, _) = await PatchAsync(cms, id, etag, """{"contentKind":"EDITORIAL"}""");
        Assert.NotEqual(rev, editorial.GetProperty("workingRevisionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, editorial.GetProperty("sponsorship").ValueKind);
    }

    [DbFact]
    public async Task Article_in_review_cannot_be_patched()
    {
        using var cms = Cms();
        var (article, _) = await CreateAsync(cms, "Đang duyệt");
        var id = article.GetProperty("id").GetGuid();
        await ExecAsync($"UPDATE tt.articles SET status = 'IN_REVIEW' WHERE id = '{id}'");
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/articles/{id}", _author, StaffRoles.Author, json: """{"title":"x"}""", ifMatch: await ETagAsync(id)));
        await NewsHttp.ProblemAsync(response, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
    }

    [DbFact]
    public async Task Admin_list_filters_and_paginates_with_signed_cursor()
    {
        using var cms = Cms();
        var owner = Guid.NewGuid();
        await ExecAsync($"INSERT INTO tt.users (id, display_name) VALUES ('{owner}', 'Người lọc')");
        var marker = "loc" + Guid.NewGuid().ToString("N")[..6];
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++) created.Add((await CreateAsync(cms, $"{marker} bài {i}", owner)).Article.GetProperty("id").GetGuid());

        var seen = new List<Guid>();
        string? cursor = null;
        var query = $"ownerId={owner}&status=DRAFT,PUBLISHED&q={marker}&limit=2";
        do
        {
            using var page = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles?{query}{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}", _author, StaffRoles.Author));
            var body = await NewsHttp.JsonAsync(page, HttpStatusCode.OK);
            seen.AddRange(body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
            cursor = body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : body.GetProperty("nextCursor").GetString();
            if (cursor is not null)
            {
                // Cursor gắn với bộ lọc: dùng cho bộ lọc khác → 400 INVALID_CURSOR.
                using var foreign = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles?sort=title_asc&cursor={Uri.EscapeDataString(cursor)}", _author, StaffRoles.Author));
                await NewsHttp.ProblemAsync(foreign, HttpStatusCode.BadRequest, ErrorCodes.InvalidCursor);
            }
        }
        while (cursor is not null);

        Assert.Equal(created.AsEnumerable().Reverse(), seen);   // updatedAt_desc

        using var byTitle = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles?ownerId={owner}&sort=title_asc&limit=100", _author, StaffRoles.Author));
        var titles = (await NewsHttp.JsonAsync(byTitle, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()).ToList();
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"{marker} bài {i}"), titles);

        using var none = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles?ownerId={owner}&status=PUBLISHED", _author, StaffRoles.Author));
        Assert.Equal(0, (await NewsHttp.JsonAsync(none, HttpStatusCode.OK)).GetProperty("items").GetArrayLength());
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>Trang chủ (FR-DISC-003, WBS-027/051) — phần không cần DB: bề mặt API, quyền, siteKey.</summary>
public sealed class HomeUnitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Featured = "/api/v1/admin/home/www/featured-articles";

    [Fact]
    public async Task Admin_home_api_needs_a_staff_session_and_is_only_served_on_cms()
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        using (var anonymous = await cms.GetAsync(Featured))
        {
            await NewsHttp.ProblemAsync(anonymous, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        }

        using var www = factory.WithProbes().Client(TestHost.Www);
        using var onPublicSite = await www.SendAsync(NewsHttp.Admin(HttpMethod.Get, Featured, Guid.NewGuid(), StaffRoles.Editor));
        Assert.Equal(HttpStatusCode.NotFound, onPublicSite.StatusCode);
        using var homeOnCms = await cms.GetAsync("/api/v1/home");
        Assert.Equal(HttpStatusCode.NotFound, homeOnCms.StatusCode);
    }

    [Theory]
    [InlineData(StaffRoles.Editor, true)]      // không có content.publish
    [InlineData(StaffRoles.Approver, false)]   // content.publish cần MFA
    [InlineData(StaffRoles.Admin, true)]       // quản trị tài khoản không đụng nội dung
    public async Task Replacing_featured_articles_requires_content_publish_with_mfa(string roles, bool mfa)
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Put, Featured, Guid.NewGuid(), roles, mfa, """{"articleIds":[]}""", "W/\"1\""));
        await NewsHttp.ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("cms")]      // không phải site public-web
    [InlineData("media")]
    [InlineData("game")]     // không có trong Site Registry
    public async Task Unknown_or_non_public_site_is_404_before_if_match(string siteKey)
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        var path = $"/api/v1/admin/home/{siteKey}/featured-articles";
        using (var get = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, path, Guid.NewGuid(), StaffRoles.Author)))
        {
            await NewsHttp.ProblemAsync(get, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        using var put = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Put, path, Guid.NewGuid(), StaffRoles.Approver, true, """{"articleIds":[]}"""));
        await NewsHttp.ProblemAsync(put, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Without_database_home_and_categories_return_503()
    {
        using var www = factory.WithProbes().Client(TestHost.Www);
        foreach (var path in new[] { "/api/v1/home", "/api/v1/categories" })
        {
            using var response = await www.GetAsync(path);
            await NewsHttp.ProblemAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
        }
    }
}

/// <summary>Trang chủ trên PostgreSQL thật: thứ tự biên tập, bù bài mới, ẩn bài đã gỡ, If-Match, lịch sử, chuyên mục.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class HomeDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Featured = "/api/v1/admin/home/www/featured-articles";
    private readonly Guid _author = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly Guid _category = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) return;
        await ExecAsync($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_author}', 'Tác giả'), ('{_approver}', 'Người duyệt');
            INSERT INTO tt.categories (id, slug, name) VALUES ('{_category}', 'home-{_category:N}', 'Trang chủ');
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private HttpClient Cms() => factory.WithProbes(db.ConnectionString).Client(TestHost.Cms);

    private HttpClient Www() => factory.WithProbes(db.ConnectionString).Client(TestHost.Www);

    private async Task<Guid> DraftAsync(HttpClient cms, string title)
    {
        var json = $$"""{"title":"{{title}}","summary":"Sapo","categoryId":"{{_category}}"}""";
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", _author, StaffRoles.Author, json: json,
            idempotencyKey: "create-" + Guid.NewGuid()));
        return (await NewsHttp.JsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    /// <summary>Tạo + duyệt giả (workflow WBS-023/024 chưa có) + publish qua API.</summary>
    private async Task<Guid> PublishedAsync(HttpClient cms, string title)
    {
        var id = await DraftAsync(cms, title);
        await ExecAsync($"""
            UPDATE tt.articles SET rights_status = 'CLEARED' WHERE id = '{id}';
            UPDATE tt.article_revisions SET status = 'IN_REVIEW' WHERE article_id = '{id}';
            UPDATE tt.article_revisions SET status = 'APPROVED' WHERE article_id = '{id}';
            """);
        var revision = await db.ScalarAsync<Guid>($"SELECT working_revision_id FROM tt.articles WHERE id = '{id}'");
        var etag = $"W/\"{await db.ScalarAsync<long>($"SELECT row_version FROM tt.articles WHERE id = '{id}'")}\"";
        using var publish = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, $"/api/v1/admin/articles/{id}/publish", _approver, StaffRoles.Approver, true,
            $$"""{"revisionId":"{{revision}}"}""", etag, "publish-" + Guid.NewGuid()));
        await NewsHttp.JsonAsync(publish, HttpStatusCode.OK);
        return id;
    }

    private async Task<(JsonElement Body, string ETag)> GetFeaturedAsync(HttpClient cms)
    {
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, Featured, _author, StaffRoles.Author));
        return (await NewsHttp.JsonAsync(response, HttpStatusCode.OK), response.Headers.ETag!.ToString());
    }

    private Task<HttpResponseMessage> PutFeaturedAsync(HttpClient cms, string? ifMatch, params Guid[] ids) =>
        cms.SendAsync(NewsHttp.Admin(HttpMethod.Put, Featured, _approver, StaffRoles.Approver, true,
            $$"""{"articleIds":[{{string.Join(",", ids.Select(i => $"\"{i}\""))}}]}""", ifMatch));

    private static async Task<(Guid[] Featured, Guid[] Recent)> HomeAsync(HttpClient www)
    {
        using var response = await www.GetAsync("/api/v1/home");
        var body = await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(0, body.GetProperty("featuredVideos").GetArrayLength());
        static Guid[] Ids(JsonElement list) => [.. list.EnumerateArray().Select(a => a.GetProperty("id").GetGuid())];
        foreach (var item in body.GetProperty("featuredArticles").EnumerateArray().Concat(body.GetProperty("recentArticles").EnumerateArray()))
        {
            Assert.Equal("EDITORIAL", item.GetProperty("contentKind").GetString());
        }

        return (Ids(body.GetProperty("featuredArticles")), Ids(body.GetProperty("recentArticles")));
    }

    [DbFact]
    public async Task Home_shows_editorial_order_then_latest_and_drops_withdrawn_articles()
    {
        using var cms = Cms();
        using var www = Www();
        var a = await PublishedAsync(cms, "Bài A");
        var b = await PublishedAsync(cms, "Bài B");
        var draft = await DraftAsync(cms, "Bản nháp chưa công khai");
        var c = await PublishedAsync(cms, "Bài C mới nhất");

        var (_, etag) = await GetFeaturedAsync(cms);

        using (var missing = await PutFeaturedAsync(cms, null, b, a))
        {
            await NewsHttp.ProblemAsync(missing, (HttpStatusCode)428, ErrorCodes.PreconditionRequired);
        }

        using (var stale = await PutFeaturedAsync(cms, "W/\"999999\"", b, a))
        {
            await NewsHttp.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
        }

        using (var notPublic = await PutFeaturedAsync(cms, etag, b, draft))
        {
            var problem = await NewsHttp.ProblemAsync(notPublic, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
            Assert.True(problem.GetProperty("errors").TryGetProperty("articleIds[1]", out _));
        }

        using (var duplicate = await PutFeaturedAsync(cms, etag, b, b))
        {
            await NewsHttp.ProblemAsync(duplicate, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        }

        // Thay danh sách: B rồi A. ETag tăng, lịch sử ghi người thao tác + ảnh chụp.
        using (var saved = await PutFeaturedAsync(cms, etag, b, a))
        {
            var body = await NewsHttp.JsonAsync(saved, HttpStatusCode.OK);
            Assert.Equal([b, a], body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("articleId").GetGuid()));
            Assert.Equal([1, 2], body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("position").GetInt32()));
            Assert.All(body.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("isPublic").GetBoolean()));
            Assert.Equal(_approver, body.GetProperty("updatedBy").GetGuid());
            Assert.NotEqual(etag, saved.Headers.ETag!.ToString());
            Assert.Equal($"W/\"{body.GetProperty("rowVersion").GetInt64()}\"", saved.Headers.ETag!.ToString());
            etag = saved.Headers.ETag!.ToString();
        }

        Assert.Equal(1L, await db.ScalarAsync<long>($"""
            SELECT count(*) FROM tt.home_featured_history
             WHERE site_key = 'www' AND actor_id = '{_approver}' AND article_ids = ARRAY['{b}', '{a}']::uuid[]
            """));

        // Trang chủ: ghim theo thứ tự, rồi bù bằng bài mới nhất (C); Tin mới không lặp Tin nổi bật; bản nháp không xuất hiện.
        var (featured, recent) = await HomeAsync(www);
        Assert.Equal([b, a, c], featured.Take(3));
        Assert.True(featured.Length <= 5 && recent.Length <= 12);
        Assert.Empty(featured.Intersect(recent));
        Assert.DoesNotContain(draft, featured.Concat(recent));

        // Bài ghim bị gỡ (FR-DISC-004): biến khỏi trang chủ, CMS vẫn thấy với isPublic = false.
        await ExecAsync($"UPDATE tt.articles SET status = 'UNPUBLISHED' WHERE id = '{b}'");
        (featured, recent) = await HomeAsync(www);
        Assert.DoesNotContain(b, featured.Concat(recent));
        Assert.Equal(a, featured[0]);
        var (admin, _) = await GetFeaturedAsync(cms);
        Assert.False(admin.GetProperty("items")[0].GetProperty("isPublic").GetBoolean());

        // Xóa hết ghim → tin nổi bật là bài mới nhất.
        using (var cleared = await PutFeaturedAsync(cms, etag))
        {
            Assert.Equal(0, (await NewsHttp.JsonAsync(cleared, HttpStatusCode.OK)).GetProperty("items").GetArrayLength());
        }

        (featured, _) = await HomeAsync(www);
        Assert.Equal(c, featured[0]);
    }

    private static async Task<(Guid[] Ids, string? Next)> PageAsync(HttpClient www, string path)
    {
        using var response = await www.GetAsync(path);
        var body = await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
        var next = body.GetProperty("nextCursor");
        return ([.. body.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetGuid())],
            next.ValueKind == JsonValueKind.Null ? null : next.GetString());
    }

    [DbFact]
    public async Task Category_and_latest_lists_page_newest_first_and_hide_unpublished()
    {
        using var cms = Cms();
        using var www = Www();
        var oldest = await PublishedAsync(cms, "Cũ nhất");
        var middle = await PublishedAsync(cms, "Ở giữa");
        var draft = await DraftAsync(cms, "Nháp trong chuyên mục");
        var newest = await PublishedAsync(cms, "Mới nhất");
        var withdrawn = await PublishedAsync(cms, "Sẽ bị gỡ");
        await ExecAsync($"UPDATE tt.articles SET status = 'UNPUBLISHED' WHERE id = '{withdrawn}'");
        var slug = $"home-{_category:N}";

        // Chuyên mục: đi hết các trang, mỗi bài đúng một lần, mới nhất trước; nháp/đã gỡ không có.
        var (page1, cursor) = await PageAsync(www, $"/api/v1/categories/{slug}/articles?limit=2");
        Assert.Equal([newest, middle], page1);
        Assert.NotNull(cursor);
        var (page2, end) = await PageAsync(www, $"/api/v1/categories/{slug}/articles?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal([oldest], page2);
        Assert.Null(end);
        Assert.DoesNotContain(draft, page1.Concat(page2));
        Assert.DoesNotContain(withdrawn, page1.Concat(page2));

        // Cursor gắn với danh sách: dùng cursor của chuyên mục cho /articles → 400 INVALID_CURSOR.
        using (var foreign = await www.GetAsync($"/api/v1/articles?cursor={Uri.EscapeDataString(cursor!)}"))
        {
            await NewsHttp.ProblemAsync(foreign, HttpStatusCode.BadRequest, ErrorCodes.InvalidCursor);
        }

        // /articles: bài mới nhất của site lên đầu; lọc category cho cùng kết quả; category lạ → rỗng (không 404).
        var (latest, _) = await PageAsync(www, "/api/v1/articles?limit=3");
        Assert.Equal(newest, latest[0]);
        var (filtered, _) = await PageAsync(www, $"/api/v1/articles?category={slug}");
        Assert.Equal([newest, middle, oldest], filtered);
        var (none, noneNext) = await PageAsync(www, "/api/v1/articles?category=khong-co-chuyen-muc-nay");
        Assert.Empty(none);
        Assert.Null(noneNext);

        // Chuyên mục không có hoặc đã tắt → 404.
        using (var missing = await www.GetAsync("/api/v1/categories/khong-co-chuyen-muc-nay/articles"))
        {
            await NewsHttp.ProblemAsync(missing, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }

        await ExecAsync($"UPDATE tt.categories SET is_enabled = false WHERE id = '{_category}'");
        using (var disabled = await www.GetAsync($"/api/v1/categories/{slug}/articles"))
        {
            await NewsHttp.ProblemAsync(disabled, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }
    }

    [DbFact]
    public async Task Public_categories_list_only_enabled_categories_of_the_site()
    {
        var disabled = Guid.NewGuid();
        await ExecAsync($"INSERT INTO tt.categories (id, slug, name, is_enabled) VALUES ('{disabled}', 'tat-{disabled:N}', 'Đã tắt', false)");
        using var www = Www();
        using var response = await www.GetAsync("/api/v1/categories");
        var items = (await NewsHttp.JsonAsync(response, HttpStatusCode.OK)).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, c => c.GetProperty("id").GetGuid() == _category && c.GetProperty("name").GetString() == "Trang chủ");
        Assert.DoesNotContain(items, c => c.GetProperty("id").GetGuid() == disabled);
        Assert.All(items, c => Assert.True(c.GetProperty("isEnabled").GetBoolean()));
    }
}

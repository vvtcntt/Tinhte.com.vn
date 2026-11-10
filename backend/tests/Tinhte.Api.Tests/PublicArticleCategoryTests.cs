using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>
/// getPublicArticle trả <c>category</c> (Article kế thừa ArticleSummary.category — DRAFT v0.3 mục 17), vẫn giữ <c>categorySlug</c>.
/// File riêng (không sửa NewsArticleTests/HomeTests) để dễ gộp với nhánh đang sửa News module.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PublicArticleCategoryTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly Guid _author = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Tạo + duyệt giả + publish qua API trong chuyên mục <paramref name="category"/>; trả slug công khai.</summary>
    private async Task<string> PublishedAsync(HttpClient cms, string title, Guid category)
    {
        var json = $$"""{"title":"{{title}}","summary":"Sapo","categoryId":"{{category}}"}""";
        Guid id;
        string slug;
        using (var created = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", _author, StaffRoles.Author, json: json,
            idempotencyKey: "create-" + Guid.NewGuid())))
        {
            var body = await NewsHttp.JsonAsync(created, HttpStatusCode.Created);
            id = body.GetProperty("id").GetGuid();
            slug = body.GetProperty("slug").GetString()!;
        }

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
        return slug;
    }

    private static async Task<JsonElement> ArticleAsync(HttpClient www, string slug)
    {
        using var response = await www.GetAsync($"/api/v1/articles/{slug}");
        return await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
    }

    [DbFact]
    public async Task Public_article_carries_the_enabled_category_and_keeps_category_slug()
    {
        Guid laptops = Guid.NewGuid(), retiring = Guid.NewGuid();
        await ExecAsync($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_author}', 'Tác giả'), ('{_approver}', 'Người duyệt');
            INSERT INTO tt.categories (id, slug, name) VALUES ('{laptops}', 'lt-{laptops:N}', 'Laptop'), ('{retiring}', 'rt-{retiring:N}', 'Sắp tắt');
            """);
        using var cms = factory.WithProbes(db.ConnectionString).Client(TestHost.Cms);
        var shownSlug = await PublishedAsync(cms, $"Bài laptop {laptops:N}", laptops);
        var hiddenSlug = await PublishedAsync(cms, $"Bài chuyên mục sắp tắt {retiring:N}", retiring);
        await ExecAsync($"UPDATE tt.categories SET is_enabled = false WHERE id = '{retiring}'");

        using var www = factory.WithProbes(db.ConnectionString).Client(TestHost.Www);
        var shown = await ArticleAsync(www, shownSlug);
        var category = shown.GetProperty("category");
        Assert.Equal(laptops, category.GetProperty("id").GetGuid());
        Assert.Equal($"lt-{laptops:N}", category.GetProperty("slug").GetString());
        Assert.Equal("Laptop", category.GetProperty("name").GetString());
        Assert.Equal($"lt-{laptops:N}", shown.GetProperty("categorySlug").GetString());   // giữ cho client cũ

        // Chuyên mục bị tắt → category null (không hiện), bài vẫn đọc được.
        var hidden = await ArticleAsync(www, hiddenSlug);
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("category").ValueKind);
    }
}

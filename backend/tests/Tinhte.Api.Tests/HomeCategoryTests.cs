using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>
/// ArticleSummary.category trên trang chủ (DRAFT v0.3, đợt 3 — UX-PUB-01): chuyên mục theo bản đã xuất bản, null khi chuyên mục bị tắt.
/// File riêng (không sửa HomeTests.cs) để dễ gộp với nhánh đang sửa trang danh sách.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HomeCategoryTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly Guid _author = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Tạo + duyệt giả (như HomeDbTests) + publish qua API, trong chuyên mục <paramref name="category"/>.</summary>
    private async Task<Guid> PublishedAsync(HttpClient cms, string title, Guid category)
    {
        var json = $$"""{"title":"{{title}}","summary":"Sapo","categoryId":"{{category}}"}""";
        Guid id;
        using (var created = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", _author, StaffRoles.Author, json: json,
            idempotencyKey: "create-" + Guid.NewGuid())))
        {
            id = (await NewsHttp.JsonAsync(created, HttpStatusCode.Created)).GetProperty("id").GetGuid();
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
        return id;
    }

    [DbFact]
    public async Task Home_cards_carry_the_enabled_category_of_the_published_article()
    {
        Guid phones = Guid.NewGuid(), hidden = Guid.NewGuid();
        await ExecAsync($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_author}', 'Tác giả'), ('{_approver}', 'Người duyệt');
            INSERT INTO tt.categories (id, slug, name) VALUES ('{phones}', 'dt-{phones:N}', 'Điện thoại'), ('{hidden}', 'an-{hidden:N}', 'Sắp tắt');
            """);
        using var cms = factory.WithProbes(db.ConnectionString).Client(TestHost.Cms);
        var shown = await PublishedAsync(cms, "Bài có chuyên mục", phones);
        var orphan = await PublishedAsync(cms, "Bài thuộc chuyên mục bị tắt", hidden);
        await ExecAsync($"UPDATE tt.categories SET is_enabled = false WHERE id = '{hidden}'");

        using var www = factory.WithProbes(db.ConnectionString).Client(TestHost.Www);
        using var response = await www.GetAsync("/api/v1/home");
        var body = await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
        var cards = body.GetProperty("featuredArticles").EnumerateArray().Concat(body.GetProperty("recentArticles").EnumerateArray())
            .ToDictionary(a => a.GetProperty("id").GetGuid());

        var category = cards[shown].GetProperty("category");
        Assert.Equal(phones, category.GetProperty("id").GetGuid());
        Assert.Equal($"dt-{phones:N}", category.GetProperty("slug").GetString());
        Assert.Equal("Điện thoại", category.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, cards[orphan].GetProperty("category").ValueKind);
    }
}

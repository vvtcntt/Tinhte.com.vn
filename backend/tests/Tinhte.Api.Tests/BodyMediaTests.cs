using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Ai.Application;
using Tinhte.Modules.Ai.Infrastructure;
using Tinhte.Modules.Video.Infrastructure;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>getAdminMediaContent — phần không cần DB: quyền, chưa cấu hình kho.</summary>
public sealed class MediaContentUnitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Media_content_needs_content_read_and_configured_storage()
    {
        var path = $"/api/v1/admin/media/{Guid.NewGuid()}/content";
        using var app = factory.WithProbes().WithWebHostBuilder(b => b.UseSetting("Media:StorageProvider", "None"));
        using var cms = app.Client(TestHost.Cms);
        using (var anonymous = await cms.GetAsync(path))
        {
            await NewsHttp.ProblemAsync(anonymous, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        }

        await NewsHttp.ProblemAsync(await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, path, Guid.NewGuid(), StaffRoles.Moderator)), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await NewsHttp.ProblemAsync(await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, path, Guid.NewGuid(), StaffRoles.Author)),
            HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
        await NewsHttp.ProblemAsync(await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/media/not-a-uuid/content", Guid.NewGuid(), StaffRoles.Author)),
            HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
    }
}

/// <summary>Kho giả kiểu object storage: đọc = 302 tới URL ký sẵn (không có pipeline xử lý — dòng media đặt tay trong DB).</summary>
internal sealed class RedirectingMediaStorage : IMediaStorage
{
    public bool IsConfigured => true;

    public Uri CreateUploadUrl(MediaUploadTarget target, Uri requestOrigin) => throw new NotSupportedException();

    public Task<StoredObject?> HeadAsync(string storageKey, CancellationToken ct) => Task.FromResult<StoredObject?>(null);

    public Task<byte[]?> ReadHeaderAsync(string storageKey, int length, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    public Task DeleteAsync(string storageKey, CancellationToken ct) => Task.CompletedTask;

    public Task<MediaContent?> OpenReadAsync(string storageKey, CancellationToken ct) =>
        Task.FromResult<MediaContent?>(MediaContent.Redirect(new Uri($"https://objects.example/{storageKey}?X-Signature=abc"), DateTimeOffset.UtcNow.AddMinutes(5)));
}

/// <summary>
/// Trên PostgreSQL thật: xem ảnh trong CMS (getAdminMediaContent), FR-CMS-009 cho khối image của thân bài/video, thân video
/// (UpdateVideoRequest.bodyBlocks / AdminVideo.bodyBlocks, migration 0355) và chữ thân video trong nội dung gửi AI.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BodyMediaDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private readonly Guid _author = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _category = Guid.NewGuid();
    private readonly string _root = Directory.CreateTempSubdirectory("tt-media-body-").FullName;
    private WebApplicationFactory<Program>? _app;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) return;
        await ExecAsync($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_author}', 'Tác giả'), ('{_admin}', 'Quản trị');
            INSERT INTO tt.categories (id, slug, name) VALUES ('{_category}', 'body-{_category:N}', 'Điện thoại');
            """);
        _app = factory.WithProbes(db.ConnectionString).WithWebHostBuilder(b => b.UseSetting("Media:LocalRoot", _root));
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private async Task ExecAsync(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Dòng media như sau finalize + pipeline (đặt tay), kèm tệp trong kho Local khi có <paramref name="bytes"/>.</summary>
    private async Task<Guid> MediaAsync(string type = "IMAGE", string status = "READY", string rights = "UNVERIFIED", string contentType = "image/png", byte[]? bytes = null)
    {
        var id = Guid.NewGuid();
        var key = $"media/2026/11/{id}";
        var content = bytes ?? Png;
        await ExecAsync($"""
            INSERT INTO tt.media_assets (id, status, media_type, file_name, content_type, size_bytes, checksum_sha256, storage_key, uploaded_at, rights_status, created_by)
            VALUES ('{id}', '{status}', '{type}', 'anh.png', '{contentType}', {content.Length}, '{MediaSamples.Sha256(content)}', '{key}', now(), '{rights}', '{_author}')
            """);
        if (bytes is not null)
        {
            var path = Path.Combine(_root, "media", "2026", "11", id.ToString());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }

        return id;
    }

    private static string ImageBody(Guid mediaId) => $$"""[{"type":"paragraph","text":"Ảnh"},{"type":"image","mediaId":"{{mediaId}}","alt":"Mặt lưng"}]""";

    private HttpRequestMessage Get(string path) => NewsHttp.Admin(HttpMethod.Get, path, _author, StaffRoles.Author);

    // ---------------------------------------------------------------- getAdminMediaContent

    [DbFact]
    public async Task Ready_images_are_streamed_by_local_storage_and_everything_else_is_404()
    {
        using var cms = _app!.Client(TestHost.Cms);
        var image = await MediaAsync(bytes: Png);
        using (var ok = await cms.SendAsync(Get($"/api/v1/admin/media/{image}/content")))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(Png, await ok.Content.ReadAsByteArrayAsync());
            Assert.Equal("image/png", ok.Content.Headers.ContentType!.MediaType);
            Assert.Equal("nosniff", ok.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("inline", ok.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Contains("no-store", ok.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        }

        // Quyền bị thu hồi vẫn xem được trong CMS (biên tập viên thấy ảnh nào phải thay).
        var revoked = await MediaAsync(rights: "REVOKED", bytes: Png);
        using (var ok = await cms.SendAsync(Get($"/api/v1/admin/media/{revoked}/content"))) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        foreach (var id in new[]
                 {
                     Guid.NewGuid(),
                     await MediaAsync(type: "VIDEO", contentType: "video/mp4", bytes: MediaSamples.Mp4(64)),
                     await MediaAsync(status: "PROCESSING", bytes: Png),
                     await MediaAsync(status: "REJECTED", bytes: Png),
                     await MediaAsync(),   // READY nhưng tệp không còn trong kho
                 })
        {
            await NewsHttp.ProblemAsync(await cms.SendAsync(Get($"/api/v1/admin/media/{id}/content")), HttpStatusCode.NotFound, ErrorCodes.NotFound);
        }
    }

    [DbFact]
    public async Task Object_storage_answers_with_a_short_lived_redirect()
    {
        using var app = factory.WithProbes(db.ConnectionString).WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddSingleton<IMediaStorage, RedirectingMediaStorage>()));
        using var cms = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = TestHost.Cms, AllowAutoRedirect = false });
        var image = await MediaAsync();
        using var response = await cms.SendAsync(Get($"/api/v1/admin/media/{image}/content"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"https://objects.example/media/2026/11/{image}?X-Signature=abc", response.Headers.Location!.AbsoluteUri);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- FR-CMS-009: khối image của bài

    [DbFact]
    public async Task Article_bodies_only_accept_usable_image_media()
    {
        using var cms = _app!.Client(TestHost.Cms);
        async Task<HttpResponseMessage> CreateAsync(string body) => await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", _author, StaffRoles.Author,
            json: $$"""{"title":"Bài có ảnh {{Guid.NewGuid():N}}","categoryId":"{{_category}}","initialBodyBlocks":{{body}}}""", idempotencyKey: "article-" + Guid.NewGuid()));

        foreach (var (media, why) in new[]
                 {
                     (Guid.NewGuid(), "Unknown media."),
                     (await MediaAsync(type: "VIDEO", contentType: "video/mp4"), "Only an IMAGE"),
                     (await MediaAsync(status: "QUARANTINED"), "QUARANTINED"),
                     (await MediaAsync(rights: "REVOKED"), "REVOKED"),
                     (await MediaAsync(rights: "RESTRICTED"), "RESTRICTED"),
                     (await MediaAsync(rights: "EXPIRED"), "EXPIRED"),
                 })
        {
            var problem = await NewsHttp.ProblemAsync(await CreateAsync(ImageBody(media)), HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
            var message = Assert.Single(problem.GetProperty("errors").GetProperty("initialBodyBlocks[1].mediaId").EnumerateArray()).GetString();
            Assert.Contains(why, message, StringComparison.Ordinal);
        }

        // Quyền hết hạn theo thời gian cũng bị chặn.
        var lapsed = await MediaAsync(rights: "CLEARED");
        await ExecAsync($"UPDATE tt.media_assets SET rights_expires_at = now() - interval '1 day' WHERE id = '{lapsed}'");
        await NewsHttp.ProblemAsync(await CreateAsync(ImageBody(lapsed)), HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);

        // Cấu trúc sai bị chặn trước khi chạm DB, kèm đường dẫn chính xác.
        var invalid = await NewsHttp.ProblemAsync(await CreateAsync("""[{"type":"paragraph","text":"a","runs":[{"text":"a","href":"javascript:alert(1)"}]}]"""),
            HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        Assert.True(invalid.GetProperty("errors").TryGetProperty("initialBodyBlocks[0].runs[0].href", out _));

        // Ảnh IMAGE dùng được (UNVERIFIED/CLEARED) → tạo được; PATCH thân cũng kiểm.
        using var created = await CreateAsync(ImageBody(await MediaAsync()));
        var article = await NewsHttp.JsonAsync(created, HttpStatusCode.Created);
        var etag = created.Headers.ETag!.ToString();
        var revoked = await MediaAsync(rights: "REVOKED");
        using var patch = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/articles/{article.GetProperty("id").GetGuid()}", _author, StaffRoles.Author,
            json: $$"""{"bodyBlocks":{{ImageBody(revoked)}}}""", ifMatch: etag));
        var patchProblem = await NewsHttp.ProblemAsync(patch, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        Assert.True(patchProblem.GetProperty("errors").TryGetProperty("bodyBlocks[1].mediaId", out _));
    }

    // ---------------------------------------------------------------- thân video (0355)

    [DbFact]
    public async Task Video_body_is_saved_on_the_working_revision_validated_and_sent_to_ai()
    {
        using var cms = _app!.Client(TestHost.Cms);
        using var created = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/videos", _author, StaffRoles.Author,
            json: $$"""{"title":"Trên tay {{Guid.NewGuid():N}}","summary":"Sapo video"}""", idempotencyKey: "video-" + Guid.NewGuid()));
        var video = await NewsHttp.JsonAsync(created, HttpStatusCode.Created);
        var videoId = video.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Array, video.GetProperty("bodyBlocks").ValueKind);
        Assert.Equal(0, video.GetProperty("bodyBlocks").GetArrayLength());
        var etag = created.Headers.ETag!.ToString();

        async Task<HttpResponseMessage> PatchAsync(string json) =>
            await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/videos/{videoId}", _author, StaffRoles.Author, json: json, ifMatch: etag));

        // Sai cấu trúc / ảnh không dùng được → 422 với đường dẫn chính xác, không đổi gì.
        var invalid = await NewsHttp.ProblemAsync(await PatchAsync("""{"bodyBlocks":[{"type":"heading","level":4,"text":"x"}]}"""),
            HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        Assert.True(invalid.GetProperty("errors").TryGetProperty("bodyBlocks[0].level", out _));
        var badImage = await NewsHttp.ProblemAsync(await PatchAsync($$"""{"bodyBlocks":{{ImageBody(await MediaAsync(rights: "EXPIRED"))}}}"""),
            HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        Assert.True(badImage.GetProperty("errors").TryGetProperty("bodyBlocks[1].mediaId", out _));

        // Hợp lệ → lưu vào working revision (DRAFT sửa tại chỗ), GET trả lại đúng thân; danh sách không kèm thân.
        var image = await MediaAsync();
        var body = $$"""
            [{"type":"paragraph","text":"Máy mỏng, pin 5000 mAh","runs":[{"text":"Máy mỏng","marks":["bold"]},{"text":", pin 5000 mAh"}]},
             {"type":"image","mediaId":"{{image}}","alt":"Mặt trước"}]
            """;
        using (var ok = await PatchAsync($$"""{"bodyBlocks":{{body}}}"""))
        {
            var patched = await NewsHttp.JsonAsync(ok, HttpStatusCode.OK);
            Assert.Equal("Máy mỏng, pin 5000 mAh", patched.GetProperty("bodyBlocks")[0].GetProperty("text").GetString());
            etag = ok.Headers.ETag!.ToString();
        }

        var fetched = await NewsHttp.JsonAsync(await cms.SendAsync(Get($"/api/v1/admin/videos/{videoId}")), HttpStatusCode.OK);
        Assert.Equal(image, fetched.GetProperty("bodyBlocks")[1].GetProperty("mediaId").GetGuid());
        Assert.Equal("Sapo video", fetched.GetProperty("summary").GetString());
        Assert.Equal(1, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.video_revisions WHERE video_id = '{videoId}'"));
        Assert.Equal("Máy mỏng", await db.ScalarAsync<string>($"SELECT body->0->'runs'->0->>'text' FROM tt.video_revisions WHERE video_id = '{videoId}'"));

        var list = await NewsHttp.JsonAsync(await cms.SendAsync(Get("/api/v1/admin/videos?limit=100")), HttpStatusCode.OK);
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == videoId);
        Assert.False(item.TryGetProperty("bodyBlocks", out _));

        // Đổi tiêu đề không đụng tới thân (ảnh không bị kiểm lại).
        using (var titled = await PatchAsync("""{"title":"Tiêu đề mới"}"""))
        {
            Assert.Equal(2, (await NewsHttp.JsonAsync(titled, HttpStatusCode.OK)).GetProperty("bodyBlocks").GetArrayLength());
        }

        // Trigger đóng băng thân (0355) khi revision rời DRAFT/đang public: kiểm trong doc/07_AI_REVIEW/v0.3/db/tests (VB5, VB6).

        // Gợi ý AI cho video đọc cả thân video (trước 0355 chỉ có tiêu đề + tóm tắt).
        var ai = new FakeAiClient { Answer = _ => new AiChatResult("""{"suggestions":["Pin 5000 mAh"]}""", 1, 1, "stop") };
        using var aiApp = _app!.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAiChatClient>(ai)));
        using var aiCms = aiApp.Client(TestHost.Cms);
        await ExecAsync($"UPDATE tt.feature_gates SET enabled = true, approval_reference = 'TEST-AI', approved_by = '{_admin}', approved_at = now() WHERE key = 'ai.assistance'");
        try
        {
            await NewsHttp.JsonAsync(await aiCms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/ai/metadata-suggestions", _author, StaffRoles.Author,
                json: $$"""{"contentId":"{{videoId}}","task":"TAG"}""", idempotencyKey: "ai-video-" + Guid.NewGuid())), HttpStatusCode.Created);
            var prompt = Assert.Single(ai.Prompts);
            Assert.Contains("Nội dung: Máy mỏng, pin 5000 mAh", prompt.User, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(prompt.User, "Máy mỏng"));   // runs không bị gửi lặp
        }
        finally
        {
            await ExecAsync("UPDATE tt.feature_gates SET enabled = false WHERE key = 'ai.assistance'");
        }
    }
}

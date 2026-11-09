using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Video;
using Tinhte.Modules.Video.Application;
using Tinhte.Modules.Video.Domain;
using Tinhte.Modules.Video.Infrastructure;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>Tệp mẫu + request tải lên cho test media (WBS-046).</summary>
internal static class MediaSamples
{
    /// <summary>Đầu tệp MP4 hợp lệ (box ftyp) + dữ liệu đệm.</summary>
    public static byte[] Mp4(int size = 4096)
    {
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        "\0\0\0\u0018ftypisom"u8.ToArray().CopyTo(bytes, 0);
        return bytes;
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string IntentJson(string fileName, string contentType, byte[] bytes, string? checksum = null) =>
        JsonSerializer.Serialize(new { fileName, contentType, sizeBytes = bytes.Length, checksumSha256 = checksum ?? Sha256(bytes) });

    /// <summary>PUT nội dung lên uploadUrl (kho Local trả về URL cùng origin cms) với phiên staff giả + CSRF.</summary>
    public static HttpRequestMessage Put(string uploadUrl, byte[] bytes, Guid user, string roles = StaffRoles.Author)
    {
        var url = new Uri(uploadUrl);
        var request = NewsHttp.Admin(HttpMethod.Put, url.PathAndQuery, user, roles);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }
}

/// <summary>WBS-046 — phần không cần DB: định dạng, slug, PATCH, kho Local, cấu hình, bề mặt API, quyền.</summary>
public sealed class VideoMediaUnitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tt-media-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("Đánh giá iPhone 17 Pro", "danh-gia-iphone-17-pro")]
    [InlineData("!!!", "video")]
    public void Video_slug_comes_from_the_title_plus_an_id_suffix(string title, string prefix)
    {
        var id = Guid.Parse("0190f3c4-1111-7000-8000-00000000abcd");
        Assert.Equal($"{prefix}-0000abcd", VideoSlug.For(title, id));
        Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", VideoSlug.For(string.Join(' ', Enumerable.Repeat("Công nghệ", 100)), id));
    }

    [Fact]
    public void Media_formats_normalize_mime_and_check_file_signatures()
    {
        Assert.Equal("video/mp4", MediaFormats.Normalize(" Video/MP4; codecs=avc1 "));
        Assert.Null(MediaFormats.Normalize("application/x-msdownload"));
        Assert.Null(MediaFormats.Normalize("video/x-matroska"));
        Assert.Equal(MediaTypes.Video, MediaFormats.MediaTypeOf("video/webm"));
        Assert.Equal(MediaTypes.Subtitle, MediaFormats.MediaTypeOf("text/vtt"));
        Assert.True(MediaFormats.HeaderMatches("video/mp4", MediaSamples.Mp4(32)));
        Assert.False(MediaFormats.HeaderMatches("video/mp4", "MZ\u0090\0 not a video"u8.ToArray()));     // .exe đổi đuôi
        Assert.True(MediaFormats.HeaderMatches("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }));
        Assert.True(MediaFormats.HeaderMatches("text/vtt", "\uFEFFWEBVTT\n\n"u8.ToArray()));
        Assert.False(MediaFormats.HeaderMatches("image/webp", "RIFF\0\0\0\0WAVE"u8.ToArray()));
    }

    [Theory]
    [InlineData("clip.mp4", "clip.mp4")]
    [InlineData(@"C:\fakepath\Video hay.mp4", "Video hay.mp4")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a/b/", null)]
    [InlineData("bad\u0001name.mp4", null)]
    [InlineData("..", null)]
    public void File_names_lose_paths_and_control_characters(string input, string? expected) =>
        Assert.Equal(expected, MediaService.CleanFileName(input));

    private static VideoPatch Parse(string json) => VideoPatch.Parse(JsonDocument.Parse(json).RootElement);

    private static IDictionary<string, string[]> PatchErrors(string json) =>
        Assert.Throws<DomainProblemException>(() => Parse(json)) is { Code: ErrorCodes.ValidationFailed, Errors: { } e } ? e : throw new Xunit.Sdk.XunitException("no errors");

    [Fact]
    public void Video_patch_distinguishes_absent_from_null_and_validates_fields()
    {
        var p = Parse("""{"summary":null,"mediaId":"0190f3c4-1111-7000-8000-00000000abcd"}""");
        Assert.True(p.Summary.IsSet);
        Assert.Null(p.Summary.Value);
        Assert.True(p.MediaId.IsSet);
        Assert.True(p.TouchesRevision);
        Assert.False(Parse("""{"mediaId":"0190f3c4-1111-7000-8000-00000000abcd"}""").TouchesRevision);

        Assert.Contains("$", PatchErrors("{}").Keys);
        Assert.Contains("title", PatchErrors("""{"title":""}""").Keys);
        Assert.Contains("mediaId", PatchErrors("""{"mediaId":null}""").Keys);   // UpdateVideoRequest.mediaId không nullable
        Assert.Contains("contentKind", PatchErrors("""{"contentKind":"ADVERTORIAL"}""").Keys);
        Assert.Contains("rightsSource", PatchErrors("""{"rightsSource":"Tự quay"}""").Keys);   // WBS-023
        Assert.Contains("sponsorship.evidenceRef", PatchErrors("""{"sponsorship":{"sponsorName":"A","sponsorReference":"B","validFrom":"2026-01-01T00:00:00Z","validTo":"2026-02-01T00:00:00Z"}}""").Keys);
    }

    [Fact]
    public async Task Local_storage_signs_urls_limits_size_and_hashes_content()
    {
        var storage = new LocalMediaStorage(_root);
        var id = Guid.CreateVersion7();
        var key = MediaStorageKeys.For(id, DateTimeOffset.UtcNow);
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var url = storage.CreateUploadUrl(new MediaUploadTarget(id, key, 10, "video/mp4", expires), new Uri("http://cms.example.test:3001"));
        Assert.Equal("cms.example.test", url.Host);
        Assert.StartsWith($"/api/v1/admin/media/{id}/dev-upload?expires=", url.PathAndQuery, StringComparison.Ordinal);
        var signature = System.Web.HttpUtility.ParseQueryString(url.Query)["signature"];
        Assert.True(storage.Verify(id, 10, expires.ToUnixTimeSeconds(), signature));
        Assert.False(storage.Verify(id, 11, expires.ToUnixTimeSeconds(), signature));        // kích thước khác
        Assert.False(storage.Verify(Guid.NewGuid(), 10, expires.ToUnixTimeSeconds(), signature));
        Assert.False(new LocalMediaStorage(_root).Verify(id, 10, expires.ToUnixTimeSeconds(), signature));   // khóa của tiến trình khác

        var tooBig = await Assert.ThrowsAsync<DomainProblemException>(() => storage.WriteAsync(key, new MemoryStream(new byte[11]), 10, CancellationToken.None));
        Assert.Equal(ErrorCodes.PayloadTooLarge, tooBig.Code);
        Assert.Null(await storage.HeadAsync(key, CancellationToken.None));                     // không để lại tệp dở
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));

        var bytes = Encoding.UTF8.GetBytes("0123456789");
        Assert.Equal(10, await storage.WriteAsync(key, new MemoryStream(bytes), 10, CancellationToken.None));
        Assert.Equal(new StoredObject(10, MediaSamples.Sha256(bytes)), await storage.HeadAsync(key, CancellationToken.None));
        Assert.Equal("0123"u8.ToArray(), await storage.ReadHeaderAsync(key, 4, CancellationToken.None));
        await storage.DeleteAsync(key, CancellationToken.None);
        Assert.Null(await storage.HeadAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.HeadAsync("../../secret", CancellationToken.None));
    }

    [Fact]
    public void Local_storage_is_development_only_and_other_environments_fail_closed()
    {
        IHostEnvironment Env(string name) => new HostingEnvironment { EnvironmentName = name };
        Assert.IsType<LocalMediaStorage>(VideoModule.CreateStorage(new MediaOptions { LocalRoot = _root }, Env(Environments.Development)));
        Assert.IsType<UnconfiguredMediaStorage>(VideoModule.CreateStorage(new MediaOptions(), Env(Environments.Production)));
        Assert.IsType<UnconfiguredMediaStorage>(VideoModule.CreateStorage(new MediaOptions { StorageProvider = "None" }, Env(Environments.Development)));
        Assert.Throws<InvalidOperationException>(() => VideoModule.CreateStorage(new MediaOptions { StorageProvider = "Local" }, Env(Environments.Staging)));
        Assert.Throws<InvalidOperationException>(() => VideoModule.CreateStorage(new MediaOptions { StorageProvider = "S3" }, Env(Environments.Development)));
    }

    [Fact]
    public async Task Admin_video_and_media_api_require_a_staff_session_and_are_only_on_cms()
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        foreach (var path in new[] { "/api/v1/admin/videos", "/api/v1/admin/media", $"/api/v1/admin/media/{Guid.NewGuid()}" })
        {
            using var anonymous = await cms.GetAsync(path);
            await NewsHttp.ProblemAsync(anonymous, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
        }

        using var www = factory.WithProbes().Client(TestHost.Www);
        using var onPublicSite = await www.SendAsync(NewsHttp.Admin(HttpMethod.Get, "/api/v1/admin/videos", Guid.NewGuid(), StaffRoles.Editor));
        Assert.Equal(HttpStatusCode.NotFound, onPublicSite.StatusCode);
    }

    [Theory]
    [InlineData(StaffRoles.Moderator, "/api/v1/admin/media/upload-intents")]   // không có media.upload
    [InlineData(StaffRoles.Approver, "/api/v1/admin/media/upload-intents")]
    [InlineData(StaffRoles.Admin, "/api/v1/admin/videos")]                     // quản trị tài khoản không đụng nội dung
    public async Task Writes_require_the_matching_permission(string roles, string path)
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, path, Guid.NewGuid(), roles, mfa: true,
            json: path.EndsWith("videos", StringComparison.Ordinal) ? """{"title":"x"}""" : MediaSamples.IntentJson("a.mp4", "video/mp4", [1]),
            idempotencyKey: "perm-" + Guid.NewGuid()));
        await NewsHttp.ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Dev_upload_endpoint_is_404_without_local_storage()
    {
        using var app = factory.WithProbes().WithWebHostBuilder(b => b.UseSetting("Media:StorageProvider", "None"));
        using var cms = app.Client(TestHost.Cms);
        using var put = await cms.SendAsync(MediaSamples.Put($"http://cms.tinhte.localhost/api/v1/admin/media/{Guid.NewGuid()}/dev-upload?expires=1&signature=x", [1], Guid.NewGuid()));
        await NewsHttp.ProblemAsync(put, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Without_database_endpoints_return_503_and_bad_queries_400()
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        foreach (var path in new[] { "/api/v1/admin/videos", "/api/v1/admin/media" })
        {
            using var list = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, path, Guid.NewGuid(), StaffRoles.Author));
            await NewsHttp.ProblemAsync(list, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
        }

        foreach (var query in new[] { "/api/v1/admin/videos?status=LIVE", "/api/v1/admin/videos?sort=random", "/api/v1/admin/media?mediaType=AUDIO",
                     "/api/v1/admin/media?rightsStatus=OK", "/api/v1/admin/media?visibility=SECRET", "/api/v1/admin/media?limit=0" })
        {
            using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, query, Guid.NewGuid(), StaffRoles.Author));
            await NewsHttp.ProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
        }
    }
}

/// <summary>
/// WBS-046 trên PostgreSQL thật: upload intent → PUT (kho Local) → finalize → pipeline giả lập → gắn vào video → sửa (If-Match),
/// thư viện media, phân quyền theo dữ liệu, chốt chặn gắn media.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VideoMediaDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly Guid _author = Guid.NewGuid();
    private readonly Guid _otherAuthor = Guid.NewGuid();
    private readonly Guid _editor = Guid.NewGuid();
    private readonly string _root = Directory.CreateTempSubdirectory("tt-media-db-").FullName;
    private WebApplicationFactory<Program>? _app;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) return;
        await ExecAsync($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_author}', 'Tác giả'), ('{_otherAuthor}', 'Tác giả khác'), ('{_editor}', 'Biên tập');
            """);
        _app = factory.WithProbes(db.ConnectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("Media:LocalRoot", _root);
            b.UseSetting("Media:SimulatedStepDelay", "00:00:00");   // đi hết pipeline giả lập trong một lần đọc
        });
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

    private HttpClient Cms() => _app!.Client(TestHost.Cms);

    private static async Task<JsonElement> SendAsync(HttpClient cms, HttpRequestMessage request, HttpStatusCode status)
    {
        using var response = await cms.SendAsync(request);
        return await NewsHttp.JsonAsync(response, status);
    }

    private async Task<JsonElement> IntentAsync(HttpClient cms, string fileName, byte[] bytes, string contentType = "video/mp4", Guid? user = null) =>
        await SendAsync(cms, NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/media/upload-intents", user ?? _author, StaffRoles.Author,
            json: MediaSamples.IntentJson(fileName, contentType, bytes), idempotencyKey: "intent-" + Guid.NewGuid()), HttpStatusCode.Created);

    private async Task<HttpResponseMessage> FinalizeAsync(HttpClient cms, Guid mediaId, string token) =>
        await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, $"/api/v1/admin/media/{mediaId}/finalize", _author, StaffRoles.Author,
            json: JsonSerializer.Serialize(new { uploadToken = token }), idempotencyKey: "finalize-" + Guid.NewGuid()));

    private async Task<JsonElement> MediaAsync(HttpClient cms, Guid mediaId) =>
        await SendAsync(cms, NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/media/{mediaId}", _author, StaffRoles.Author), HttpStatusCode.OK);

    /// <summary>Tải lên trọn vẹn (intent → PUT → finalize) và trả mediaId.</summary>
    private async Task<Guid> UploadAsync(HttpClient cms, string fileName, byte[] bytes, string contentType = "video/mp4")
    {
        var intent = await IntentAsync(cms, fileName, bytes, contentType);
        using (var put = await cms.SendAsync(MediaSamples.Put(intent.GetProperty("uploadUrl").GetString()!, bytes, _author)))
        {
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        }

        var mediaId = intent.GetProperty("mediaId").GetGuid();
        using var finalize = await FinalizeAsync(cms, mediaId, intent.GetProperty("uploadToken").GetString()!);
        Assert.Equal("UPLOADED", (await NewsHttp.JsonAsync(finalize, HttpStatusCode.Accepted)).GetProperty("status").GetString());
        return mediaId;
    }

    private async Task<(JsonElement Video, string ETag)> CreateVideoAsync(HttpClient cms, string json, Guid? owner = null, HttpStatusCode status = HttpStatusCode.Created)
    {
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/videos", owner ?? _author, StaffRoles.Author, json: json,
            idempotencyKey: "video-" + Guid.NewGuid()));
        var body = await NewsHttp.JsonAsync(response, status);
        return (body, response.Headers.ETag?.ToString() ?? "");
    }

    private static async Task<(JsonElement Body, HttpResponseMessage Response)> PatchAsync(HttpClient cms, Guid videoId, string json, string? etag, Guid user, string roles = StaffRoles.Author)
    {
        var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/videos/{videoId}", user, roles, json: json, ifMatch: etag));
        var text = await response.Content.ReadAsStringAsync();
        return (text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), response);
    }

    [DbFact]
    public async Task Upload_finalize_process_attach_and_edit_end_to_end()
    {
        using var cms = Cms();
        var bytes = MediaSamples.Mp4();
        var intent = await IntentAsync(cms, @"C:\fakepath\Mở hộp Galaxy.mp4", bytes);
        var mediaId = intent.GetProperty("mediaId").GetGuid();
        var token = intent.GetProperty("uploadToken").GetString()!;
        Assert.True(intent.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.StartsWith("http://cms.tinhte.localhost/api/v1/admin/media/", intent.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);

        // Token không lưu thô trong DB.
        Assert.Equal(MediaService.TokenHash(token), await db.ScalarAsync<string>($"SELECT upload_token_hash FROM tt.media_assets WHERE id = '{mediaId}'"));

        var uploading = await MediaAsync(cms, mediaId);
        Assert.Equal("UPLOADING", uploading.GetProperty("status").GetString());
        Assert.Equal("Mở hộp Galaxy.mp4", uploading.GetProperty("fileName").GetString());
        Assert.Equal(bytes.Length, uploading.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("VIDEO", uploading.GetProperty("mediaType").GetString());
        Assert.Equal("PRIVATE", uploading.GetProperty("visibility").GetString());
        Assert.Equal("UNVERIFIED", uploading.GetProperty("rightsStatus").GetString());
        Assert.Equal(_author, uploading.GetProperty("createdBy").GetGuid());

        // Finalize trước khi tải lên → 422 MEDIA_NOT_READY; sai token → 422.
        using (var early = await FinalizeAsync(cms, mediaId, token)) await NewsHttp.ProblemAsync(early, HttpStatusCode.UnprocessableEntity, ErrorCodes.MediaNotReady);
        using (var wrong = await FinalizeAsync(cms, mediaId, "not-the-token")) await NewsHttp.ProblemAsync(wrong, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);

        // Tệp tải lên khác tệp đã khai báo → 422, tệp bị xóa; tải lại đúng tệp lên cùng URL rồi finalize được.
        var url = intent.GetProperty("uploadUrl").GetString()!;
        var corrupted = (byte[])bytes.Clone();
        corrupted[^1] ^= 0xFF;
        using (var put = await cms.SendAsync(MediaSamples.Put(url, corrupted, _author))) Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        using (var mismatch = await FinalizeAsync(cms, mediaId, token))
        {
            var problem = await NewsHttp.ProblemAsync(mismatch, HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
            Assert.True(problem.GetProperty("errors").TryGetProperty("checksumSha256", out _));
        }

        using (var put = await cms.SendAsync(MediaSamples.Put(url, bytes, _author))) Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        using (var ok = await FinalizeAsync(cms, mediaId, token))
        {
            Assert.Equal("UPLOADED", (await NewsHttp.JsonAsync(ok, HttpStatusCode.Accepted)).GetProperty("status").GetString());
        }

        Assert.Null(await db.ScalarAsync<string>($"SELECT upload_token_hash FROM tt.media_assets WHERE id = '{mediaId}'"));
        using (var again = await FinalizeAsync(cms, mediaId, token)) await NewsHttp.JsonAsync(again, HttpStatusCode.Accepted);   // gọi lại: không lỗi
        using (var lateUpload = await cms.SendAsync(MediaSamples.Put(url, bytes, _author)))
        {
            await NewsHttp.ProblemAsync(lateUpload, HttpStatusCode.Conflict, ErrorCodes.StateConflict);              // không ghi đè sau finalize
        }

        // Pipeline giả lập (delay 0): đọc trạng thái → READY. Quyền media không đổi.
        var ready = await MediaAsync(cms, mediaId);
        Assert.Equal("READY", ready.GetProperty("status").GetString());
        Assert.Equal("UNVERIFIED", ready.GetProperty("rightsStatus").GetString());

        // Gắn vào video mới.
        var (video, etag) = await CreateVideoAsync(cms, $$"""{"title":"Mở hộp Galaxy S26","summary":"Clip 60 giây","mediaId":"{{mediaId}}"}""");
        var videoId = video.GetProperty("id").GetGuid();
        Assert.Equal("DRAFT", video.GetProperty("status").GetString());
        Assert.Equal(mediaId, video.GetProperty("mediaId").GetGuid());
        Assert.Equal("Clip 60 giây", video.GetProperty("summary").GetString());
        Assert.Equal(_author, video.GetProperty("ownerId").GetGuid());
        Assert.Equal("UNVERIFIED", video.GetProperty("rightsStatus").GetString());
        Assert.Equal(JsonValueKind.Null, video.GetProperty("publicRevisionId").ValueKind);
        Assert.Equal($"W/\"{video.GetProperty("rowVersion").GetInt64()}\"", etag);
        Assert.StartsWith("mo-hop-galaxy-s26-", await db.ScalarAsync<string>($"SELECT slug FROM tt.videos WHERE id = '{videoId}'"), StringComparison.Ordinal);

        // PATCH: thiếu If-Match → 428; ETag cũ → 412; đúng → 200 + ETag mới, sửa tại chỗ revision DRAFT.
        var (_, missing) = await PatchAsync(cms, videoId, """{"title":"x"}""", null, _author);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        var (_, stale) = await PatchAsync(cms, videoId, """{"title":"x"}""", "W/\"999\"", _author);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var (patched, response) = await PatchAsync(cms, videoId, """{"title":"Mở hộp Galaxy S26 Ultra","summary":null,"changeReason":"Đổi tiêu đề"}""", etag, _author);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Mở hộp Galaxy S26 Ultra", patched.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, patched.GetProperty("summary").ValueKind);
        Assert.Equal(video.GetProperty("workingRevisionId").GetGuid(), patched.GetProperty("workingRevisionId").GetGuid());
        Assert.NotEqual(etag, response.Headers.ETag!.ToString());

        // Quyền theo dữ liệu: AUTHOR khác không sửa được; EDITOR (content.write_any) thì được.
        var newEtag = response.Headers.ETag!.ToString();
        var (_, other) = await PatchAsync(cms, videoId, """{"title":"y"}""", newEtag, _otherAuthor);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        var (byEditor, editorResponse) = await PatchAsync(cms, videoId, """{"title":"Biên tập sửa"}""", newEtag, _editor, StaffRoles.Editor);
        Assert.Equal(HttpStatusCode.OK, editorResponse.StatusCode);
        Assert.Equal("Biên tập sửa", byEditor.GetProperty("title").GetString());
        response.Dispose();
        missing.Dispose();
        stale.Dispose();
        other.Dispose();
        editorResponse.Dispose();
    }

    [DbFact]
    public async Task Upload_intent_rejects_unsupported_types_and_oversized_files()
    {
        using var cms = Cms();
        async Task<JsonElement> Post(string json, HttpStatusCode status, string code)
        {
            using var r = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/media/upload-intents", _author, StaffRoles.Author,
                json: json, idempotencyKey: "intent-" + Guid.NewGuid()));
            return await NewsHttp.ProblemAsync(r, status, code);
        }

        var exe = await Post(MediaSamples.IntentJson("setup.exe", "application/x-msdownload", [1]), HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        Assert.True(exe.GetProperty("errors").TryGetProperty("contentType", out _));
        await Post($$"""{"fileName":"big.mp4","contentType":"video/mp4","sizeBytes":{{3L * 1024 * 1024 * 1024}},"checksumSha256":"{{new string('a', 64)}}"}""",
            HttpStatusCode.RequestEntityTooLarge, ErrorCodes.PayloadTooLarge);
        await Post("""{"fileName":"a.mp4","contentType":"video/mp4","sizeBytes":0,"checksumSha256":"abc"}""", HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        await Post(MediaSamples.IntentJson("../", "video/mp4", [1]), HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);

        // Không cấu hình kho lưu trữ (mặc định ngoài Development) → 503, không tạo dòng media.
        using var none = _app!.WithWebHostBuilder(b => b.UseSetting("Media:StorageProvider", "None"));
        using var noneClient = none.Client(TestHost.Cms);
        var before = await db.ScalarAsync<long>("SELECT count(*) FROM tt.media_assets");
        using var unavailable = await noneClient.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/media/upload-intents", _author, StaffRoles.Author,
            json: MediaSamples.IntentJson("a.mp4", "video/mp4", [1]), idempotencyKey: "none-" + Guid.NewGuid()));
        await NewsHttp.ProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
        Assert.Equal(before, await db.ScalarAsync<long>("SELECT count(*) FROM tt.media_assets"));
    }

    [DbFact]
    public async Task Dev_upload_url_is_scoped_to_the_intent()
    {
        using var cms = Cms();
        var bytes = MediaSamples.Mp4(1000);
        var intent = await IntentAsync(cms, "clip.mp4", bytes);
        var url = intent.GetProperty("uploadUrl").GetString()!;

        using (var tampered = await cms.SendAsync(MediaSamples.Put(url.Replace("signature=", "signature=x", StringComparison.Ordinal), bytes, _author)))
        {
            await NewsHttp.ProblemAsync(tampered, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }

        using (var tooBig = await cms.SendAsync(MediaSamples.Put(url, new byte[1001], _author)))
        {
            await NewsHttp.ProblemAsync(tooBig, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.PayloadTooLarge);
        }

        using (var moderator = await cms.SendAsync(MediaSamples.Put(url, bytes, _author, StaffRoles.Moderator)))
        {
            await NewsHttp.ProblemAsync(moderator, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }

        // Intent hết hạn: finalize → 409, tạo intent mới.
        var mediaId = intent.GetProperty("mediaId").GetGuid();
        using (var put = await cms.SendAsync(MediaSamples.Put(url, bytes, _author))) Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        await ExecAsync($"UPDATE tt.media_assets SET upload_expires_at = now() - interval '1 minute' WHERE id = '{mediaId}'");
        using var expired = await FinalizeAsync(cms, mediaId, intent.GetProperty("uploadToken").GetString()!);
        await NewsHttp.ProblemAsync(expired, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
    }

    [DbFact]
    public async Task Renamed_files_are_rejected_by_the_pipeline_and_cannot_be_attached()
    {
        using var cms = Cms();
        var fake = Encoding.UTF8.GetBytes("MZ this is not a video at all, just renamed");
        var mediaId = await UploadAsync(cms, "virus.mp4", fake);
        var media = await MediaAsync(cms, mediaId);
        Assert.Equal("REJECTED", media.GetProperty("status").GetString());
        Assert.Equal(SimulatedMediaProcessor.UnsupportedFormat, media.GetProperty("errorCode").GetString());

        var (problem, _) = await CreateVideoAsync(cms, $$"""{"title":"Không gắn được","mediaId":"{{mediaId}}"}""", status: HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.ValidationFailed, problem.GetProperty("code").GetString());

        // Ảnh không gắn làm media chính của video.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var imageId = await UploadAsync(cms, "poster.png", png, "image/png");
        Assert.Equal("READY", (await MediaAsync(cms, imageId)).GetProperty("status").GetString());   // ảnh không qua PROCESSING
        var (imageProblem, _) = await CreateVideoAsync(cms, $$"""{"title":"Ảnh","mediaId":"{{imageId}}"}""", status: HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.ValidationFailed, imageProblem.GetProperty("code").GetString());
    }

    [DbFact]
    public async Task Media_binding_rules_one_video_per_media_rights_and_published_videos()
    {
        using var cms = Cms();
        var mediaId = await UploadAsync(cms, "a.mp4", MediaSamples.Mp4());
        var (first, etag) = await CreateVideoAsync(cms, $$"""{"title":"Video A","mediaId":"{{mediaId}}"}""");
        var (taken, _) = await CreateVideoAsync(cms, $$"""{"title":"Video B","mediaId":"{{mediaId}}"}""", status: HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.StateConflict, taken.GetProperty("code").GetString());

        var revoked = await UploadAsync(cms, "b.mp4", MediaSamples.Mp4());
        await ExecAsync($"UPDATE tt.media_assets SET rights_status = 'REVOKED' WHERE id = '{revoked}'");
        var firstId = first.GetProperty("id").GetGuid();
        var (rights, rightsResponse) = await PatchAsync(cms, firstId, $$"""{"mediaId":"{{revoked}}"}""", etag, _author);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rightsResponse.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, rights.GetProperty("code").GetString());
        rightsResponse.Dispose();

        // Video đã từng public (giả lập publish pointer — workflow WBS-040 chưa có): đổi media → 409; sửa tiêu đề → revision mới,
        // bản công khai giữ nguyên.
        var replacement = await UploadAsync(cms, "c.mp4", MediaSamples.Mp4());
        await ExecAsync($"UPDATE tt.videos SET public_revision_id = working_revision_id WHERE id = '{firstId}'");
        var current = await SendAsync(cms, NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/videos/{firstId}", _author, StaffRoles.Author), HttpStatusCode.OK);
        var currentEtag = $"W/\"{current.GetProperty("rowVersion").GetInt64()}\"";
        var (_, swap) = await PatchAsync(cms, firstId, $$"""{"mediaId":"{{replacement}}"}""", currentEtag, _author);
        Assert.Equal(HttpStatusCode.Conflict, swap.StatusCode);
        swap.Dispose();
        var (edited, editResponse) = await PatchAsync(cms, firstId, """{"title":"Video A (bản sửa)"}""", currentEtag, _author);
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);
        editResponse.Dispose();
        Assert.NotEqual(current.GetProperty("publicRevisionId").GetGuid(), edited.GetProperty("workingRevisionId").GetGuid());
        Assert.Equal(current.GetProperty("publicRevisionId").GetGuid(), edited.GetProperty("publicRevisionId").GetGuid());
        Assert.Equal("Video A", await db.ScalarAsync<string>($"SELECT r.title FROM tt.videos v JOIN tt.video_revisions r ON r.id = v.public_revision_id WHERE v.id = '{firstId}'"));
        Assert.Equal(2, await db.ScalarAsync<int>($"SELECT max(number) FROM tt.video_revisions WHERE video_id = '{firstId}'"));
    }

    [DbFact]
    public async Task Video_and_media_lists_filter_and_page_with_cursors()
    {
        using var cms = Cms();
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 3; i++) await UploadAsync(cms, $"lib-{tag}-{i}.mp4", MediaSamples.Mp4());
        await UploadAsync(cms, $"lib-{tag}-sub.vtt", "WEBVTT\n\n00:00.000 --> 00:01.000\nXin chào"u8.ToArray(), "text/vtt");

        var page1 = await SendAsync(cms, NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/media?mediaType=VIDEO&q=lib-{tag}&limit=2", _author, StaffRoles.Author), HttpStatusCode.OK);
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        Assert.Equal($"lib-{tag}-2.mp4", page1.GetProperty("items")[0].GetProperty("fileName").GetString());   // mới nhất trước
        var cursor = page1.GetProperty("nextCursor").GetString()!;
        var page2 = await SendAsync(cms, NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/media?mediaType=VIDEO&q=lib-{tag}&limit=2&cursor={Uri.EscapeDataString(cursor)}", _author, StaffRoles.Author), HttpStatusCode.OK);
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);
        using (var reused = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/media?mediaType=SUBTITLE&limit=2&cursor={Uri.EscapeDataString(cursor)}", _author, StaffRoles.Author)))
        {
            await NewsHttp.ProblemAsync(reused, HttpStatusCode.BadRequest, ErrorCodes.InvalidCursor);   // cursor gắn với bộ lọc
        }

        var subtitles = await SendAsync(cms, NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/media?mediaType=SUBTITLE&q=lib-{tag}", _author, StaffRoles.Author), HttpStatusCode.OK);
        Assert.Equal("READY", Assert.Single(subtitles.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());

        for (var i = 0; i < 3; i++) await CreateVideoAsync(cms, $$"""{"title":"Danh sách {{tag}} {{i}}"}""", owner: i == 2 ? _otherAuthor : _author);
        var mine = await SendAsync(cms, NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/videos?q={tag}&ownerId={_author}&status=DRAFT,IN_REVIEW", _author, StaffRoles.Author), HttpStatusCode.OK);
        Assert.Equal(2, mine.GetProperty("items").GetArrayLength());
        var byTitle = await SendAsync(cms, NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/videos?q={tag}&sort=title_asc&limit=2", _author, StaffRoles.Author), HttpStatusCode.OK);
        Assert.Equal($"Danh sách {tag} 0", byTitle.GetProperty("items")[0].GetProperty("title").GetString());
        var next = await SendAsync(cms, NewsHttp.Admin(HttpMethod.Get,
            $"/api/v1/admin/videos?q={tag}&sort=title_asc&limit=2&cursor={Uri.EscapeDataString(byTitle.GetProperty("nextCursor").GetString()!)}", _author, StaffRoles.Author), HttpStatusCode.OK);
        Assert.Equal($"Danh sách {tag} 2", Assert.Single(next.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        var published = await SendAsync(cms, NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/videos?q={tag}&status=PUBLISHED", _author, StaffRoles.Author), HttpStatusCode.OK);
        Assert.Equal(0, published.GetProperty("items").GetArrayLength());
    }

    [DbFact]
    public async Task Create_is_idempotent_and_validates_input()
    {
        using var cms = Cms();
        var key = "video-" + Guid.NewGuid();
        const string json = """{"title":"Idempotent video"}""";
        using var first = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/videos", _author, StaffRoles.Author, json: json, idempotencyKey: key));
        using var replay = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/videos", _author, StaffRoles.Author, json: json, idempotencyKey: key));
        Assert.Equal((await NewsHttp.JsonAsync(first, HttpStatusCode.Created)).GetProperty("id").GetGuid(),
            (await NewsHttp.JsonAsync(replay, HttpStatusCode.Created)).GetProperty("id").GetGuid());
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());

        var (sponsored, _) = await CreateVideoAsync(cms, """{"title":"x","contentKind":"SPONSORED"}""", status: HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.SponsorshipLabelRequired, sponsored.GetProperty("code").GetString());
        var (unknownMedia, _) = await CreateVideoAsync(cms, $$"""{"title":"x","mediaId":"{{Guid.NewGuid()}}"}""", status: HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.ValidationFailed, unknownMedia.GetProperty("code").GetString());
        var (rightsSource, _) = await CreateVideoAsync(cms, """{"title":"x","rightsSource":"Tự quay"}""", status: HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.ValidationFailed, rightsSource.GetProperty("code").GetString());

        using var missing = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/videos/{Guid.NewGuid()}", _author, StaffRoles.Author));
        await NewsHttp.ProblemAsync(missing, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        using var missingMedia = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/media/{Guid.NewGuid()}", _author, StaffRoles.Author));
        await NewsHttp.ProblemAsync(missingMedia, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }
}

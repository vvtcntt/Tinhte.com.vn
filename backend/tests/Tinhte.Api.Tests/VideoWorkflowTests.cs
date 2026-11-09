using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.Video.Application;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>WBS-040 — phần không cần DB.</summary>
public sealed class VideoWorkflowUnitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData(null)]
    [InlineData("    ")]
    [InlineData(" ok  ")]
    public void Missing_or_short_reason_is_reason_required(string? reason) =>
        Assert.Equal(ErrorCodes.ReasonRequired, Assert.Throws<DomainProblemException>(() => VideoService.CheckReason(reason)).Code);

    [Fact]
    public void Reason_is_trimmed_and_capped()
    {
        Assert.Equal("Vi phạm bản quyền", VideoService.CheckReason("  Vi phạm bản quyền "));
        Assert.Equal(ErrorCodes.ValidationFailed, Assert.Throws<DomainProblemException>(() => VideoService.CheckReason(new string('x', 2001))).Code);
    }

    [Theory]
    [InlineData("submit", StaffRoles.Moderator, false)]
    [InlineData("approve", StaffRoles.Editor, false)]        // EDITOR có content.review nhưng không có content.approve
    [InlineData("approve", StaffRoles.Approver, false)]      // content.approve cần MFA
    [InlineData("request-changes", StaffRoles.Author, false)]
    [InlineData("reject", StaffRoles.Editor, false)]
    [InlineData("publish", StaffRoles.Editor, true)]
    [InlineData("unpublish", StaffRoles.Editor, true)]       // gỡ cần content.takedown
    public async Task Workflow_actions_check_permission_before_touching_data(string action, string roles, bool mfa)
    {
        using var cms = factory.WithProbes().Client(TestHost.Cms);
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, $"/api/v1/admin/videos/{Guid.NewGuid()}/{action}", Guid.NewGuid(), roles, mfa,
            json: $$"""{"revisionId":"{{Guid.NewGuid()}}","reason":"Lý do đủ dài"}""", ifMatch: "W/\"1\"", idempotencyKey: "perm-" + Guid.NewGuid()));
        await NewsHttp.ProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }
}

/// <summary>
/// WBS-040 trên PostgreSQL: gửi duyệt → duyệt (SoD, MFA) / yêu cầu sửa / từ chối (lý do) → xuất bản qua chốt chặn media + quyền →
/// revision media → gỡ. Quyền CLEARED do WBS-023 đặt: test đặt thẳng bằng SQL.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VideoWorkflowDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string AuthorApprover = $"{StaffRoles.Author},{StaffRoles.Approver}";
    private readonly Guid _author = Guid.NewGuid();
    private readonly Guid _otherAuthor = Guid.NewGuid();
    private readonly Guid _editor = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly string _root = Directory.CreateTempSubdirectory("tt-video-wf-").FullName;
    private WebApplicationFactory<Program>? _app;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) return;
        await ExecAsync($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_author}', 'Tác giả'), ('{_otherAuthor}', 'Tác giả khác'), ('{_editor}', 'Biên tập'), ('{_approver}', 'Người duyệt');
            """);
        _app = factory.WithProbes(db.ConnectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("Media:LocalRoot", _root);
            b.UseSetting("Media:SimulatedStepDelay", "00:00:00");
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

    private sealed record State(Guid Id, Guid WorkingRevisionId, string ETag, JsonElement Body)
    {
        public string Status => Body.GetProperty("status").GetString()!;
    }

    private static async Task<State> StateAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var body = await NewsHttp.JsonAsync(response, status);
        return new State(body.GetProperty("id").GetGuid(), body.GetProperty("workingRevisionId").GetGuid(), response.Headers.ETag!.ToString(), body);
    }

    /// <summary>Tải lên + finalize (UPLOADED). <paramref name="ready"/>: đọc trạng thái một lần để bộ giả lập đưa về READY.</summary>
    private async Task<Guid> UploadAsync(HttpClient cms, bool ready = true)
    {
        var bytes = MediaSamples.Mp4(2048);
        using var intentResponse = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/media/upload-intents", _author, StaffRoles.Author,
            json: MediaSamples.IntentJson("clip.mp4", "video/mp4", bytes), idempotencyKey: "intent-" + Guid.NewGuid()));
        var intent = await NewsHttp.JsonAsync(intentResponse, HttpStatusCode.Created);
        using (var put = await cms.SendAsync(MediaSamples.Put(intent.GetProperty("uploadUrl").GetString()!, bytes, _author)))
        {
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        }

        var mediaId = intent.GetProperty("mediaId").GetGuid();
        using (var finalize = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, $"/api/v1/admin/media/{mediaId}/finalize", _author, StaffRoles.Author,
            json: JsonSerializer.Serialize(new { uploadToken = intent.GetProperty("uploadToken").GetString() }), idempotencyKey: "finalize-" + Guid.NewGuid())))
        {
            await NewsHttp.JsonAsync(finalize, HttpStatusCode.Accepted);
        }

        if (ready) Assert.Equal("READY", (await GetMediaAsync(cms, mediaId)).GetProperty("status").GetString());
        return mediaId;
    }

    private async Task<JsonElement> GetMediaAsync(HttpClient cms, Guid mediaId)
    {
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/media/{mediaId}", _author, StaffRoles.Author));
        return await NewsHttp.JsonAsync(response, HttpStatusCode.OK);
    }

    private async Task<State> CreateAsync(HttpClient cms, Guid? mediaId, string extra = "")
    {
        var media = mediaId is { } m ? $",\"mediaId\":\"{m}\"" : "";
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/videos", _author, StaffRoles.Author,
            json: $$"""{"title":"Video quy trình {{Guid.NewGuid():N}}"{{media}}{{extra}}}""", idempotencyKey: "video-" + Guid.NewGuid()));
        return await StateAsync(response, HttpStatusCode.Created);
    }

    private static HttpRequestMessage Action(string action, State s, Guid user, string roles, bool mfa = false, string? json = null, string? ifMatch = "",
        string? key = null) =>
        NewsHttp.Admin(HttpMethod.Post, $"/api/v1/admin/videos/{s.Id}/{action}", user, roles, mfa, json ?? "{}", ifMatch == "" ? s.ETag : ifMatch,
            key ?? $"{action}-{Guid.NewGuid()}");

    private async Task<State> DoAsync(HttpClient cms, string action, State s, Guid user, string roles, bool mfa = false, string? json = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await cms.SendAsync(Action(action, s, user, roles, mfa, json));
        return await StateAsync(response, status);
    }

    private async Task ProblemAsync(HttpClient cms, HttpRequestMessage request, HttpStatusCode status, string code)
    {
        using var response = await cms.SendAsync(request);
        await NewsHttp.ProblemAsync(response, status, code);
    }

    private static string Revision(State s) => $$"""{"revisionId":"{{s.WorkingRevisionId}}"}""";

    private async Task ClearRightsAsync(Guid videoId, Guid mediaId)
    {
        await ExecAsync($"""
            UPDATE tt.media_assets SET rights_status = 'CLEARED' WHERE id = '{mediaId}';
            UPDATE tt.videos SET rights_status = 'CLEARED' WHERE id = '{videoId}';
            """);
    }

    private async Task<State> ReloadAsync(HttpClient cms, Guid videoId)
    {
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/videos/{videoId}", _author, StaffRoles.Author));
        return await StateAsync(response);
    }

    /// <summary>Video có media READY, quyền CLEARED, đã gửi duyệt + duyệt (chưa publish).</summary>
    private async Task<(State Approved, Guid MediaId)> ApprovedAsync(HttpClient cms)
    {
        var mediaId = await UploadAsync(cms);
        var created = await CreateAsync(cms, mediaId);
        var submitted = await DoAsync(cms, "submit", created, _author, StaffRoles.Author);
        var approved = await DoAsync(cms, "approve", submitted, _approver, StaffRoles.Approver, mfa: true, json: Revision(submitted));
        await ClearRightsAsync(approved.Id, mediaId);
        return (await ReloadAsync(cms, approved.Id), mediaId);
    }

    [DbFact]
    public async Task Submit_approve_publish_with_separation_of_duties_mfa_and_rights_gate()
    {
        using var cms = Cms();
        var mediaId = await UploadAsync(cms);

        // Không có media → không gửi duyệt được.
        var empty = await CreateAsync(cms, null);
        await ProblemAsync(cms, Action("submit", empty, _author, StaffRoles.Author), HttpStatusCode.Conflict, ErrorCodes.StateConflict);

        var created = await CreateAsync(cms, mediaId);
        await ProblemAsync(cms, Action("submit", created, _author, StaffRoles.Author, ifMatch: null), (HttpStatusCode)428, ErrorCodes.PreconditionRequired);
        await ProblemAsync(cms, Action("submit", created, _author, StaffRoles.Author, ifMatch: "W/\"999\""), HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
        await ProblemAsync(cms, Action("submit", created, _otherAuthor, StaffRoles.Author), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);   // video người khác cần write_any

        var submitted = await DoAsync(cms, "submit", created, _author, StaffRoles.Author);
        Assert.Equal("IN_REVIEW", submitted.Status);
        Assert.Equal("IN_REVIEW", submitted.Body.GetProperty("revisionStatus").GetString());
        Assert.Equal(_author, submitted.Body.GetProperty("submittedBy").GetGuid());
        await ProblemAsync(cms, Action("submit", submitted, _author, StaffRoles.Author), HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        using (var patch = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/videos/{submitted.Id}", _author, StaffRoles.Author,
            json: """{"title":"Lén sửa"}""", ifMatch: submitted.ETag)))
        {
            await NewsHttp.ProblemAsync(patch, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        }

        // Duyệt: tác giả kiêm người duyệt không tự duyệt (SoD); sai revision → 422; người duyệt khác (MFA) → APPROVED + thế hệ media.
        await ProblemAsync(cms, Action("approve", submitted, _author, AuthorApprover, mfa: true, Revision(submitted)), HttpStatusCode.Forbidden, ErrorCodes.SodViolation);
        await ProblemAsync(cms, Action("approve", submitted, _approver, StaffRoles.Approver, mfa: true, $$"""{"revisionId":"{{Guid.NewGuid()}}"}"""),
            HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        var approved = await DoAsync(cms, "approve", submitted, _approver, StaffRoles.Approver, mfa: true,
            json: $$"""{"revisionId":"{{submitted.WorkingRevisionId}}","note":"Ổn, chờ quyền"}""");
        Assert.Equal("APPROVED", approved.Status);
        Assert.Equal(_approver, approved.Body.GetProperty("reviewedBy").GetGuid());
        Assert.Equal("Ổn, chờ quyền", approved.Body.GetProperty("reviewNote").GetString());
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT approved_media_generation FROM tt.video_revisions WHERE id = '{approved.WorkingRevisionId}'"));

        // Chốt chặn quyền: media UNVERIFIED → 422; media CLEARED nhưng video UNVERIFIED → 422; video hết hạn quyền → 422.
        var publish = Revision(approved);
        await ProblemAsync(cms, Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, publish), HttpStatusCode.UnprocessableEntity, ErrorCodes.RightsInvalid);
        await ExecAsync($"UPDATE tt.media_assets SET rights_status = 'CLEARED' WHERE id = '{mediaId}'");
        await ProblemAsync(cms, Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, publish), HttpStatusCode.UnprocessableEntity, ErrorCodes.RightsInvalid);
        await ExecAsync($"UPDATE tt.videos SET rights_status = 'CLEARED', rights_expires_at = now() - interval '1 day' WHERE id = '{approved.Id}'");
        approved = await ReloadAsync(cms, approved.Id);   // đổi quyền làm tăng row_version (ETag)
        await ProblemAsync(cms, Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, publish), HttpStatusCode.UnprocessableEntity, ErrorCodes.RightsInvalid);
        await ExecAsync($"UPDATE tt.videos SET rights_expires_at = now() + interval '30 days' WHERE id = '{approved.Id}'");
        approved = await ReloadAsync(cms, approved.Id);

        // Không MFA → 403; tác giả (có quyền publish) không tự xuất bản → SOD_VIOLATION; ETag cũ → 412.
        await ProblemAsync(cms, Action("publish", approved, _approver, StaffRoles.Approver, mfa: false, publish), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await ProblemAsync(cms, Action("publish", approved, _author, AuthorApprover, mfa: true, publish), HttpStatusCode.Forbidden, ErrorCodes.SodViolation);
        await ProblemAsync(cms, Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, publish, ifMatch: submitted.ETag), HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);

        var key = "publish-" + Guid.NewGuid();
        using var first = await cms.SendAsync(Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, publish, key: key));
        var published = await StateAsync(first);
        Assert.Equal("PUBLISHED", published.Status);
        Assert.Equal(approved.WorkingRevisionId, published.Body.GetProperty("publicRevisionId").GetGuid());
        Assert.Equal(mediaId, await db.ScalarAsync<Guid>($"SELECT media_id FROM tt.videos WHERE id = '{published.Id}'"));   // media đang phát
        Assert.Equal(_approver, await db.ScalarAsync<Guid>($"SELECT published_by FROM tt.video_revisions WHERE id = '{published.WorkingRevisionId}'"));

        using var replay = await cms.SendAsync(Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, publish, key: key));
        Assert.Equal(published.Body.GetProperty("rowVersion").GetInt64(), (await StateAsync(replay)).Body.GetProperty("rowVersion").GetInt64());
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        await ProblemAsync(cms, Action("publish", published, _approver, StaffRoles.Approver, mfa: true, publish), HttpStatusCode.Conflict, ErrorCodes.StateConflict);
    }

    [DbFact]
    public async Task Publish_requires_ready_media_of_the_approved_generation()
    {
        using var cms = Cms();
        var mediaId = await UploadAsync(cms, ready: false);   // UPLOADED: chưa đọc trạng thái → bộ giả lập chưa chạy
        var created = await CreateAsync(cms, mediaId);
        var submitted = await DoAsync(cms, "submit", created, _author, StaffRoles.Author);
        var approved = await DoAsync(cms, "approve", submitted, _approver, StaffRoles.Approver, mfa: true, json: Revision(submitted));
        await ClearRightsAsync(approved.Id, mediaId);
        approved = await ReloadAsync(cms, approved.Id);

        await ProblemAsync(cms, Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, Revision(approved)), HttpStatusCode.UnprocessableEntity, ErrorCodes.MediaNotReady);

        Assert.Equal("READY", (await GetMediaAsync(cms, mediaId)).GetProperty("status").GetString());
        await ExecAsync($"UPDATE tt.media_assets SET generation = 2 WHERE id = '{mediaId}'");   // xử lý lại sau khi duyệt (WBS-038)
        await ProblemAsync(cms, Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, Revision(approved)), HttpStatusCode.Conflict, ErrorCodes.StateConflict);

        await ExecAsync($"UPDATE tt.media_assets SET generation = 1 WHERE id = '{mediaId}'");
        Assert.Equal("PUBLISHED", (await DoAsync(cms, "publish", approved, _approver, StaffRoles.Approver, mfa: true, json: Revision(approved))).Status);
    }

    [DbFact]
    public async Task Sponsored_video_is_blocked_while_the_flag_is_off()
    {
        using var cms = Cms();
        var mediaId = await UploadAsync(cms);
        var created = await CreateAsync(cms, mediaId, ""","contentKind":"SPONSORED","sponsorship":{"sponsorName":"ACME","sponsorReference":"HD-01","evidenceRef":"dms://hd-01","validFrom":"2026-01-01T00:00:00Z","validTo":"2030-01-01T00:00:00Z"}""");
        var submitted = await DoAsync(cms, "submit", created, _author, StaffRoles.Author);
        var approved = await DoAsync(cms, "approve", submitted, _approver, StaffRoles.Approver, mfa: true, json: Revision(submitted));
        await ClearRightsAsync(approved.Id, mediaId);
        approved = await ReloadAsync(cms, approved.Id);

        // ads.sponsored_publication OFF (production mặc định): trigger 0307 chặn trong transaction publish. Test không bật cờ.
        await ProblemAsync(cms, Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, Revision(approved)), HttpStatusCode.Forbidden, ErrorCodes.FeatureDisabled);
        Assert.Null(await db.ScalarAsync<Guid?>($"SELECT public_revision_id FROM tt.videos WHERE id = '{approved.Id}'"));
    }

    [DbFact]
    public async Task Request_changes_and_reject_need_a_reason_and_return_the_video_to_its_author()
    {
        using var cms = Cms();
        var created = await CreateAsync(cms, await UploadAsync(cms));
        var submitted = await DoAsync(cms, "submit", created, _author, StaffRoles.Author);

        await ProblemAsync(cms, Action("request-changes", submitted, _editor, StaffRoles.Editor, json: """{"reason":"ok"}"""), HttpStatusCode.UnprocessableEntity, ErrorCodes.ReasonRequired);
        var returned = await DoAsync(cms, "request-changes", submitted, _editor, StaffRoles.Editor, json: """{"reason":"Thiếu phụ đề và nguồn"}""", status: HttpStatusCode.Created);
        Assert.Equal("CHANGES_REQUESTED", returned.Status);
        Assert.Equal(_author, returned.Body.GetProperty("assigneeId").GetGuid());
        Assert.Equal("Thiếu phụ đề và nguồn", returned.Body.GetProperty("reviewNote").GetString());

        // Sửa → revision mới DRAFT; gửi lại; từ chối cần content.approve (MFA).
        using var patchResponse = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/videos/{returned.Id}", _author, StaffRoles.Author,
            json: """{"summary":"Đã thêm nguồn","changeReason":"Theo góp ý"}""", ifMatch: returned.ETag));
        var edited = await StateAsync(patchResponse);
        Assert.Equal("DRAFT", edited.Status);
        Assert.NotEqual(returned.WorkingRevisionId, edited.WorkingRevisionId);
        var resubmitted = await DoAsync(cms, "submit", edited, _author, StaffRoles.Author);
        await ProblemAsync(cms, Action("reject", resubmitted, _approver, StaffRoles.Approver, mfa: true, json: "{}"), HttpStatusCode.UnprocessableEntity, ErrorCodes.ReasonRequired);
        var rejected = await DoAsync(cms, "reject", resubmitted, _approver, StaffRoles.Approver, mfa: true, json: """{"reason":"Nội dung trùng video đã có"}""");
        Assert.Equal("REJECTED", rejected.Status);
        await ProblemAsync(cms, Action("approve", rejected, _approver, StaffRoles.Approver, mfa: true, Revision(rejected)), HttpStatusCode.Conflict, ErrorCodes.StateConflict);
    }

    [DbFact]
    public async Task Media_change_on_a_published_video_goes_through_a_new_revision_then_unpublish()
    {
        using var cms = Cms();
        var (approved, oldMedia) = await ApprovedAsync(cms);
        var published = await DoAsync(cms, "publish", approved, _approver, StaffRoles.Approver, mfa: true, json: Revision(approved));
        var publicRevision = published.WorkingRevisionId;

        // Đổi media của video đang public: revision mới, bản công khai (và media đang phát) giữ nguyên.
        var newMedia = await UploadAsync(cms);
        using var patchResponse = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/videos/{published.Id}", _author, StaffRoles.Author,
            json: $$"""{"mediaId":"{{newMedia}}","changeReason":"Bản dựng lại"}""", ifMatch: published.ETag));
        var edited = await StateAsync(patchResponse);
        Assert.Equal(newMedia, edited.Body.GetProperty("mediaId").GetGuid());
        Assert.NotEqual(publicRevision, edited.WorkingRevisionId);
        Assert.Equal(publicRevision, edited.Body.GetProperty("publicRevisionId").GetGuid());
        Assert.Equal(oldMedia, await db.ScalarAsync<Guid>($"SELECT media_id FROM tt.videos WHERE id = '{edited.Id}'"));

        var submitted = await DoAsync(cms, "submit", edited, _author, StaffRoles.Author);
        var approvedAgain = await DoAsync(cms, "approve", submitted, _approver, StaffRoles.Approver, mfa: true, json: Revision(submitted));
        await ProblemAsync(cms, Action("publish", approvedAgain, _approver, StaffRoles.Approver, mfa: true, Revision(approvedAgain)), HttpStatusCode.UnprocessableEntity, ErrorCodes.RightsInvalid);
        await ExecAsync($"UPDATE tt.media_assets SET rights_status = 'CLEARED' WHERE id = '{newMedia}'");
        var republished = await DoAsync(cms, "publish", approvedAgain, _approver, StaffRoles.Approver, mfa: true, json: Revision(approvedAgain));
        Assert.Equal(newMedia, await db.ScalarAsync<Guid>($"SELECT media_id FROM tt.videos WHERE id = '{republished.Id}'"));
        Assert.Equal("SUPERSEDED", await db.ScalarAsync<string>($"SELECT status FROM tt.video_revisions WHERE id = '{publicRevision}'"));

        // Gỡ: cần content.takedown (MFA) + lý do; If-Match tùy chọn nhưng nếu gửi thì phải khớp.
        await ProblemAsync(cms, Action("unpublish", republished, _approver, StaffRoles.Approver, mfa: true, """{"reason":"x"}"""), HttpStatusCode.UnprocessableEntity, ErrorCodes.ReasonRequired);
        await ProblemAsync(cms, Action("unpublish", republished, _approver, StaffRoles.Approver, mfa: true, """{"reason":"Khiếu nại bản quyền"}""", ifMatch: published.ETag),
            HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
        var unpublished = await DoAsync(cms, "unpublish", republished with { ETag = null! }, _approver, StaffRoles.Approver, mfa: true,
            json: """{"reason":"Khiếu nại bản quyền"}""");
        Assert.Equal("UNPUBLISHED", unpublished.Status);
        Assert.Equal(JsonValueKind.Null, unpublished.Body.GetProperty("publicRevisionId").ValueKind);
        Assert.Equal("Khiếu nại bản quyền", unpublished.Body.GetProperty("unpublishReason").GetString());
        Assert.Null(await db.ScalarAsync<Guid?>($"SELECT media_id FROM tt.videos WHERE id = '{unpublished.Id}'"));   // không còn media phát công khai
        await ProblemAsync(cms, Action("unpublish", unpublished, _approver, StaffRoles.Approver, mfa: true, """{"reason":"Gỡ lần nữa"}"""), HttpStatusCode.Conflict, ErrorCodes.StateConflict);

        // Sau khi gỡ vẫn sửa được (revision mới) để đưa lên lại qua duyệt.
        using var after = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/videos/{unpublished.Id}", _author, StaffRoles.Author,
            json: """{"title":"Bản sửa sau khi gỡ"}""", ifMatch: unpublished.ETag));
        var draft = await StateAsync(after);
        Assert.Equal("UNPUBLISHED", draft.Status);
        Assert.Equal("DRAFT", draft.Body.GetProperty("revisionStatus").GetString());
        Assert.Equal("IN_REVIEW", (await DoAsync(cms, "submit", draft, _author, StaffRoles.Author)).Status);
    }
}

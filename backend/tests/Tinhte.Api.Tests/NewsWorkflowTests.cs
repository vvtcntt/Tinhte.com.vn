using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Modules.News.Application;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>WBS-024 — phần không cần DB.</summary>
public sealed class NewsWorkflowUnitTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("    ")]
    [InlineData(" ok  ")]
    public void Missing_or_short_reason_is_reason_required(string? reason) =>
        Assert.Equal(ErrorCodes.ReasonRequired, Assert.Throws<DomainProblemException>(() => ArticleService.CheckReason(reason)).Code);

    [Fact]
    public void Reason_is_trimmed_and_capped()
    {
        Assert.Equal("Thiếu nguồn ảnh", ArticleService.CheckReason("  Thiếu nguồn ảnh "));
        Assert.Equal(ErrorCodes.ValidationFailed, Assert.Throws<DomainProblemException>(() => ArticleService.CheckReason(new string('x', 2001))).Code);
    }
}

/// <summary>WBS-024 trên PostgreSQL: gửi duyệt → duyệt (SoD, MFA) / yêu cầu sửa / từ chối (lý do) → xuất bản.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class NewsWorkflowDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string AuthorApprover = $"{StaffRoles.Author},{StaffRoles.Approver}";
    private readonly Guid _author = Guid.NewGuid();
    private readonly Guid _otherAuthor = Guid.NewGuid();
    private readonly Guid _editor = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly Guid _category = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) return;
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO tt.users (id, display_name) VALUES ('{_author}', 'Tác giả'), ('{_otherAuthor}', 'Tác giả khác'), ('{_editor}', 'Biên tập'), ('{_approver}', 'Người duyệt');
            INSERT INTO tt.categories (id, slug, name) VALUES ('{_category}', 'wf-{Guid.NewGuid():N}', 'Quy trình');
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Cms() => factory.WithProbes(db.ConnectionString).Client(TestHost.Cms);

    private sealed record State(Guid Id, Guid WorkingRevisionId, string ETag, JsonElement Body);

    private static async Task<State> StateAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await NewsHttp.JsonAsync(response, status);
        return new State(body.GetProperty("id").GetGuid(), body.GetProperty("workingRevisionId").GetGuid(), response.Headers.ETag!.ToString(), body);
    }

    private async Task<State> CreateAsync(HttpClient cms, Guid? author = null)
    {
        using var response = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Post, "/api/v1/admin/articles", author ?? _author, AuthorApprover, mfa: true,
            json: $$"""{"title":"Bài quy trình {{Guid.NewGuid():N}}","categoryId":"{{_category}}","initialBodyBlocks":[{"type":"paragraph","text":"Nội dung"}]}""",
            idempotencyKey: "wf-" + Guid.NewGuid()));
        return await StateAsync(response, HttpStatusCode.Created);
    }

    private static HttpRequestMessage Action(string action, State s, Guid user, string roles, bool mfa = false, string? json = null, string? ifMatch = "") =>
        NewsHttp.Admin(HttpMethod.Post, $"/api/v1/admin/articles/{s.Id}/{action}", user, roles, mfa, json ?? "{}", ifMatch == "" ? s.ETag : ifMatch, $"{action}-{Guid.NewGuid()}");

    private async Task<State> SubmitAsync(HttpClient cms, State s, Guid? user = null)
    {
        using var response = await cms.SendAsync(Action("submit", s, user ?? _author, StaffRoles.Author));
        return await StateAsync(response, HttpStatusCode.OK);
    }

    private async Task ClearRightsAsync(Guid articleId)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"UPDATE tt.articles SET rights_status = 'CLEARED' WHERE id = '{articleId}'", connection);
        await command.ExecuteNonQueryAsync();
    }

    [DbFact]
    public async Task Submit_approve_publish_with_separation_of_duties_and_mfa()
    {
        using var cms = Cms();
        var created = await CreateAsync(cms);

        using (var noIfMatch = await cms.SendAsync(Action("submit", created, _author, StaffRoles.Author, ifMatch: null)))
        {
            await NewsHttp.ProblemAsync(noIfMatch, (HttpStatusCode)428, ErrorCodes.PreconditionRequired);
        }

        using (var stranger = await cms.SendAsync(Action("submit", created, _otherAuthor, StaffRoles.Author)))
        {
            await NewsHttp.ProblemAsync(stranger, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);   // bài người khác cần write_any
        }

        var submitted = await SubmitAsync(cms, created);
        Assert.Equal("IN_REVIEW", submitted.Body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, submitted.Body.GetProperty("assigneeId").ValueKind);

        using (var again = await cms.SendAsync(Action("submit", submitted, _author, StaffRoles.Author)))
        {
            await NewsHttp.ProblemAsync(again, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        }

        using (var patchInReview = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/articles/{submitted.Id}", _author, StaffRoles.Author,
            json: """{"title":"Lén sửa"}""", ifMatch: submitted.ETag)))
        {
            await NewsHttp.ProblemAsync(patchInReview, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
        }

        var approve = $$"""{"revisionId":"{{submitted.WorkingRevisionId}}","note":"Ổn"}""";
        using (var self = await cms.SendAsync(Action("approve", submitted, _author, AuthorApprover, mfa: true, approve)))
        {
            await NewsHttp.ProblemAsync(self, HttpStatusCode.Forbidden, ErrorCodes.SodViolation);    // không tự duyệt bài mình viết
        }

        using (var noMfa = await cms.SendAsync(Action("approve", submitted, _approver, StaffRoles.Approver, mfa: false, approve)))
        {
            await NewsHttp.ProblemAsync(noMfa, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }

        using (var editor = await cms.SendAsync(Action("approve", submitted, _editor, StaffRoles.Editor, mfa: true, approve)))
        {
            await NewsHttp.ProblemAsync(editor, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);     // EDITOR không có content.approve
        }

        using var approvedResponse = await cms.SendAsync(Action("approve", submitted, _approver, StaffRoles.Approver, mfa: true, approve));
        var approved = await StateAsync(approvedResponse, HttpStatusCode.OK);
        Assert.Equal("APPROVED", approved.Body.GetProperty("status").GetString());

        using (var revision = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles/{approved.Id}/revisions/{approved.WorkingRevisionId}", _author, StaffRoles.Author)))
        {
            var rev = await NewsHttp.JsonAsync(revision, HttpStatusCode.OK);
            Assert.Equal("APPROVED", rev.GetProperty("status").GetString());
            Assert.Equal(_author, rev.GetProperty("submittedBy").GetGuid());
            Assert.Equal(_approver, rev.GetProperty("reviewedBy").GetGuid());
            Assert.Equal("Ổn", rev.GetProperty("reviewNote").GetString());
        }

        await ClearRightsAsync(approved.Id);
        var etag = $"W/\"{await db.ScalarAsync<long>($"SELECT row_version FROM tt.articles WHERE id = '{approved.Id}'")}\"";
        var publish = $$"""{"revisionId":"{{approved.WorkingRevisionId}}"}""";
        using (var selfPublish = await cms.SendAsync(Action("publish", approved, _author, AuthorApprover, mfa: true, publish, etag)))
        {
            await NewsHttp.ProblemAsync(selfPublish, HttpStatusCode.Forbidden, ErrorCodes.SodViolation);
        }

        using var published = await cms.SendAsync(Action("publish", approved, _approver, StaffRoles.Approver, mfa: true, publish, etag));
        Assert.Equal("PUBLISHED", (await NewsHttp.JsonAsync(published, HttpStatusCode.OK)).GetProperty("status").GetString());
    }

    [DbFact]
    public async Task Request_changes_needs_a_reason_and_hands_the_article_back_to_the_author()
    {
        using var cms = Cms();
        var submitted = await SubmitAsync(cms, await CreateAsync(cms));

        foreach (var body in new[] { "{}", """{"reason":"  ok "}""" })
        {
            using var missing = await cms.SendAsync(Action("request-changes", submitted, _editor, StaffRoles.Editor, json: body));
            await NewsHttp.ProblemAsync(missing, HttpStatusCode.UnprocessableEntity, ErrorCodes.ReasonRequired);
        }

        using var response = await cms.SendAsync(Action("request-changes", submitted, _editor, StaffRoles.Editor, json: """{"reason":"Thiếu nguồn ảnh minh họa"}"""));
        var returned = await StateAsync(response, HttpStatusCode.Created);
        Assert.Equal("CHANGES_REQUESTED", returned.Body.GetProperty("status").GetString());
        Assert.Equal(_author, returned.Body.GetProperty("assigneeId").GetGuid());   // người phụ trách tiếp theo (FR-CMS-010)

        using (var revision = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Get, $"/api/v1/admin/articles/{returned.Id}/revisions/{returned.WorkingRevisionId}", _author, StaffRoles.Author)))
        {
            var rev = await NewsHttp.JsonAsync(revision, HttpStatusCode.OK);
            Assert.Equal("CHANGES_REQUESTED", rev.GetProperty("status").GetString());
            Assert.Equal("Thiếu nguồn ảnh minh họa", rev.GetProperty("reviewNote").GetString());
            Assert.Equal(_editor, rev.GetProperty("reviewedBy").GetGuid());
        }

        // Tác giả sửa → revision mới (bản bị trả về giữ nguyên làm lịch sử), bài về DRAFT, gửi lại được.
        using var patch = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/articles/{returned.Id}", _author, StaffRoles.Author,
            json: """{"title":"Đã bổ sung nguồn","changeReason":"Theo yêu cầu biên tập"}""", ifMatch: returned.ETag));
        var edited = await StateAsync(patch, HttpStatusCode.OK);
        Assert.NotEqual(returned.WorkingRevisionId, edited.WorkingRevisionId);
        Assert.Equal("DRAFT", edited.Body.GetProperty("status").GetString());
        Assert.Equal("IN_REVIEW", (await SubmitAsync(cms, edited)).Body.GetProperty("status").GetString());
    }

    [DbFact]
    public async Task Reject_requires_approve_permission_and_is_final_for_that_revision()
    {
        using var cms = Cms();
        var submitted = await SubmitAsync(cms, await CreateAsync(cms));
        const string reason = """{"reason":"Nội dung sai sự thật"}""";

        using (var editor = await cms.SendAsync(Action("reject", submitted, _editor, StaffRoles.Editor, mfa: true, reason)))
        {
            await NewsHttp.ProblemAsync(editor, HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        }

        using var response = await cms.SendAsync(Action("reject", submitted, _approver, StaffRoles.Approver, mfa: true, reason));
        var rejected = await StateAsync(response, HttpStatusCode.OK);
        Assert.Equal("REJECTED", rejected.Body.GetProperty("status").GetString());

        using var approve = await cms.SendAsync(Action("approve", rejected, _approver, StaffRoles.Approver, mfa: true,
            $$"""{"revisionId":"{{rejected.WorkingRevisionId}}"}"""));
        await NewsHttp.ProblemAsync(approve, HttpStatusCode.Conflict, ErrorCodes.StateConflict);
    }

    [DbFact]
    public async Task Approver_who_edited_the_revision_cannot_approve_it()
    {
        using var cms = Cms();
        var created = await CreateAsync(cms);
        // Biên tập viên kiêm người duyệt sửa bản nháp của tác giả → thành người đóng góp của revision.
        using var patch = await cms.SendAsync(NewsHttp.Admin(HttpMethod.Patch, $"/api/v1/admin/articles/{created.Id}", _editor, $"{StaffRoles.Editor},{StaffRoles.Approver}",
            json: """{"summary":"Biên tập thêm sapo"}""", ifMatch: created.ETag));
        var edited = await StateAsync(patch, HttpStatusCode.OK);
        var submitted = await SubmitAsync(cms, edited);

        using var approve = await cms.SendAsync(Action("approve", submitted, _editor, $"{StaffRoles.Editor},{StaffRoles.Approver}", mfa: true,
            $$"""{"revisionId":"{{submitted.WorkingRevisionId}}"}"""));
        await NewsHttp.ProblemAsync(approve, HttpStatusCode.Forbidden, ErrorCodes.SodViolation);
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Tinhte.Api.Tests.Support;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Security;

namespace Tinhte.Api.Tests;

/// <summary>Danh mục nguồn nội dung và giấy phép (ADR-0007 §1, FR-NEWS-006): CRUD không xóa, If-Match, quyền taxonomy.manage / licenses.manage (MFA).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class CatalogDbTests(MigratedDatabaseFixture db, WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private readonly Guid _editor = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();

    private HttpClient Cms() => factory.WithProbes(db.ConnectionString).Client(TestHost.Cms);

    private static async Task<(JsonElement Body, string ETag)> CallAsync(HttpClient cms, HttpRequestMessage request, HttpStatusCode expected)
    {
        using var response = await cms.SendAsync(request);
        var body = await NewsHttp.JsonAsync(response, expected);
        return (body, response.Headers.ETag?.ToString() ?? "");
    }

    private static async Task ProblemAsync(HttpClient cms, HttpRequestMessage request, HttpStatusCode status, string code)
    {
        using var response = await cms.SendAsync(request);
        await NewsHttp.ProblemAsync(response, status, code);
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, Guid user, string roles, bool mfa = false, string? json = null, string? ifMatch = null) =>
        NewsHttp.Admin(method, "/api/v1/admin" + path, user, roles, mfa, json, ifMatch, method == HttpMethod.Post ? "cat-" + Guid.NewGuid() : null);

    [DbFact]
    public async Task Content_sources_are_created_listed_and_disabled_never_deleted()
    {
        using var cms = Cms();
        var (created, etag) = await CallAsync(cms, Req(HttpMethod.Post, "/sources", _editor, StaffRoles.Editor,
            json: $$"""{"name":"  Báo Mẫu {{_tag}} ","kind":"AGENCY","url":"https://bao-mau.example.org"}"""), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(($"Báo Mẫu {_tag}", "AGENCY", true, "W/\"1\""), (created.GetProperty("name").GetString(), created.GetProperty("kind").GetString(),
            created.GetProperty("isEnabled").GetBoolean(), etag));

        await ProblemAsync(cms, Req(HttpMethod.Post, "/sources", _editor, StaffRoles.Editor, json: $$"""{"name":"BÁO MẪU {{_tag}}","kind":"OTHER"}"""),
            HttpStatusCode.Conflict, ErrorCodes.StateConflict);   // tên trùng, không phân biệt hoa thường
        foreach (var bad in new[] { """{"name":"X","kind":"BLOG"}""", """{"name":" ","kind":"OTHER"}""", """{"name":"Y","kind":"OTHER","url":"ftp://x"}""",
                     """{"name":"Z","kind":"OTHER","owner":"me"}""" })
        {
            await ProblemAsync(cms, Req(HttpMethod.Post, "/sources", _editor, StaffRoles.Editor, json: bad), HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        }

        await ProblemAsync(cms, Req(HttpMethod.Post, "/sources", Guid.NewGuid(), StaffRoles.Author, json: """{"name":"A","kind":"OTHER"}"""),
            HttpStatusCode.Forbidden, ErrorCodes.Forbidden);

        var (list, _) = await CallAsync(cms, Req(HttpMethod.Get, $"/sources?q={_tag}", Guid.NewGuid(), StaffRoles.Author), HttpStatusCode.OK);   // content.read
        Assert.Equal(id, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var path = $"/sources/{id}";
        await ProblemAsync(cms, Req(HttpMethod.Patch, path, _editor, StaffRoles.Editor, json: """{"isEnabled":false}"""), HttpStatusCode.PreconditionRequired,
            ErrorCodes.PreconditionRequired);
        await ProblemAsync(cms, Req(HttpMethod.Patch, path, _editor, StaffRoles.Editor, json: """{"isEnabled":false}""", ifMatch: "W/\"7\""),
            HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict);
        await ProblemAsync(cms, Req(HttpMethod.Patch, $"/sources/{Guid.NewGuid()}", _editor, StaffRoles.Editor, json: """{"isEnabled":false}""", ifMatch: "W/\"1\""),
            HttpStatusCode.NotFound, ErrorCodes.NotFound);
        var (patched, etag2) = await CallAsync(cms, Req(HttpMethod.Patch, path, _editor, StaffRoles.Editor, json: """{"isEnabled":false,"url":null}""", ifMatch: etag),
            HttpStatusCode.OK);
        Assert.Equal((false, JsonValueKind.Null, "W/\"2\""), (patched.GetProperty("isEnabled").GetBoolean(), patched.GetProperty("url").ValueKind, etag2));

        var (enabledOnly, _) = await CallAsync(cms, Req(HttpMethod.Get, $"/sources?q={_tag}&isEnabled=true", _editor, StaffRoles.Editor), HttpStatusCode.OK);
        Assert.Empty(enabledOnly.GetProperty("items").EnumerateArray());
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM tt.content_sources WHERE id = '{id}'"));
    }

    [DbFact]
    public async Task Licences_need_licenses_manage_with_mfa_evidence_and_a_valid_window()
    {
        using var cms = Cms();
        var code = $"LIC-{_tag.ToUpperInvariant()}";
        var json = $$"""{"code":"{{code}}","name":"Thỏa thuận trích dẫn","usageScope":"Dùng dữ kiện, ghi nguồn","evidenceRef":"dms://hd/{{_tag}}"}""";
        await ProblemAsync(cms, Req(HttpMethod.Post, "/licenses", _approver, StaffRoles.Approver, mfa: false, json: json), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        await ProblemAsync(cms, Req(HttpMethod.Post, "/licenses", _editor, StaffRoles.Editor, mfa: true, json: json), HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
        foreach (var bad in new[]
        {
            $$"""{"code":"{{code}}","name":"N","usageScope":"S"}""",                                                    // thiếu bằng chứng
            $$"""{"code":"lic-lower","name":"N","usageScope":"S","evidenceRef":"dms://x"}""",                            // mã sai mẫu
            $$"""{"code":"{{code}}X","name":"N","usageScope":"S","evidenceRef":"dms://x","validFrom":"2026-12-01T00:00:00Z","validTo":"2026-11-01T00:00:00Z"}""",
        })
        {
            await ProblemAsync(cms, Req(HttpMethod.Post, "/licenses", _approver, StaffRoles.Approver, mfa: true, json: bad), HttpStatusCode.UnprocessableEntity,
                ErrorCodes.ValidationFailed);
        }

        var (created, etag) = await CallAsync(cms, Req(HttpMethod.Post, "/licenses", _approver, StaffRoles.Approver, mfa: true, json: json), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal((code, true, true, true), (created.GetProperty("code").GetString(), created.GetProperty("requiresAttribution").GetBoolean(),
            created.GetProperty("isEnabled").GetBoolean(), created.GetProperty("active").GetBoolean()));
        await ProblemAsync(cms, Req(HttpMethod.Post, "/licenses", _approver, StaffRoles.Approver, mfa: true, json: json), HttpStatusCode.Conflict, ErrorCodes.StateConflict);

        var path = $"/licenses/{id}";
        await ProblemAsync(cms, Req(HttpMethod.Patch, path, _approver, StaffRoles.Approver, mfa: true, json: """{"code":"OTHER"}""", ifMatch: etag),
            HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);
        var (expired, etag2) = await CallAsync(cms, Req(HttpMethod.Patch, path, _approver, StaffRoles.Approver, mfa: true,
            json: """{"validFrom":"2020-01-01T00:00:00Z","validTo":"2021-01-01T00:00:00Z"}""", ifMatch: etag), HttpStatusCode.OK);
        Assert.Equal((false, "W/\"2\""), (expired.GetProperty("active").GetBoolean(), etag2));   // hết hạn → không dùng được cho M2/M3
        await ProblemAsync(cms, Req(HttpMethod.Patch, path, _approver, StaffRoles.Approver, mfa: true, json: """{"validFrom":"2022-01-01T00:00:00Z"}""", ifMatch: etag2),
            HttpStatusCode.UnprocessableEntity, ErrorCodes.ValidationFailed);   // validTo (2021) phải sau validFrom
        var (cleared, _) = await CallAsync(cms, Req(HttpMethod.Patch, path, _approver, StaffRoles.Approver, mfa: true, json: """{"validTo":null,"validFrom":null}""",
            ifMatch: etag2), HttpStatusCode.OK);
        Assert.True(cleared.GetProperty("active").GetBoolean());

        var (list, _) = await CallAsync(cms, Req(HttpMethod.Get, $"/licenses?q={code}", _editor, StaffRoles.Editor), HttpStatusCode.OK);   // content.read
        Assert.Equal(id, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }
}

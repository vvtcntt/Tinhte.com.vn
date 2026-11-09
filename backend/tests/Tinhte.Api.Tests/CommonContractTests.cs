using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Tinhte.Api.Tests.Support;
using Tinhte.Platform.Data;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Mvc;

namespace Tinhte.Api.Tests;

/// <summary>WBS-014: hợp đồng chung — phân trang cursor, ETag/If-Match, lỗi nghiệp vụ/DB, idempotency (phần không cần DB).</summary>
public sealed class CommonContractTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Guid NoteId = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private HttpClient Client() => factory.WithProbes().Client();

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private static HmacCursorCodec Codec(string? key = null, string environment = "Development")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [HmacCursorCodec.SigningKeyConfig] = key }).Build();
        return new HmacCursorCodec(config, new FakeEnvironment(environment), NullLogger<HmacCursorCodec>.Instance);
    }

    // ---------------------------------------------------------------- phân trang

    [Fact]
    public async Task Cursor_pagination_walks_every_item_once_in_order()
    {
        using var client = Client();
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/api/v1/_contract/items?limit=10" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await client.GetFromJsonAsync<JsonElement>(url);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(ContractProbeController.Items.Select(i => i.Id), seen);
    }

    [Fact]
    public async Task Default_limit_is_20_and_last_page_has_null_cursor()
    {
        using var client = Client();
        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/_contract/items");
        Assert.Equal(PageRequest.DefaultLimit, page.GetProperty("items").GetArrayLength());
        var last = await client.GetFromJsonAsync<JsonElement>("/api/v1/_contract/items?limit=100");
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=abc")]
    public async Task Out_of_range_query_parameter_is_400_bad_request(string query)
    {
        using var client = Client();
        using var response = await client.GetAsync("/api/v1/_contract/items?" + query);
        await ProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
    }

    [Fact]
    public async Task Tampered_or_foreign_cursor_is_400_invalid_cursor()
    {
        using var client = Client();
        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/_contract/items?limit=5");
        var cursor = page.GetProperty("nextCursor").GetString()!;

        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? "B" : "A") + cursor[^1];
        foreach (var bad in new[] { tampered, "garbage", cursor.Split('.')[0], new string('x', 300) })
        {
            using var r = await client.GetAsync("/api/v1/_contract/items?cursor=" + Uri.EscapeDataString(bad));
            await ProblemAsync(r, HttpStatusCode.BadRequest, ErrorCodes.InvalidCursor);
        }

        // Cursor hợp lệ của danh sách khác → không dùng được ở đây.
        using var foreign = await client.GetAsync("/api/v1/_contract/other-items?cursor=" + Uri.EscapeDataString(cursor));
        await ProblemAsync(foreign, HttpStatusCode.BadRequest, ErrorCodes.InvalidCursor);
    }

    [Fact]
    public void Cursor_is_bound_to_scope_and_signing_key()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var codec = Codec(key);
        var cursor = codec.Encode("www:articles", new { Id = 7 });

        Assert.Equal(7, codec.Decode<JsonElement>("www:articles", cursor).GetProperty("id").GetInt32());
        Assert.Equal(ErrorCodes.InvalidCursor, Assert.Throws<DomainProblemException>(() => codec.Decode<JsonElement>("cms:articles", cursor)).Code);
        var otherKey = Codec(Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()));
        Assert.Equal(ErrorCodes.InvalidCursor, Assert.Throws<DomainProblemException>(() => otherKey.Decode<JsonElement>("www:articles", cursor)).Code);
        Assert.NotEqual(CursorPaging.Scope("www", "articles", "category=a"), CursorPaging.Scope("www", "articles", "category=b"));
    }

    [Fact]
    public void Cursor_signing_key_is_required_outside_development()
    {
        Assert.Throws<InvalidOperationException>(() => Codec(null, "Production"));
        Assert.Throws<InvalidOperationException>(() => Codec(Convert.ToBase64String(new byte[16]), "Production"));
        _ = Codec(Convert.ToBase64String(new byte[32]), "Production");
    }

    // ---------------------------------------------------------------- ETag / If-Match

    [Fact]
    public async Task Get_returns_weak_etag_from_row_version()
    {
        using var client = Client();
        using var response = await client.GetAsync($"/api/v1/_contract/notes/{NoteId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("W/\"3\"", response.Headers.ETag?.ToString());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.PreconditionRequired, ErrorCodes.PreconditionRequired)]
    [InlineData("W/\"2\"", HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict)]
    [InlineData("*", HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict)]
    [InlineData("W/\"3\", W/\"4\"", HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict)]
    [InlineData("not-an-etag", HttpStatusCode.PreconditionFailed, ErrorCodes.RevisionConflict)]
    public async Task Patch_requires_matching_if_match(string? ifMatch, HttpStatusCode status, string code)
    {
        using var client = Client();
        using var response = await client.SendAsync(TestHost.Write(HttpMethod.Patch, $"/api/v1/_contract/notes/{NoteId}", """{"text":"x"}""", ifMatch: ifMatch));
        await ProblemAsync(response, status, code);
    }

    [Theory]
    [InlineData("W/\"3\"")]
    [InlineData("\"3\"")]
    public async Task Patch_with_current_etag_succeeds_and_returns_next_etag(string ifMatch)
    {
        using var client = Client();
        using var response = await client.SendAsync(TestHost.Write(HttpMethod.Patch, $"/api/v1/_contract/notes/{NoteId}", """{"text":"x"}""", ifMatch: ifMatch));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("W/\"4\"", response.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Missing_resource_is_404_before_if_match_is_checked()
    {
        using var client = Client();
        using var response = await client.SendAsync(TestHost.Write(HttpMethod.Patch, $"/api/v1/_contract/notes/{Guid.NewGuid()}", """{"text":"x"}"""));
        await ProblemAsync(response, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    // ---------------------------------------------------------------- lỗi nghiệp vụ / DB

    [Theory]
    [InlineData(ErrorCodes.StateConflict, HttpStatusCode.Conflict)]
    [InlineData(ErrorCodes.ContentWithdrawn, HttpStatusCode.Gone)]
    [InlineData(ErrorCodes.SponsorshipExpired, HttpStatusCode.UnprocessableEntity)]
    [InlineData(ErrorCodes.DependencyUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Domain_problem_exception_uses_catalog_status(string code, HttpStatusCode status)
    {
        using var client = Client();
        using var response = await client.GetAsync("/api/v1/_contract/domain-error?code=" + code);
        var body = await ProblemAsync(response, status, code);
        Assert.Equal("probe detail", body.GetProperty("detail").GetString());
    }

    [Fact]
    public void Unknown_error_code_is_rejected_at_throw_site()
    {
        Assert.Throws<ArgumentException>(() => new DomainProblemException("NOT_IN_CATALOG", "x"));
    }

    [Theory]
    [InlineData("P0001", "FEATURE_DISABLED", ErrorCodes.FeatureDisabled)]
    [InlineData("P0001", "SPONSORSHIP_LABEL_REQUIRED", ErrorCodes.SponsorshipLabelRequired)]
    [InlineData("P0001", "STATE_CONFLICT", ErrorCodes.StateConflict)]
    [InlineData("23514", "new row violates check constraint", ErrorCodes.ValidationFailed)]
    [InlineData("23505", "duplicate key", ErrorCodes.ValidationFailed)]
    [InlineData("23503", "fk", ErrorCodes.ValidationFailed)]
    [InlineData("22001", "value too long", ErrorCodes.ValidationFailed)]
    public void Postgres_errors_translate_to_catalog_codes(string sqlState, string message, string expected)
    {
        var translated = PostgresErrorTranslator.Translate(new PostgresException(message, "ERROR", "ERROR", sqlState));
        Assert.Equal(expected, translated?.Code);
    }

    [Theory]
    [InlineData("P0001", "APPEND_ONLY")]   // sửa/xóa bằng chứng: không thuộc catalog → 500 + cảnh báo bảo mật
    [InlineData("P0001", "SOMETHING_ELSE")]
    [InlineData("40001", "serialization failure")]
    [InlineData("42P01", "relation does not exist")]
    public void Postgres_errors_outside_the_contract_are_not_translated(string sqlState, string message)
    {
        Assert.Null(PostgresErrorTranslator.Translate(new PostgresException(message, "ERROR", "ERROR", sqlState)));
    }

    // ---------------------------------------------------------------- idempotency (không cần DB)

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task Idempotent_post_requires_a_valid_key(string? key)
    {
        using var client = Client();
        using var response = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"hi"}""", user: "u1", idempotencyKey: key));
        await ProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.BadRequest);
    }

    [Fact]
    public async Task Idempotent_post_requires_an_authenticated_actor()
    {
        using var client = Client();
        using var response = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"hi"}""", idempotencyKey: "key-00000001"));
        await ProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task Idempotent_post_without_database_is_503()
    {
        using var client = Client();
        using var response = await client.SendAsync(TestHost.Write(HttpMethod.Post, "/api/v1/_contract/notes", """{"text":"hi"}""", user: "u1", idempotencyKey: "key-00000001"));
        await ProblemAsync(response, HttpStatusCode.ServiceUnavailable, ErrorCodes.DependencyUnavailable);
    }

    [Fact]
    public void Canonical_json_ignores_key_order_and_whitespace_but_not_values()
    {
        static string C(string json) => Encoding.UTF8.GetString(RequestHasher.CanonicalJson(Encoding.UTF8.GetBytes(json))!);
        Assert.Equal(C("""{"b":1,"a":{"y":[1,{"d":2,"c":3}],"x":null}}"""), C("""{ "a": { "x": null, "y": [1, {"c":3,"d":2}] }, "b": 1 }"""));
        Assert.NotEqual(C("""{"a":[1,2]}"""), C("""{"a":[2,1]}"""));
        Assert.NotEqual(C("""{"a":"1"}"""), C("""{"a":1}"""));
        Assert.Null(RequestHasher.CanonicalJson(Encoding.UTF8.GetBytes("{not json")));
    }

    // ---------------------------------------------------------------- readiness

    [Fact]
    public async Task Ready_without_database_is_ok_only_in_development()
    {
        using var client = Client();
        var body = await client.GetFromJsonAsync<JsonElement>("/health/ready");
        Assert.Equal("not_configured", body.GetProperty("checks").GetProperty("database").GetString());
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

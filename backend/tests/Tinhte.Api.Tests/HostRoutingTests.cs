using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Tinhte.Api.Tests;

/// <summary>ADR-0001 D2–D5 qua pipeline thật (WebApplicationFactory).</summary>
public sealed class HostRoutingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client(string siteEnv = "local") =>
        factory.WithWebHostBuilder(b => b.UseSetting("Sites:Environment", siteEnv))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpRequestMessage Request(HttpMethod method, string host, string path)
    {
        var request = new HttpRequestMessage(method, $"http://{host}{path}");
        request.Headers.Host = host;
        return request;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Site_config_is_served_per_site_with_all_public_flags_off()
    {
        using var response = await Client().SendAsync(Request(HttpMethod.Get, "www.tinhte.localhost:3000", "/api/v1/site/config"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("www", response.Headers.GetValues("X-TT-Site").Single());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        foreach (var flag in new[] { "videoCommentsEnabled", "memberLoginEnabled", "sponsoredContentEnabled", "adSlotsEnabled", "shortsInstreamAdsEnabled" })
        {
            Assert.False(body.GetProperty(flag).GetBoolean(), flag);
        }
    }

    [Fact]
    public async Task Apex_alias_redirects_308_to_canonical_host_in_production()
    {
        using var response = await Client("production").SendAsync(Request(HttpMethod.Get, "tinhte.com.vn", "/api/v1/site/config?x=1"));
        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("https://www.tinhte.com.vn/api/v1/site/config?x=1", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Unknown_host_is_rejected_with_400_problem()
    {
        using var response = await Client("production").SendAsync(Request(HttpMethod.Get, "evil.example", "/api/v1/site/config"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BAD_REQUEST", (await ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Planned_or_other_environment_hosts_do_not_resolve_in_production()
    {
        using var response = await Client("production").SendAsync(Request(HttpMethod.Get, "www.staging.tinhte.com.vn", "/api/v1/site/config"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_surface_is_only_served_on_cms_site()
    {
        using var onWww = await Client().SendAsync(Request(HttpMethod.Get, "www.tinhte.localhost", "/api/v1/admin/articles"));
        Assert.Equal(HttpStatusCode.NotFound, onWww.StatusCode);
        var problem = await ProblemAsync(onWww);
        Assert.Equal("NOT_FOUND", problem.GetProperty("code").GetString());
        Assert.Contains("'admin' is not served on site 'www'", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        using var onCms = await Client().SendAsync(Request(HttpMethod.Get, "cms.tinhte.localhost", "/api/v1/admin/articles"));
        Assert.Equal("cms", onCms.Headers.GetValues("X-TT-Site").Single());          // qua được cổng site; endpoint chưa làm → 404 của routing
        Assert.NotEqual("application/problem+json", onCms.Content.Headers.ContentType?.MediaType);

        using var publicOnCms = await Client().SendAsync(Request(HttpMethod.Get, "cms.tinhte.localhost", "/api/v1/site/config"));
        Assert.Equal(HttpStatusCode.NotFound, publicOnCms.StatusCode);                 // cms chỉ có bề mặt admin
    }

    [Fact]
    public async Task Media_site_exposes_no_api()
    {
        using var response = await Client().SendAsync(Request(HttpMethod.Get, "media.tinhte.localhost", "/api/v1/site/config"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cross_site_origin_on_mutation_is_rejected()
    {
        var request = Request(HttpMethod.Post, "www.tinhte.localhost", "/api/v1/analytics/events");
        request.Headers.Add("Origin", "http://cms.tinhte.localhost:8080");
        using var response = await Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("CSRF_INVALID", (await ProblemAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Same_site_origin_passes_the_guard()
    {
        var request = Request(HttpMethod.Post, "www.tinhte.localhost", "/api/v1/analytics/events");
        request.Headers.Add("Origin", "http://www.tinhte.localhost:8080");
        using var response = await Client().SendAsync(request);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Cookie_mutation_without_origin_is_rejected()
    {
        var request = Request(HttpMethod.Post, "www.tinhte.localhost", "/api/v1/analytics/events");
        request.Headers.Add("Cookie", "__Host-tt_member_session=abc");
        using var response = await Client().SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_bypass_host_resolution()
    {
        using var response = await Client("production").SendAsync(Request(HttpMethod.Get, "10.0.0.5", "/health/live"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tinhte.Platform.Modules;
using Tinhte.Platform.Mvc;

namespace Tinhte.Api.Tests;

/// <summary>Controller chỉ dùng trong test để kiểm hành vi chung của MVC (validation, exception).</summary>
[Route(ApiRoutes.Prefix + "/_test")]
public sealed class ConventionProbeController : TinhteControllerBase
{
    // Record positional: attribute validation đặt trên tham số (MVC từ chối attribute "property:" trên record).
    public sealed record ProbeRequest([Required, StringLength(10, MinimumLength = 3)] string Name, [Range(1, 5)] int Rank);

    [HttpPost("echo")]
    public ActionResult<ProbeRequest> Echo(ProbeRequest request) => Ok(request);

    [HttpGet("boom")]
    public IActionResult Boom() => throw new InvalidOperationException("secret stack detail");

    [HttpGet("conflict")]
    public IActionResult ConflictProbe() => Problem("STATE_CONFLICT", "Conflict", "Transition not allowed");
}

/// <summary>ADR-0002: Web API dạng Controller — quy ước + hành vi lỗi theo hợp đồng.</summary>
public sealed class ControllerConventionTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client() => factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(ConventionProbeController).Assembly)))
        .CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://www.tinhte.localhost"), AllowAutoRedirect = false });

    private static HttpRequestMessage Post(string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", "http://www.tinhte.localhost:8080");
        return request;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        return body;
    }

    public static IEnumerable<Type> ProductionControllers() =>
        new[] { typeof(Program).Assembly }
            .Concat(typeof(Program).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Tinhte.", StringComparison.Ordinal)).Select(Assembly.Load))
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

    [Fact]
    public void Controllers_follow_conventions()
    {
        var controllers = ProductionControllers().ToList();
        Assert.NotEmpty(controllers);
        foreach (var c in controllers)
        {
            Assert.True(c.IsSubclassOf(typeof(TinhteControllerBase)), $"{c.Name} must inherit TinhteControllerBase");
            Assert.True(c.IsSealed, $"{c.Name} must be sealed");
            Assert.EndsWith("Controller", c.Name, StringComparison.Ordinal);
            Assert.EndsWith(".Controllers", c.Namespace, StringComparison.Ordinal);
            var route = c.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
            Assert.True(route.StartsWith(ApiRoutes.Prefix + "/", StringComparison.Ordinal) || route == "health", $"{c.Name} route '{route}'");
        }
    }

    [Fact]
    public void Module_controllers_are_registered_as_application_parts()
    {
        // Mọi assembly module phải nằm trong ApplicationParts để controller của module được phát hiện.
        var partManager = factory.Services.GetRequiredService<Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartManager>();
        var parts = partManager.ApplicationParts.Select(p => p.Name).ToHashSet();
        foreach (var module in factory.Services.GetServices<IModule>())
        {
            Assert.Contains(module.GetType().Assembly.GetName().Name!, parts);
        }
    }

    [Fact]
    public async Task Malformed_json_returns_400_bad_request()
    {
        using var response = await Client().SendAsync(Post("/api/v1/_test/echo", "{ not json"));
        await ProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Constraint_violation_returns_422_with_field_errors()
    {
        using var response = await Client().SendAsync(Post("/api/v1/_test/echo", """{"name":"x","rank":9}"""));
        var body = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var errors = body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Name", out _) || errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("Rank", out _) || errors.TryGetProperty("rank", out _));
    }

    [Fact]
    public async Task Valid_request_reaches_the_action()
    {
        using var response = await Client().SendAsync(Post("/api/v1/_test/echo", """{"name":"tinhte","rank":3}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tinhte", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Business_problem_uses_catalog_status()
    {
        using var response = await Client().GetAsync("/api/v1/_test/conflict");
        await ProblemAsync(response, HttpStatusCode.Conflict, "STATE_CONFLICT");
    }

    [Fact]
    public async Task Unhandled_exception_returns_500_without_leaking_details()
    {
        using var response = await Client().GetAsync("/api/v1/_test/boom");
        await ProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        Assert.DoesNotContain("secret stack detail", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApi_document_is_available_in_development()
    {
        using var response = await Client().GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/v1/site/config", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

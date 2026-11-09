using System.Text.Json;
using Tinhte.Platform.Sites;

namespace Tinhte.Api.Tests;

public sealed class SiteRegistryTests
{
    private static SiteRegistryDocument LoadDocument() =>
        JsonSerializer.Deserialize<SiteRegistryDocument>(File.ReadAllText(RepoPaths.File("config", "sites", "sites.json")), SiteRegistry.JsonOptions)!;

    [Fact]
    public void Real_registry_is_valid_in_every_environment()
    {
        var document = LoadDocument();
        Assert.Empty(SiteRegistry.Validate(document));
        foreach (var env in new[] { "production", "staging", "local" })
        {
            Assert.NotEmpty(SiteRegistry.Create(document, env).ResolvableSites);
        }
    }

    public static TheoryData<string, string, string, string?, string?> SharedCases()
    {
        // Cùng bộ ca với @tinhte/sites (TypeScript) → hai bản cài đặt không thể lệch nhau.
        var data = new TheoryData<string, string, string, string?, string?>();
        using var json = JsonDocument.Parse(File.ReadAllText(RepoPaths.File("config", "sites", "resolution-cases.json")));
        foreach (var c in json.RootElement.EnumerateArray())
        {
            var expect = c.GetProperty("expect");
            data.Add(
                c.GetProperty("env").GetString()!,
                c.GetProperty("host").GetString()!,
                expect.GetProperty("type").GetString()!,
                expect.TryGetProperty("site", out var s) ? s.GetString() : null,
                expect.TryGetProperty("canonicalHost", out var h) ? h.GetString() : null);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    public void Resolution_matches_shared_cases(string env, string host, string type, string? site, string? canonicalHost)
    {
        var match = SiteRegistry.Create(LoadDocument(), env).Resolve(host);
        if (type == "unknown")
        {
            Assert.Null(match);
            return;
        }

        Assert.NotNull(match);
        Assert.Equal(site, match.Site.Key);
        Assert.Equal(type == "alias", match.IsAlias);
        if (canonicalHost is not null) Assert.Equal(canonicalHost, match.CanonicalHost);
    }

    [Fact]
    public void Adding_a_site_only_requires_a_registry_entry()
    {
        var document = LoadDocument();
        var game = document.Sites[0] with { Key = "game", Label = "Game", Subdomain = "game", Aliases = [], Status = "active", Scope = "P1" };
        var registry = SiteRegistry.Create(document with { Sites = [.. document.Sites, game] }, "production");
        var match = registry.Resolve("game.tinhte.com.vn");
        Assert.Equal("game", match?.Site.Key);
        Assert.Equal("https://game.tinhte.com.vn", registry.OriginFor(game));
    }

    [Fact]
    public void Invalid_registries_fail_fast()
    {
        var document = LoadDocument();
        var www = document.Sites[0];
        var duplicateLabel = document with { Sites = [.. document.Sites, www with { Key = "www2" }] };
        Assert.Contains(SiteRegistry.Validate(duplicateLabel), e => e.Contains("already used by site \"www\"", StringComparison.Ordinal));

        var adminOnPublic = document with { Sites = [.. document.Sites, www with { Key = "game", Subdomain = "game", Aliases = [], ApiSurfaces = ["public", "admin"] }] };
        Assert.Contains(SiteRegistry.Validate(adminOnPublic), e => e.Contains("admin api surface is only allowed on cms sites", StringComparison.Ordinal));

        var plannedInProduction = document with
        {
            Environments = new Dictionary<string, SiteEnvironment>(document.Environments)
            {
                ["production"] = document.Environments["production"] with { ResolveStatuses = ["active", "planned"] },
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => SiteRegistry.Create(plannedInProduction, "production"));
        Assert.Contains("only \"active\" sites may resolve", ex.Message, StringComparison.Ordinal);
    }
}

using System.Reflection;
using System.Text.RegularExpressions;
using Tinhte.Platform.Errors;
using Tinhte.Platform.Flags;
using Tinhte.Platform.Modules;

namespace Tinhte.Api.Tests;

/// <summary>Mã nguồn phải khớp hợp đồng OpenAPI v0.3 (x-error-catalog, x-feature-flags) và ranh giới module.</summary>
public sealed partial class ContractConsistencyTests
{
    private static readonly string Spec = File.ReadAllText(RepoPaths.File("doc", "07_AI_REVIEW", "v0.3", "07_OPENAPI_TINHTE_v0.3_DRAFT.yaml"));

    private static string Block(string key)
    {
        var start = Spec.IndexOf($"\n{key}:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{key} not found in spec");
        var end = Spec.IndexOf("\npaths:", start, StringComparison.Ordinal);
        var next = Spec.IndexOf("\nx-", start + 1, StringComparison.Ordinal);
        return Spec[start..(next > start && next < end ? next : end)];
    }

    [Fact]
    public void Error_catalog_matches_spec()
    {
        var spec = ErrorPattern().Matches(Block("x-error-catalog"))
            .ToDictionary(m => m.Groups["code"].Value, m => int.Parse(m.Groups["status"].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(27, spec.Count);
        Assert.Equal(spec.OrderBy(x => x.Key), ErrorCatalog.Statuses.OrderBy(x => x.Key));
    }

    [Fact]
    public void Feature_flag_keys_match_spec_registry()
    {
        var spec = FlagPattern().Matches(Block("x-feature-flags")).Select(m => m.Groups["key"].Value).Order().ToList();
        Assert.Equal(17, spec.Count);
        Assert.Equal(spec, FeatureFlagKeys.All.Order().ToList());
    }

    [Fact]
    public void Modules_reference_only_the_platform()
    {
        var moduleAssemblies = typeof(Program).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("Tinhte.Modules.", StringComparison.Ordinal))
            .Select(Assembly.Load)
            .ToList();
        Assert.Equal(5, moduleAssemblies.Count);
        foreach (var assembly in moduleAssemblies)
        {
            var illegal = assembly.GetReferencedAssemblies().Where(r => r.Name!.StartsWith("Tinhte.", StringComparison.Ordinal) && r.Name != "Tinhte.Platform");
            Assert.Empty(illegal);
            Assert.Single(assembly.GetTypes(), t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract);
        }
    }

    [GeneratedRegex(@"- code: (?<code>[A-Z_]+)\s+status: (?<status>\d{3})")]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"- key: (?<key>[a-z_.]+)")]
    private static partial Regex FlagPattern();
}

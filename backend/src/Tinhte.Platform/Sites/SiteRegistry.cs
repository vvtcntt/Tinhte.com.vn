using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tinhte.Platform.Sites;

/// <summary>
/// Site Registry (ADR-0001): nguồn sự thật duy nhất cho mọi subdomain. Luật phân giải phải khớp
/// @tinhte/sites (TypeScript) — cả hai cùng chạy bộ ca kiểm thử config/sites/resolution-cases.json.
/// </summary>
public sealed partial class SiteRegistry
{
    private static readonly string[] Environments = ["production", "staging", "local"];
    private static readonly string[] Kinds = ["public-web", "cms", "media", "api", "identity"];
    private static readonly string[] Apps = ["web", "cms", "api", "cdn", "identity"];
    private static readonly string[] Statuses = ["active", "planned", "reserved", "retired"];
    private static readonly string[] Scopes = ["P0", "P0*", "P0-ADS", "P1", "P2"];
    private static readonly string[] Sessions = ["none", "member", "staff"];
    private static readonly string[] Surfaces = [ApiSurfaces.Public, ApiSurfaces.Admin];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private SiteRegistry(SiteRegistryDocument document, string environmentName)
    {
        Document = document;
        EnvironmentName = environmentName;
        Environment = document.Environments[environmentName];
        ResolvableSites = document.Sites.Where(s => Environment.ResolveStatuses.Contains(s.Status)).ToList();
    }

    public SiteRegistryDocument Document { get; }

    public string EnvironmentName { get; }

    public SiteEnvironment Environment { get; }

    public IReadOnlyList<SiteDefinition> ResolvableSites { get; }

    public static SiteRegistry Load(string path, string environmentName)
    {
        var document = JsonSerializer.Deserialize<SiteRegistryDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Site registry '{path}' is empty.");
        return Create(document, environmentName);
    }

    public static SiteRegistry Create(SiteRegistryDocument document, string environmentName)
    {
        var errors = Validate(document);
        if (!Environments.Contains(environmentName))
        {
            errors.Add($"unknown site environment '{environmentName}' (expected production | staging | local)");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid site registry:\n - " + string.Join("\n - ", errors));
        }

        return new SiteRegistry(document, environmentName);
    }

    /// <summary>Ràng buộc chéo (giữ tương đương validateRegistry của @tinhte/sites).</summary>
    public static List<string> Validate(SiteRegistryDocument document)
    {
        var errors = new List<string>();
        if (document.Version != 1)
        {
            errors.Add("version must be 1");
        }

        foreach (var env in Environments)
        {
            if (!document.Environments.TryGetValue(env, out var e))
            {
                errors.Add($"missing environment \"{env}\"");
                continue;
            }

            if (env != "local" && e.Scheme != "https")
            {
                errors.Add($"environment \"{env}\": must use https");
            }
        }

        if (document.Environments.TryGetValue("production", out var prod) && prod.ResolveStatuses.Any(s => s != "active"))
        {
            errors.Add("environment \"production\": only \"active\" sites may resolve");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in document.Sites)
        {
            var at = $"site \"{s.Key}\"";
            if (!KeyPattern().IsMatch(s.Key)) errors.Add($"{at}: invalid key");
            if (!keys.Add(s.Key)) errors.Add($"{at}: duplicate key");
            if (!Kinds.Contains(s.Kind)) errors.Add($"{at}: invalid kind \"{s.Kind}\"");
            if (!Apps.Contains(s.App)) errors.Add($"{at}: invalid app \"{s.App}\"");
            if (!Statuses.Contains(s.Status)) errors.Add($"{at}: invalid status \"{s.Status}\"");
            if (!Scopes.Contains(s.Scope)) errors.Add($"{at}: invalid scope \"{s.Scope}\"");
            if (!Sessions.Contains(s.Session)) errors.Add($"{at}: invalid session \"{s.Session}\"");
            foreach (var surface in s.ApiSurfaces.Where(x => !Surfaces.Contains(x)))
            {
                errors.Add($"{at}: invalid api surface \"{surface}\"");
            }

            foreach (var label in s.Aliases.Prepend(s.Subdomain))
            {
                if (!LabelPattern().IsMatch(label)) errors.Add($"{at}: invalid host label \"{label}\"");
                if (labels.TryGetValue(label, out var owner)) errors.Add($"{at}: host label \"{label}\" already used by site \"{owner}\"");
                labels[label] = s.Key;
            }

            var isCms = s.Kind == "cms";
            if (isCms && (s.App != "cms" || s.Session != "staff" || s.Seo.Indexable || !s.ApiSurfaces.Contains(ApiSurfaces.Admin)))
            {
                errors.Add($"{at}: cms sites must use app cms, staff session, no indexing and the admin api surface");
            }

            if (!isCms && s.ApiSurfaces.Contains(ApiSurfaces.Admin)) errors.Add($"{at}: admin api surface is only allowed on cms sites");
            if (!isCms && s.Session == "staff") errors.Add($"{at}: staff session is only allowed on cms sites");
            if (s.Seo.Sitemap && !s.Seo.Indexable) errors.Add($"{at}: sitemap requires seo.indexable");
        }

        return errors;
    }

    public static string NormalizeHost(string? raw)
    {
        var host = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (host.StartsWith('['))
        {
            return host; // IPv6 literal — never a registry host
        }

        var colon = host.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0) host = host[..colon];
        return host.EndsWith('.') ? host[..^1] : host;
    }

    public string HostFor(SiteDefinition site, string? label = null)
    {
        label ??= site.Subdomain;
        return label == "@" ? Environment.DomainSuffix : $"{label}.{Environment.DomainSuffix}";
    }

    public string OriginFor(SiteDefinition site) =>
        $"{Environment.Scheme}://{HostFor(site)}{(Environment.PublicPort is int port ? $":{port}" : string.Empty)}";

    public SiteMatch? Resolve(string? rawHost)
    {
        var host = NormalizeHost(rawHost);
        if (host.Length == 0) return null;
        foreach (var site in ResolvableSites)
        {
            var canonical = HostFor(site);
            if (host == canonical) return new SiteMatch(site, false, canonical);
            if (site.Aliases.Any(a => HostFor(site, a) == host)) return new SiteMatch(site, true, canonical);
        }

        return null;
    }

    public SiteDefinition? Find(string key) => Document.Sites.FirstOrDefault(s => s.Key == key);

    [GeneratedRegex("^[a-z][a-z0-9-]{1,30}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^(@|[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)$")]
    private static partial Regex LabelPattern();
}

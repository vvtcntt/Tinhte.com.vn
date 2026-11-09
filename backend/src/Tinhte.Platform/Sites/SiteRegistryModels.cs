namespace Tinhte.Platform.Sites;

/// <summary>Một site (subdomain) trong config/sites/sites.json — ADR-0001.</summary>
public sealed record SiteDefinition(
    string Key,
    string Label,
    string Kind,
    string App,
    string Subdomain,
    IReadOnlyList<string> Aliases,
    string Status,
    string Scope,
    IReadOnlyList<string> ApiSurfaces,
    string Session,
    SiteSeo Seo,
    string Owner);

public sealed record SiteSeo(bool Indexable, bool Sitemap);

public sealed record SiteEnvironment(
    string DomainSuffix,
    string Scheme,
    int? PublicPort,
    bool Indexable,
    IReadOnlyList<string> ResolveStatuses);

public sealed record SiteRegistryDocument(
    int Version,
    IReadOnlyDictionary<string, SiteEnvironment> Environments,
    IReadOnlyList<SiteDefinition> Sites);

/// <summary>Kết quả phân giải Host: site chính tắc hoặc alias cần 308 về <see cref="CanonicalHost"/>.</summary>
public sealed record SiteMatch(SiteDefinition Site, bool IsAlias, string CanonicalHost);

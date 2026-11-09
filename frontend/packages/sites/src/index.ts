// @tinhte/sites — Site Registry runtime (ADR-0001).
// Nguồn sự thật duy nhất: config/sites/sites.json. KHÔNG viết cứng hostname ở bất kỳ đâu khác.
// Chỉ dùng cú pháp TypeScript "erasable" để Node ≥ 22.18 chạy trực tiếp (tools, test) và Next.js transpile.
import registryJson from '../../../../config/sites/sites.json' with { type: 'json' };

export type SiteEnv = 'production' | 'staging' | 'local';
export type SiteStatus = 'active' | 'planned' | 'reserved' | 'retired';
export type SiteKind = 'public-web' | 'cms' | 'media' | 'api' | 'identity';
export type SiteApp = 'web' | 'cms' | 'api' | 'cdn' | 'identity';
export type ApiSurface = 'public' | 'admin';
export type SessionKind = 'none' | 'member' | 'staff';

export interface SiteDefinition {
  key: string;
  label: string;
  kind: SiteKind;
  app: SiteApp;
  subdomain: string;
  aliases: string[];
  status: SiteStatus;
  scope: string;
  apiSurfaces: ApiSurface[];
  session: SessionKind;
  seo: { indexable: boolean; sitemap: boolean };
  owner: string;
}

export interface SiteEnvironment {
  domainSuffix: string;
  scheme: 'https' | 'http';
  publicPort: number | null;
  indexable: boolean;
  resolveStatuses: SiteStatus[];
}

export interface SiteRegistry {
  version: 1;
  environments: Record<SiteEnv, SiteEnvironment>;
  sites: SiteDefinition[];
}

export const registry = registryJson as unknown as SiteRegistry;

export const SITE_ENVS: readonly SiteEnv[] = ['production', 'staging', 'local'];
const KEY = /^[a-z][a-z0-9-]{1,30}$/;
const LABEL = /^(@|[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)$/;
const SUFFIX = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$/;
const ENUMS = {
  kind: ['public-web', 'cms', 'media', 'api', 'identity'],
  app: ['web', 'cms', 'api', 'cdn', 'identity'],
  status: ['active', 'planned', 'reserved', 'retired'],
  scope: ['P0', 'P0*', 'P0-ADS', 'P1', 'P2'],
  session: ['none', 'member', 'staff'],
  surface: ['public', 'admin'],
} as const;

/** Kiểm schema + ràng buộc chéo. Trả về danh sách lỗi (rỗng = hợp lệ). */
export function validateRegistry(r: SiteRegistry = registry): string[] {
  const errors: string[] = [];
  if (r.version !== 1) errors.push('version must be 1');
  for (const env of SITE_ENVS) {
    const e = r.environments?.[env];
    if (!e) { errors.push(`missing environment "${env}"`); continue; }
    if (!SUFFIX.test(e.domainSuffix)) errors.push(`environment "${env}": invalid domainSuffix "${e.domainSuffix}"`);
    if (e.scheme !== 'https' && e.scheme !== 'http') errors.push(`environment "${env}": invalid scheme`);
    if (env !== 'local' && e.scheme !== 'https') errors.push(`environment "${env}": must use https`);
    if (!Array.isArray(e.resolveStatuses) || e.resolveStatuses.length === 0) errors.push(`environment "${env}": resolveStatuses required`);
  }
  if (r.environments?.production?.resolveStatuses?.some((s) => s !== 'active')) {
    errors.push('environment "production": only "active" sites may resolve');
  }
  const keys = new Set<string>();
  const labels = new Map<string, string>();
  for (const s of r.sites ?? []) {
    const at = `site "${s.key}"`;
    if (!KEY.test(s.key)) errors.push(`${at}: key must match ${KEY}`);
    if (keys.has(s.key)) errors.push(`${at}: duplicate key`);
    keys.add(s.key);
    if (!(ENUMS.kind as readonly string[]).includes(s.kind)) errors.push(`${at}: invalid kind "${s.kind}"`);
    if (!(ENUMS.app as readonly string[]).includes(s.app)) errors.push(`${at}: invalid app "${s.app}"`);
    if (!(ENUMS.status as readonly string[]).includes(s.status)) errors.push(`${at}: invalid status "${s.status}"`);
    if (!(ENUMS.scope as readonly string[]).includes(s.scope)) errors.push(`${at}: invalid scope "${s.scope}"`);
    if (!(ENUMS.session as readonly string[]).includes(s.session)) errors.push(`${at}: invalid session "${s.session}"`);
    for (const surface of s.apiSurfaces ?? []) {
      if (!(ENUMS.surface as readonly string[]).includes(surface)) errors.push(`${at}: invalid api surface "${surface}"`);
    }
    for (const label of [s.subdomain, ...(s.aliases ?? [])]) {
      if (!LABEL.test(label)) errors.push(`${at}: invalid host label "${label}"`);
      const owner = labels.get(label);
      if (owner) errors.push(`${at}: host label "${label}" already used by site "${owner}"`);
      labels.set(label, s.key);
    }
    if (s.seo?.sitemap && !s.seo.indexable) errors.push(`${at}: sitemap requires seo.indexable`);
    if (s.session === 'staff' && s.kind !== 'cms') errors.push(`${at}: staff session is only allowed on cms sites`);
    if (s.kind === 'cms') {
      if (s.app !== 'cms') errors.push(`${at}: cms sites must be served by app "cms"`);
      if (s.session !== 'staff') errors.push(`${at}: cms sites must use staff session`);
      if (s.seo?.indexable) errors.push(`${at}: cms sites must not be indexable`);
      if (!s.apiSurfaces.includes('admin')) errors.push(`${at}: cms sites must expose the admin api surface`);
    }
    if (s.kind !== 'cms' && s.apiSurfaces.includes('admin')) errors.push(`${at}: admin api surface is only allowed on cms sites`);
    if (s.kind === 'public-web' && s.app !== 'web') errors.push(`${at}: public-web sites must be served by app "web"`);
    if (s.kind === 'media' && (s.session !== 'none' || s.apiSurfaces.length > 0)) errors.push(`${at}: media sites are cookieless and expose no api`);
  }
  return errors;
}

export function parseSiteEnv(value: string | undefined | null): SiteEnv {
  const v = (value ?? 'local').trim();
  if (v === 'production' || v === 'staging' || v === 'local') return v;
  throw new Error(`Invalid site environment "${v}" (expected production | staging | local)`);
}

export function normalizeHost(raw: string): string {
  let host = raw.trim().toLowerCase();
  if (host.startsWith('[')) return host;            // IPv6 literal: never a registry host
  const colon = host.indexOf(':');
  if (colon >= 0) host = host.slice(0, colon);
  return host.endsWith('.') ? host.slice(0, -1) : host;
}

export function hostFor(site: SiteDefinition, env: SiteEnv, label: string = site.subdomain, r: SiteRegistry = registry): string {
  const suffix = r.environments[env].domainSuffix;
  return label === '@' ? suffix : `${label}.${suffix}`;
}

export function originFor(site: SiteDefinition, env: SiteEnv, r: SiteRegistry = registry): string {
  const e = r.environments[env];
  return `${e.scheme}://${hostFor(site, env, site.subdomain, r)}${e.publicPort ? `:${e.publicPort}` : ''}`;
}

export function resolvableSites(env: SiteEnv, r: SiteRegistry = registry): SiteDefinition[] {
  const allowed = r.environments[env].resolveStatuses;
  return r.sites.filter((s) => allowed.includes(s.status));
}

export type HostResolution =
  | { type: 'site'; site: SiteDefinition; canonicalHost: string }
  | { type: 'alias'; site: SiteDefinition; canonicalHost: string }
  | { type: 'unknown' };

export function resolveHost(rawHost: string | null | undefined, env: SiteEnv, r: SiteRegistry = registry): HostResolution {
  const host = normalizeHost(rawHost ?? '');
  if (!host) return { type: 'unknown' };
  for (const site of resolvableSites(env, r)) {
    const canonicalHost = hostFor(site, env, site.subdomain, r);
    if (host === canonicalHost) return { type: 'site', site, canonicalHost };
    if (site.aliases.some((a) => hostFor(site, env, a, r) === host)) return { type: 'alias', site, canonicalHost };
  }
  return { type: 'unknown' };
}

export function isIndexable(site: SiteDefinition, env: SiteEnv, r: SiteRegistry = registry): boolean {
  return r.environments[env].indexable && site.seo.indexable;
}

/** Cookie phiên luôn host-only với tiền tố __Host- (ADR-0001 D4). */
export function sessionCookieName(site: SiteDefinition): string | null {
  if (site.session === 'member') return '__Host-tt_member_session';
  if (site.session === 'staff') return '__Host-tt_cms_session';
  return null;
}

export function mediaOrigin(env: SiteEnv, r: SiteRegistry = registry): string | null {
  const media = resolvableSites(env, r).find((s) => s.kind === 'media');
  return media ? originFor(media, env, r) : null;
}

export function securityHeaders(site: SiteDefinition, env: SiteEnv, r: SiteRegistry = registry): Record<string, string> {
  const media = mediaOrigin(env, r);
  const csp = [
    "default-src 'self'",
    `img-src 'self' data:${media ? ` ${media}` : ''}`,
    `media-src 'self' blob:${media ? ` ${media}` : ''}`,
    "connect-src 'self'",
    "script-src 'self'",
    "style-src 'self' 'unsafe-inline'",
    "font-src 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    site.kind === 'cms' ? "frame-ancestors 'none'" : "frame-ancestors 'self'",
  ].join('; ');
  const headers: Record<string, string> = {
    // Sprint 0: Report-Only. Chuyển sang enforce kèm nonce ở WBS-071 (hardening).
    'Content-Security-Policy-Report-Only': csp,
    'Referrer-Policy': 'strict-origin-when-cross-origin',
    'X-Content-Type-Options': 'nosniff',
    'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
    'X-TT-Site': site.key,
  };
  if (!isIndexable(site, env, r)) headers['X-Robots-Tag'] = 'noindex, nofollow';
  if (r.environments[env].scheme === 'https') headers['Strict-Transport-Security'] = 'max-age=31536000; includeSubDomains';
  return headers;
}

export type RouteDecision =
  | { action: 'rewrite'; site: SiteDefinition; pathname: string; headers: Record<string, string> }
  | { action: 'redirect'; status: 308; location: string }
  | { action: 'reject'; status: 400 | 404; reason: 'unknown-host' | 'site-not-served-by-app' };

/** Luật định tuyến dùng chung cho proxy.ts của web và cms (ADR-0001 D2). */
export function routeRequest(
  input: { host: string | null | undefined; pathname: string; search?: string; env: SiteEnv; app: SiteApp },
  r: SiteRegistry = registry,
): RouteDecision {
  const resolved = resolveHost(input.host, input.env, r);
  if (resolved.type === 'unknown') return { action: 'reject', status: 400, reason: 'unknown-host' };
  if (resolved.type === 'alias') {
    return { action: 'redirect', status: 308, location: `${originFor(resolved.site, input.env, r)}${input.pathname}${input.search ?? ''}` };
  }
  if (resolved.site.app !== input.app) return { action: 'reject', status: 404, reason: 'site-not-served-by-app' };
  const path = input.pathname === '/' ? '' : input.pathname;
  return { action: 'rewrite', site: resolved.site, pathname: `/${resolved.site.key}${path}`, headers: securityHeaders(resolved.site, input.env, r) };
}

export function findSite(key: string, r: SiteRegistry = registry): SiteDefinition | undefined {
  return r.sites.find((s) => s.key === key);
}

export function robotsTxt(site: SiteDefinition, env: SiteEnv, r: SiteRegistry = registry): string {
  if (!isIndexable(site, env, r)) return 'User-agent: *\nDisallow: /\n';
  const lines = ['User-agent: *', 'Allow: /'];
  if (site.seo.sitemap) lines.push(`Sitemap: ${originFor(site, env, r)}/sitemap.xml`);
  return `${lines.join('\n')}\n`;
}

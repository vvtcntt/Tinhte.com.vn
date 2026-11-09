// Kế hoạch hạ tầng suy ra từ Site Registry (ADR-0001 D10): TLS, DNS, edge route.
// IaC (Terraform/CDN) đọc JSON này thay vì tự liệt kê host.  Dùng: node tools/sites/plan.mjs <production|staging|local>
import { pathToFileURL } from 'node:url';
import { registry, resolvableSites, hostFor, parseSiteEnv } from '../../frontend/packages/sites/src/index.ts';

const UPSTREAM = { web: 'web', cms: 'cms', api: 'api', cdn: 'object-storage', identity: 'identity' };

export function plan(env, r = registry) {
  const e = r.environments[env];
  const sites = resolvableSites(env, r);
  const hosts = sites.flatMap((s) => [
    { host: hostFor(s, env, s.subdomain, r), site: s.key, role: 'canonical' },
    ...s.aliases.map((a) => ({ host: hostFor(s, env, a, r), site: s.key, role: 'alias-redirect-308' })),
  ]);
  return {
    environment: env,
    generatedFrom: 'config/sites/sites.json',
    tls: { certificates: [e.domainSuffix, `*.${e.domainSuffix}`] },
    dns: hosts.map((h) => ({ name: h.host, type: h.host === e.domainSuffix ? 'ALIAS' : 'CNAME', target: '<edge-endpoint>' })),
    edge: hosts.map((h) => {
      const s = sites.find((x) => x.key === h.site);
      if (h.role !== 'canonical') return { host: h.host, redirectTo: hostFor(s, env, s.subdomain, r), status: 308 };
      return {
        host: h.host,
        site: s.key,
        routes: [
          ...(s.apiSurfaces.length ? [{ path: '/api/*', upstream: 'api' }] : []),
          { path: '/*', upstream: UPSTREAM[s.app] },
        ],
        cookies: s.session === 'none' ? 'strip' : 'host-only',
        cache: s.kind === 'media' ? 'long-lived, signed URLs' : 'per-route',
      };
    }),
  };
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  console.log(JSON.stringify(plan(parseSiteEnv(process.argv[2] ?? 'production')), null, 2));
}

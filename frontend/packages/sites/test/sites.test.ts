import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  registry, validateRegistry, resolveHost, routeRequest, securityHeaders, sessionCookieName, robotsTxt, findSite,
  type SiteRegistry, type SiteDefinition,
} from '../src/index.ts';

const clone = (): SiteRegistry => structuredClone(registry);
const site = (key: string): SiteDefinition => {
  const s = findSite(key);
  assert.ok(s, `site ${key} missing`);
  return s;
};
const newSite = (over: Partial<SiteDefinition>): SiteDefinition => ({
  key: 'game', label: 'Game', kind: 'public-web', app: 'web', subdomain: 'game', aliases: [], status: 'planned', scope: 'P1',
  apiSurfaces: ['public'], session: 'member', seo: { indexable: true, sitemap: true }, owner: 'Test', ...over,
});

test('registry thật hợp lệ', () => {
  assert.deepEqual(validateRegistry(), []);
});

test('production: www phân giải, apex là alias → 308 về www (giữ path + query)', () => {
  const r = resolveHost('WWW.Tinhte.com.vn.', 'production');
  assert.equal(r.type, 'site');
  const d = routeRequest({ host: 'tinhte.com.vn', pathname: '/tin/abc', search: '?x=1', env: 'production', app: 'web' });
  assert.deepEqual(d, { action: 'redirect', status: 308, location: 'https://www.tinhte.com.vn/tin/abc?x=1' });
});

test('local: bỏ port khi phân giải, redirect dùng publicPort', () => {
  assert.equal(resolveHost('www.tinhte.localhost:3000', 'local').type, 'site');
  const d = routeRequest({ host: 'tinhte.localhost:8080', pathname: '/', env: 'local', app: 'web' });
  assert.deepEqual(d, { action: 'redirect', status: 308, location: 'http://www.tinhte.localhost:8080/' });
});

test('host lạ → 400; site của app khác → 404', () => {
  assert.deepEqual(routeRequest({ host: 'evil.example', pathname: '/', env: 'production', app: 'web' }),
    { action: 'reject', status: 400, reason: 'unknown-host' });
  assert.deepEqual(routeRequest({ host: 'cms.tinhte.com.vn', pathname: '/', env: 'production', app: 'web' }),
    { action: 'reject', status: 404, reason: 'site-not-served-by-app' });
  assert.deepEqual(routeRequest({ host: 'www.tinhte.com.vn', pathname: '/', env: 'production', app: 'cms' }),
    { action: 'reject', status: 404, reason: 'site-not-served-by-app' });
});

test('rewrite về /<siteKey><path>', () => {
  const d = routeRequest({ host: 'www.tinhte.com.vn', pathname: '/robots.txt', env: 'production', app: 'web' });
  assert.equal(d.action, 'rewrite');
  assert.equal(d.action === 'rewrite' && d.pathname, '/www/robots.txt');
  const home = routeRequest({ host: 'cms.staging.tinhte.com.vn', pathname: '/', env: 'staging', app: 'cms' });
  assert.equal(home.action === 'rewrite' && home.pathname, '/cms');
});

test('THÊM SUBDOMAIN MỚI chỉ bằng registry: không đổi code', () => {
  const r = clone();
  r.sites.push(newSite({}));
  assert.deepEqual(validateRegistry(r), []);
  assert.equal(resolveHost('game.tinhte.com.vn', 'production', r).type, 'unknown', 'planned không phân giải ở production');
  const d = routeRequest({ host: 'game.staging.tinhte.com.vn', pathname: '/review', env: 'staging', app: 'web' }, r);
  assert.equal(d.action === 'rewrite' && d.pathname, '/game/review');
  r.sites.at(-1)!.status = 'active';
  const prod = routeRequest({ host: 'game.tinhte.com.vn', pathname: '/', env: 'production', app: 'web' }, r);
  assert.equal(prod.action, 'rewrite');
  assert.equal(prod.action === 'rewrite' && prod.headers['X-TT-Site'], 'game');
  assert.equal(robotsTxt(r.sites.at(-1)!, 'production', r), 'User-agent: *\nAllow: /\nSitemap: https://game.tinhte.com.vn/sitemap.xml\n');
});

test('registry chặn cấu hình sai', () => {
  const dupLabel = clone(); dupLabel.sites.push(newSite({ subdomain: 'www' }));
  assert.match(validateRegistry(dupLabel).join(), /host label "www" already used by site "www"/);
  const dupKey = clone(); dupKey.sites.push(newSite({ key: 'www', subdomain: 'www2' }));
  assert.match(validateRegistry(dupKey).join(), /duplicate key/);
  const adminOnPublic = clone(); adminOnPublic.sites.push(newSite({ apiSurfaces: ['public', 'admin'] }));
  assert.match(validateRegistry(adminOnPublic).join(), /admin api surface is only allowed on cms sites/);
  const indexableCms = clone(); indexableCms.sites[1]!.seo.indexable = true;
  assert.match(validateRegistry(indexableCms).join(), /cms sites must not be indexable/);
  const plannedInProd = clone(); plannedInProd.environments.production.resolveStatuses = ['active', 'planned'];
  assert.match(validateRegistry(plannedInProd).join(), /only "active" sites may resolve/);
  const badKey = clone(); badKey.sites.push(newSite({ key: 'Game_1', subdomain: 'game1' }));
  assert.match(validateRegistry(badKey).join(), /key must match/);
});

test('cookie host-only __Host-, không có Domain cha', () => {
  assert.equal(sessionCookieName(site('www')), '__Host-tt_member_session');
  assert.equal(sessionCookieName(site('cms')), '__Host-tt_cms_session');
  assert.equal(sessionCookieName(site('media')), null);
});

test('header bảo mật: noindex ngoài production, CSP cho phép media host, CMS cấm nhúng frame', () => {
  const stagingWww = securityHeaders(site('www'), 'staging');
  assert.equal(stagingWww['X-Robots-Tag'], 'noindex, nofollow');
  const prodWww = securityHeaders(site('www'), 'production');
  assert.equal(prodWww['X-Robots-Tag'], undefined);
  assert.match(prodWww['Content-Security-Policy-Report-Only']!, /img-src 'self' data: https:\/\/media\.tinhte\.com\.vn/);
  assert.match(securityHeaders(site('cms'), 'production')['Content-Security-Policy-Report-Only']!, /frame-ancestors 'none'/);
  assert.equal(securityHeaders(site('cms'), 'production')['X-Robots-Tag'], 'noindex, nofollow');
  assert.equal(securityHeaders(site('www'), 'local')['Strict-Transport-Security'], undefined);
});

test('ca phân giải dùng chung với backend C# (config/sites/resolution-cases.json)', async () => {
  const { default: cases } = await import('../../../../config/sites/resolution-cases.json', { with: { type: 'json' } });
  for (const c of cases as Array<{ env: 'production' | 'staging' | 'local'; host: string; expect: Record<string, string> }>) {
    const r = resolveHost(c.host, c.env);
    const got: Record<string, string> = r.type === 'unknown' ? { type: 'unknown' } : { type: r.type, site: r.site.key };
    if (r.type === 'alias') got.canonicalHost = r.canonicalHost;
    assert.deepEqual(got, c.expect, `${c.env} ${c.host}`);
  }
});

test('robots: staging luôn Disallow', () => {
  assert.equal(robotsTxt(site('www'), 'staging'), 'User-agent: *\nDisallow: /\n');
});

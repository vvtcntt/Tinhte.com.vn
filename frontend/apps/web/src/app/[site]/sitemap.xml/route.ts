import { findSite, isIndexable, originFor, parseSiteEnv } from '@tinhte/sites';

// Phụ thuộc Host + môi trường lúc chạy → không prerender.
export const dynamic = 'force-dynamic';

// Sitemap theo site (ADR-0001 D7). Sprint 0: chỉ trang chủ; WBS-052 bổ sung news/video sitemap từ API.
export async function GET(_request: Request, { params }: { params: Promise<{ site: string }> }) {
  const { site: key } = await params;
  const site = findSite(key);
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  if (!site || !site.seo.sitemap || !isIndexable(site, env)) return new Response('Not found', { status: 404 });
  const xml = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
  <url><loc>${originFor(site, env)}/</loc></url>
</urlset>
`;
  return new Response(xml, { headers: { 'content-type': 'application/xml; charset=utf-8' } });
}

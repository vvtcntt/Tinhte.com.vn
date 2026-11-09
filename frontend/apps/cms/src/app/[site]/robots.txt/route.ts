import { findSite, parseSiteEnv, robotsTxt } from '@tinhte/sites';

// Phụ thuộc Host + môi trường lúc chạy → không prerender.
export const dynamic = 'force-dynamic';

// robots.txt theo site + môi trường (ADR-0001 D7): staging/local và site không index luôn Disallow.
export async function GET(_request: Request, { params }: { params: Promise<{ site: string }> }) {
  const { site: key } = await params;
  const site = findSite(key);
  if (!site) return new Response('Not found', { status: 404 });
  return new Response(robotsTxt(site, parseSiteEnv(process.env.TT_SITE_ENV)), {
    headers: { 'content-type': 'text/plain; charset=utf-8' },
  });
}

import { findSite, parseSiteEnv } from '@tinhte/sites';
import { demoAdSvg } from '../../../../lib/demo-ads';

export const dynamic = 'force-dynamic';

// Ảnh banner mẫu cho `/?demo-ads=1` (lib/demo-ads.ts). CHỈ môi trường local; staging/production luôn 404.
export async function GET(_request: Request, { params }: { params: Promise<{ site: string; file: string }> }) {
  const { site: key, file } = await params;
  const svg = parseSiteEnv(process.env.TT_SITE_ENV) === 'local' && findSite(key) ? demoAdSvg(file) : null;
  if (!svg) return new Response('Not found', { status: 404, headers: { 'content-type': 'text/plain; charset=utf-8' } });
  return new Response(svg, {
    headers: { 'content-type': 'image/svg+xml; charset=utf-8', 'cache-control': 'no-store', 'x-robots-tag': 'noindex, nofollow' },
  });
}

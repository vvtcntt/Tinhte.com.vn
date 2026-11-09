import { NextResponse, type NextRequest } from 'next/server';
import { parseSiteEnv, routeRequest } from '@tinhte/sites';

// ADR-0001 D2: phân giải site theo Host bằng Site Registry; rewrite về /<siteKey><path>.
const env = parseSiteEnv(process.env.TT_SITE_ENV);

export function proxy(request: NextRequest) {
  const decision = routeRequest({
    host: request.headers.get('host'),
    pathname: request.nextUrl.pathname,
    search: request.nextUrl.search,
    env,
    app: 'cms',
  });
  if (decision.action === 'redirect') return NextResponse.redirect(decision.location, decision.status);
  if (decision.action === 'reject') {
    return new NextResponse(decision.status === 400 ? 'Unknown host' : 'Not found', {
      status: decision.status,
      headers: { 'content-type': 'text/plain; charset=utf-8', 'x-robots-tag': 'noindex, nofollow' },
    });
  }
  const url = request.nextUrl.clone();
  url.pathname = decision.pathname;
  const response = NextResponse.rewrite(url);
  for (const [name, value] of Object.entries(decision.headers)) response.headers.set(name, value);
  return response;
}

// /api/* do edge (hoặc rewrites khi dev) chuyển tới API; tài nguyên build của Next không cần phân giải site.
export const config = {
  matcher: ['/((?!api/|_next/|favicon.ico).*)'],
};

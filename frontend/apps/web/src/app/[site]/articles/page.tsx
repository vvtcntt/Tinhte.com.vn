import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound, redirect } from 'next/navigation';
import { findSite, isIndexable, originFor, parseSiteEnv } from '@tinhte/sites';
import { ArticleListView } from '../../../components/ArticleListView';
import { cursorParam, loadArticleList } from '../../../lib/lists';
import { routes } from '../../../lib/routes';

// Tin mới (listPublicArticles; FR-DISC-004, FR-ID-001). Trang sau = ?cursor= (noindex, canonical về trang đầu).
export const dynamic = 'force-dynamic';

type Props = { params: Promise<{ site: string }>; searchParams: Promise<Record<string, string | string[] | undefined>> };

export async function generateMetadata({ params, searchParams }: Props): Promise<Metadata> {
  const site = findSite((await params).site);
  if (!site) return {};
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  const paged = cursorParam((await searchParams).cursor) !== undefined;
  return {
    title: `Tin mới — ${site.label}`,
    description: `Tin công nghệ mới nhất trên ${site.label}.`,
    alternates: { canonical: `${originFor(site, env)}${routes.latest()}` },
    robots: isIndexable(site, env) && !paged ? undefined : { index: false, follow: true },
  };
}

export default async function LatestPage({ params, searchParams }: Props) {
  const site = findSite((await params).site);
  if (!site) notFound();
  const cursor = cursorParam((await searchParams).cursor);
  const result = await loadArticleList(site, { cursor });
  if (result.kind === 'bad-cursor') redirect(routes.latest());
  if (result.kind === 'not-found') notFound();
  if (result.kind === 'error') {
    return (
      <div className="container">
        <div className="state-block" role="alert">
          <h1>Chưa tải được danh sách bài</h1>
          <p>Hệ thống đang bận. Vui lòng thử lại sau ít phút.</p>
          <Link className="button" href={routes.latest()}>
            Tải lại
          </Link>
        </div>
      </div>
    );
  }

  return (
    <ArticleListView
      title="Tin mới"
      breadcrumb={
        <nav className="breadcrumb" aria-label="Đường dẫn">
          <ol>
            <li>
              <Link href={routes.home()}>Trang chủ</Link>
            </li>
          </ol>
        </nav>
      }
      items={result.items}
      basePath={routes.latest()}
      nextCursor={result.nextCursor}
      isFirstPage={cursor === undefined}
      demo={result.demo}
    />
  );
}

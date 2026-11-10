import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound, redirect } from 'next/navigation';
import { findSite, isIndexable, originFor, parseSiteEnv } from '@tinhte/sites';
import { ArticleListView } from '../../../../components/ArticleListView';
import { loadNavCategories } from '../../../../lib/home';
import { cursorParam, isValidSlug, loadArticleList } from '../../../../lib/lists';
import { routes } from '../../../../lib/routes';

// Trang chuyên mục UX-PUB-02 (getCategoriesSlugArticles; FR-DISC-002, WBS-028). Chuyên mục không có / đã tắt → 404.
export const dynamic = 'force-dynamic';

type Props = {
  params: Promise<{ site: string; slug: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
};

async function resolveCategory(params: Props['params']) {
  const { site: key, slug } = await params;
  const site = findSite(key);
  if (!site || !isValidSlug(slug)) notFound();
  // Tên chuyên mục lấy từ GET /categories (đã nạp cho thanh điều hướng; fetch được dedupe trong request).
  const category = (await loadNavCategories(site)).find((c) => c.slug === slug);
  return { site, slug, category };
}

export async function generateMetadata({ params, searchParams }: Props): Promise<Metadata> {
  const { site, slug, category } = await resolveCategory(params);
  if (!category) return { title: site.label, robots: { index: false, follow: false } };
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  const paged = cursorParam((await searchParams).cursor) !== undefined;
  return {
    title: `${category.name} — ${site.label}`,
    description: `Tin ${category.name.toLowerCase()} mới nhất trên ${site.label}.`,
    alternates: { canonical: `${originFor(site, env)}${routes.category(slug)}` },
    robots: isIndexable(site, env) && !paged ? undefined : { index: false, follow: true },
  };
}

export default async function CategoryPage({ params, searchParams }: Props) {
  const { site, slug, category } = await resolveCategory(params);
  const cursor = cursorParam((await searchParams).cursor);
  const result = await loadArticleList(site, { category: slug, cursor });
  if (result.kind === 'bad-cursor') redirect(routes.category(slug));
  if (result.kind === 'not-found') notFound();   // API 404 = chuyên mục không có hoặc đã tắt
  if (result.kind === 'error') {
    return (
      <div className="container">
        <div className="state-block" role="alert">
          <h1>Chưa tải được chuyên mục</h1>
          <p>Hệ thống đang bận. Vui lòng thử lại sau ít phút.</p>
          <Link className="button" href={routes.category(slug)}>
            Tải lại
          </Link>
        </div>
      </div>
    );
  }

  const name = category?.name ?? slug;
  return (
    <ArticleListView
      title={name}
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
      basePath={routes.category(slug)}
      nextCursor={result.nextCursor}
      isFirstPage={cursor === undefined}
      demo={result.demo}
    />
  );
}

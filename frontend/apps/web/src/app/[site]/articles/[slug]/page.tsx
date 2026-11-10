import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound, permanentRedirect } from 'next/navigation';
import { findSite, isIndexable, originFor, parseSiteEnv } from '@tinhte/sites';
import logo from '../../../../assets/logo.png';
import { ArticleBody } from '../../../../components/article/ArticleBody';
import { AdZone } from '../../../../components/home/AdSlot';
import { SponsorBadge, TimeAgo } from '../../../../components/home/parts';
import { StoryCard } from '../../../../components/home/Stories';
import { loadArticle, loadArticleAd, loadRelated } from '../../../../lib/article';
import { dateTime } from '../../../../lib/format';
import { loadNavCategories } from '../../../../lib/home';
import { routes, safeHttpUrl } from '../../../../lib/routes';

// Trang bài UX-PUB-03 (FR-NEWS-001/002/008, FR-DISC-007, WBS-029). Thiết kế: doc/ux/UX-PUB-03_TRANG_BAI_v0.1_DRAFT.md
export const dynamic = 'force-dynamic';

type Props = { params: Promise<{ site: string; slug: string }> };

async function resolve(params: Props['params']) {
  const { site: key, slug } = await params;
  const site = findSite(key);
  if (!site) notFound();
  return { site, slug, result: await loadArticle(site, slug) };
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { site, result } = await resolve(params);
  if (result.kind !== 'ok') return { title: site.label, robots: { index: false, follow: false } };
  const { article } = result;
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  const url = `${originFor(site, env)}${routes.article(article.slug)}`;
  const image = safeHttpUrl(article.thumbnailUrl);
  return {
    title: `${article.title} — ${site.label}`,
    description: article.summary ?? undefined,
    alternates: { canonical: url },
    openGraph: {
      type: 'article',
      url,
      siteName: site.label,
      title: article.title,
      description: article.summary ?? undefined,
      publishedTime: article.publishedAt,
      authors: article.authorDisplayName ? [article.authorDisplayName] : undefined,
      images: image ? [image] : undefined,
      locale: 'vi_VN',
    },
    robots: isIndexable(site, env) && !result.demo ? undefined : { index: false, follow: false },
  };
}

export default async function ArticlePage({ params }: Props) {
  const { site, result } = await resolve(params);
  if (result.kind === 'moved') permanentRedirect(routes.article(result.slug));   // slug cũ → 308 (FR-NEWS-002)
  if (result.kind === 'not-found') notFound();
  if (result.kind === 'error') {
    return (
      <div className="container">
        <div className="state-block" role="alert">
          <h1>Chưa tải được bài viết</h1>
          <p>Hệ thống đang bận. Vui lòng thử lại sau ít phút.</p>
          <Link className="button" href={routes.home()}>
            Về trang chủ
          </Link>
        </div>
      </div>
    );
  }

  const { article, demo } = result;
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  const origin = originFor(site, env);
  const [categories, related, ad] = await Promise.all([loadNavCategories(site), loadRelated(site, article.id, demo), loadArticleAd(site)]);
  const category = categories.find((c) => c.slug === article.categorySlug);
  const image = safeHttpUrl(article.thumbnailUrl);
  const url = `${origin}${routes.article(article.slug)}`;

  const jsonLd = [
    {
      '@context': 'https://schema.org',
      '@type': 'NewsArticle',
      headline: article.title,
      description: article.summary ?? undefined,
      datePublished: article.publishedAt,
      mainEntityOfPage: url,
      image: image ? [image] : undefined,
      inLanguage: 'vi-VN',
      author: article.authorDisplayName ? { '@type': 'Person', name: article.authorDisplayName } : { '@type': 'Organization', name: site.label },
      publisher: { '@type': 'Organization', name: site.label, logo: { '@type': 'ImageObject', url: `${origin}${logo.src}` } },
      articleSection: category?.name,
    },
    {
      '@context': 'https://schema.org',
      '@type': 'BreadcrumbList',
      itemListElement: [
        { '@type': 'ListItem', position: 1, name: 'Trang chủ', item: `${origin}/` },
        ...(category ? [{ '@type': 'ListItem', position: 2, name: category.name, item: `${origin}${routes.category(category.slug)}` }] : []),
        { '@type': 'ListItem', position: category ? 3 : 2, name: article.title, item: url },
      ],
    },
  ];

  return (
    <div className="container article-page">
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd).replace(/</g, '\\u003c') }} />
      {demo && (
        <p className="demo-banner" role="note">
          <strong>Dữ liệu mẫu</strong> — API chưa phản hồi. Chỉ hiện ở môi trường local.
        </p>
      )}

      <nav className="breadcrumb" aria-label="Đường dẫn">
        <ol>
          <li>
            <Link href={routes.home()}>Trang chủ</Link>
          </li>
          {category && (
            <li>
              <Link href={routes.category(category.slug)}>{category.name}</Link>
            </li>
          )}
        </ol>
      </nav>

      <div className={related.length > 0 || ad ? 'article-layout article-layout--aside' : 'article-layout'}>
        <article className="article" aria-labelledby="tieu-de-bai">
          <header className="article__header">
            {article.contentKind === 'SPONSORED' && (
              <p className="sponsor-note">
                <SponsorBadge kind={article.contentKind} label={article.sponsorLabel} />
                <span>Bài viết này là nội dung có trả phí.</span>
              </p>
            )}
            <h1 id="tieu-de-bai" className="article__title">
              {article.title}
            </h1>
            {article.summary && <p className="article__lead">{article.summary}</p>}
            <p className="article__meta">
              {article.authorDisplayName && <span className="article__author">{article.authorDisplayName}</span>}
              <time dateTime={article.publishedAt}>{dateTime(article.publishedAt)}</time>
              <span aria-hidden="true">·</span>
              <TimeAgo iso={article.publishedAt} />
            </p>
          </header>

          {image && (
            <figure className="article__figure">
              <img src={image} alt="" fetchPriority="high" decoding="async" />
            </figure>
          )}

          <ArticleBody blocks={article.bodyBlocks} />
          <AdZone views={ad} className="ad-slot--article" />
        </article>

        {related.length > 0 && (
          <aside className="article-aside" aria-labelledby="doc-them">
            <h2 id="doc-them" className="article-aside__title">
              Đọc thêm
            </h2>
            <div className="article-aside__list">
              {related.map((a) => (
                <StoryCard key={a.id} article={a} variant="compact" />
              ))}
            </div>
          </aside>
        )}
      </div>
    </div>
  );
}

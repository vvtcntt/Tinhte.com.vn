import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound } from 'next/navigation';
import { findSite, isIndexable, originFor, parseSiteEnv } from '@tinhte/sites';
import { AdSlot } from '../../components/home/AdSlot';
import { SectionHeading } from '../../components/home/parts';
import { LeadStory, StoryCard } from '../../components/home/Stories';
import { VideoRail } from '../../components/home/VideoRail';
import { HOME_AD_CODES, HOME_FEED_AD_AFTER, loadHome } from '../../lib/home';
import { routes } from '../../lib/routes';

// Trang chủ UX-PUB-01 (FR-DISC-003, WBS-027). Thiết kế: doc/ux/UX-PUB-01_TRANG_CHU_v0.1_DRAFT.md
// Phụ thuộc Host + môi trường lúc chạy → render mỗi request; dữ liệu API cache 60 giây (lib/api.ts).
export const dynamic = 'force-dynamic';

type Props = { params: Promise<{ site: string }> };

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const site = findSite((await params).site);
  if (!site) return {};
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  const origin = originFor(site, env);
  const description = 'Tin tức, đánh giá và video ngắn về công nghệ.';
  return {
    title: `${site.label} — Tin công nghệ, đánh giá, video ngắn`,
    description,
    alternates: { canonical: `${origin}/` },
    openGraph: { type: 'website', url: `${origin}/`, siteName: site.label, title: site.label, description, locale: 'vi_VN' },
    robots: isIndexable(site, env) ? undefined : { index: false, follow: false },
  };
}

export default async function HomePage({ params }: Props) {
  const site = findSite((await params).site);
  if (!site) notFound();
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  const data = await loadHome(site);

  const jsonLd = {
    '@context': 'https://schema.org',
    '@type': 'WebSite',
    name: site.label,
    url: `${originFor(site, env)}/`,
    inLanguage: 'vi-VN',
    potentialAction: {
      '@type': 'SearchAction',
      target: `${originFor(site, env)}${routes.search()}?q={search_term_string}`,
      'query-input': 'required name=search_term_string',
    },
  };

  return (
    <>
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd).replace(/</g, '\\u003c') }} />
      <h1 className="visually-hidden">{site.label} — Tin công nghệ</h1>

      {data.status === 'error' ? (
        <div className="container">
          <div className="state-block" role="alert">
            <h2>Chưa tải được nội dung</h2>
            <p>Hệ thống đang bận. Vui lòng thử lại sau ít phút.</p>
            <Link className="button" href={routes.home()}>
              Tải lại
            </Link>
          </div>
        </div>
      ) : (
        <HomeContent data={data} />
      )}
    </>
  );
}

function HomeContent({ data }: { data: Extract<Awaited<ReturnType<typeof loadHome>>, { status: 'ok' }> }) {
  const { home, ads, demo } = data;
  const [lead, ...secondary] = home.featuredArticles;
  const recentHead = home.recentArticles.slice(0, HOME_FEED_AD_AFTER);
  const recentTail = home.recentArticles.slice(HOME_FEED_AD_AFTER);
  const sidebarAd = ads[HOME_AD_CODES.sidebar];
  const empty = !lead && home.recentArticles.length === 0 && home.featuredVideos.length === 0;

  return (
    <div className="container home">
      {demo && (
        <p className="demo-banner" role="note">
          <strong>Dữ liệu mẫu</strong> — API <code>GET /home</code> chưa phản hồi. Chỉ hiện ở môi trường local.
        </p>
      )}

      {empty && (
        <div className="state-block">
          <h2>Chưa có bài viết nào được xuất bản</h2>
          <p>Nội dung sẽ xuất hiện ở đây khi ban biên tập xuất bản bài đầu tiên.</p>
        </div>
      )}

      {lead && (
        <section className="home-section" aria-labelledby="tin-noi-bat">
          <SectionHeading id="tin-noi-bat" title="Tin nổi bật" />
          <div className="featured">
            <LeadStory article={lead} />
            {secondary.length > 0 && (
              <div className="featured__side">
                {secondary.map((a) => (
                  <StoryCard key={a.id} article={a} variant="compact" />
                ))}
              </div>
            )}
          </div>
        </section>
      )}

      {home.featuredVideos.length > 0 && (
        <section className="home-section home-section--videos" aria-labelledby="video-noi-bat">
          <SectionHeading
            id="video-noi-bat"
            title="Video nổi bật"
            action={
              <Link href={routes.shorts()} className="section-heading__more">
                Xem Shorts <span aria-hidden="true">→</span>
              </Link>
            }
          />
          <VideoRail videos={home.featuredVideos} />
        </section>
      )}

      {home.recentArticles.length > 0 && (
        <section className="home-section" aria-labelledby="tin-moi">
          <SectionHeading id="tin-moi" title="Tin mới" />
          <div className={sidebarAd ? 'latest latest--with-aside' : 'latest'}>
            <div className="latest__list">
              {recentHead.map((a) => (
                <StoryCard key={a.id} article={a} variant="row" />
              ))}
              <AdSlot slot={ads[HOME_AD_CODES.feed]} className="ad-slot--inline" />
              {recentTail.map((a) => (
                <StoryCard key={a.id} article={a} variant="row" />
              ))}
              <Link href={routes.latest()} className="button button--block">
                Xem thêm tin mới
              </Link>
            </div>
            {sidebarAd && (
              <div className="latest__aside">
                <AdSlot slot={sidebarAd} className="ad-slot--sticky" />
              </div>
            )}
          </div>
        </section>
      )}
    </div>
  );
}

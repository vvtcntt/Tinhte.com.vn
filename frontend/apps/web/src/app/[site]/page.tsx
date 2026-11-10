import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound } from 'next/navigation';
import { findSite, isIndexable, originFor, parseSiteEnv } from '@tinhte/sites';
import { AdPairSlot, AdSlot } from '../../components/home/AdSlot';
import { SectionHeading } from '../../components/home/parts';
import { RailControls } from '../../components/home/RailControls';
import { LeadStory, StoryCard } from '../../components/home/Stories';
import { VideoRail } from '../../components/home/VideoRail';
import { DEMO_ADS_PARAM, parseDemoAds } from '../../lib/demo-ads';
import { loadHome } from '../../lib/home';
import { HOME_FEED_AD_AFTER, homeAdPlan } from '../../lib/home-model';
import { routes } from '../../lib/routes';

// Trang chủ UX-PUB-01 (FR-DISC-003, WBS-027). Thiết kế: doc/ux/UX-PUB-01_TRANG_CHU_v0.1_DRAFT.md
// Phụ thuộc Host + môi trường lúc chạy → render mỗi request; dữ liệu API cache 60 giây (lib/api.ts).
export const dynamic = 'force-dynamic';

type Props = { params: Promise<{ site: string }>; searchParams: Promise<Record<string, string | string[] | undefined>> };

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

export default async function HomePage({ params, searchParams }: Props) {
  const site = findSite((await params).site);
  if (!site) notFound();
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  // ?demo-ads chỉ có tác dụng ở local (loadHome bỏ qua ở môi trường khác).
  const data = await loadHome(site, { demoAds: env === 'local' ? parseDemoAds((await searchParams)[DEMO_ADS_PARAM]) : null });

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

// Vùng quảng cáo ①–⑥ (UX-PUB-01 Bước 3): mỗi vùng chỉ render khi có slot → cờ OFF/không banner thì trang y hệt
// bản không quảng cáo. Mã placement và quy tắc vùng nào được hiện: lib/home-model.ts (HOME_AD_CODES, homeAdPlan).
function HomeContent({ data }: { data: Extract<Awaited<ReturnType<typeof loadHome>>, { status: 'ok' }> }) {
  const { home, ads, demo, demoAds } = data;
  const plan = homeAdPlan(home, ads);
  const [lead, ...secondary] = home.featuredArticles;
  const recentHead = home.recentArticles.slice(0, HOME_FEED_AD_AFTER);
  const recentTail = home.recentArticles.slice(HOME_FEED_AD_AFTER);
  const empty = !lead && home.recentArticles.length === 0 && home.featuredVideos.length === 0;

  return (
    <div className="container home">
      {demo && (
        <p className="demo-banner" role="note">
          <strong>Dữ liệu mẫu</strong> — API <code>GET /home</code> chưa phản hồi. Chỉ hiện ở môi trường local.
        </p>
      )}
      {demoAds && (
        <p className="demo-banner" role="note">
          <strong>Quảng cáo mẫu</strong> — banner giả để xem các vùng ①–⑥ (<code>?demo-ads</code>). Chỉ hiện ở môi trường local.
        </p>
      )}

      {empty && (
        <div className="state-block">
          <h2>Chưa có bài viết nào được xuất bản</h2>
          <p>Nội dung sẽ xuất hiện ở đây khi ban biên tập xuất bản bài đầu tiên.</p>
        </div>
      )}

      {/* ① Màn hình đầu: không lazy-load. */}
      <AdPairSlot pair={plan.top} eager className="ad-slot--top" />

      {lead && (
        <section className="home-section" aria-labelledby="tin-noi-bat">
          <SectionHeading id="tin-noi-bat" title="Tin nổi bật" />
          <div className="featured">
            <LeadStory article={lead} />
            {secondary.length > 0 && (
              <div className={plan.heroSide ? 'featured__side featured__side--with-ad' : 'featured__side'}>
                {secondary.map((a) => (
                  <StoryCard key={a.id} article={a} variant="compact" />
                ))}
                {/* ② Có banner: 3 tin phụ + 300×250 (homeFeaturedLimit), không có: 4 tin phụ. */}
                <AdSlot slot={plan.heroSide} className="ad-slot--rail" />
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
              <>
                <RailControls targetId="video-noi-bat-rail" label="Cuộn danh sách video" />
                <Link href={routes.shorts()} className="section-heading__more">
                  Xem Shorts <span aria-hidden="true">→</span>
                </Link>
              </>
            }
          />
          <VideoRail id="video-noi-bat-rail" videos={home.featuredVideos} />
        </section>
      )}

      {/* ③ Giữa Video nổi bật và Tin mới. */}
      <AdPairSlot pair={plan.mid} className="ad-slot--mid" />

      {/* Tin mới rỗng (mọi bài đang ở Tin nổi bật) vẫn phải có đường sang trang danh sách. */}
      {home.recentArticles.length === 0 && !empty && (
        <p className="home-more">
          <Link href={routes.latest()} className="button button--primary">
            Xem tất cả tin
          </Link>
        </p>
      )}

      {home.recentArticles.length > 0 && (
        <section className="home-section" aria-labelledby="tin-moi">
          <SectionHeading
            id="tin-moi"
            title="Tin mới"
            action={
              <Link href={routes.latest()} className="section-heading__more">
                Xem tất cả <span aria-hidden="true">→</span>
              </Link>
            }
          />
          <div className={plan.sidebar ? 'latest latest--with-aside' : 'latest'}>
            <div className="latest__list">
              {recentHead.map((a) => (
                <StoryCard key={a.id} article={a} variant="row" />
              ))}
              {/* ④ Sau tin mới thứ HOME_FEED_AD_AFTER. */}
              <AdPairSlot pair={plan.feed} className="ad-slot--inline" />
              {recentTail.map((a) => (
                <StoryCard key={a.id} article={a} variant="row" />
              ))}
              <p className="latest__more">
                <Link href={routes.latest()} className="button button--primary">
                  Xem thêm tin mới
                </Link>
              </p>
            </div>
            {plan.sidebar && (
              <div className="latest__aside">
                {/* ⑤ Chỉ ≥ 1024px, đứng yên khi cuộn. */}
                <AdSlot slot={plan.sidebar} media="wide" className="ad-slot--rail ad-slot--sticky" />
              </div>
            )}
          </div>
        </section>
      )}

      {/* ⑥ Trên footer. */}
      <AdPairSlot pair={plan.bottom} className="ad-slot--bottom" />
    </div>
  );
}

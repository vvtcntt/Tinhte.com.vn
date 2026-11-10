// Trang chủ: lọc nội dung + vùng quảng cáo ①–⑥ (UX-PUB-01). node:test, chạy TS trực tiếp như packages/sites.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import type { ArticleSummary, HomePage, PublicAdSlot, VideoSummary } from '../src/lib/contracts.ts';
import { demoAdSlots, demoAdSvg, parseDemoAds } from '../src/lib/demo-ads.ts';
import {
  acceptAdSlots, HOME_AD_CODE_LIST, HOME_AD_CODES, HOME_AD_SIZES, HOME_FEED_AD_AFTER, HOME_LIMITS, HOME_MID_AD_MIN_RECENT, homeAdPlan,
  homeFeaturedLimit, PLACEMENT_CODES_MAX, prepareHome, type HomeAds,
} from '../src/lib/home-model.ts';

const article = (n: number, minutesAgo: number, extra: Partial<ArticleSummary> = {}): ArticleSummary => ({
  id: `a${n}`,
  slug: `bai-${n}`,
  title: `Bài ${n}`,
  publishedAt: new Date(Date.UTC(2026, 10, 10, 12) - minutesAgo * 60_000).toISOString(),
  contentKind: 'EDITORIAL',
  sponsorLabel: null,
  ...extra,
});
const video = (n: number): VideoSummary => ({
  id: `v${n}`, slug: `v-${n}`, title: `Video ${n}`, publishedAt: '2026-11-10T00:00:00Z', posterUrl: null, durationMs: 1000,
  contentKind: 'EDITORIAL', sponsorLabel: null,
});
const slot = (code: string, extra: Partial<PublicAdSlot> = {}): PublicAdSlot => ({
  code, label: 'Quảng cáo', creativeUrl: `https://cdn.example.test/${code}.png`, altText: code, targetUrl: 'https://example.com/',
  width: 300, height: 250, rel: 'sponsored noopener', ...extra,
});
const allAds = (): HomeAds => acceptAdSlots(HOME_AD_CODE_LIST.map((c) => slot(c, HOME_AD_SIZES[c])));
const page = (featured: number, recent: number, videos: number): HomePage => ({
  featuredArticles: Array.from({ length: featured }, (_, i) => article(i + 1, i)),
  recentArticles: Array.from({ length: recent }, (_, i) => article(100 + i, 100 + i)),
  featuredVideos: Array.from({ length: videos }, (_, i) => video(i + 1)),
});

test('mã placement: hợp lệ theo spec, không trùng, đủ kích thước, vừa một lần gọi API', () => {
  const spec = readFileSync(new URL('../../../../doc/07_AI_REVIEW/v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml', import.meta.url), 'utf8');
  const param = spec.slice(spec.indexOf('    PlacementCodes:'), spec.indexOf('    CompareTo:'));
  assert.match(param, new RegExp(`maxItems: ${PLACEMENT_CODES_MAX}\\b`), 'PLACEMENT_CODES_MAX khớp spec');
  assert.ok(HOME_AD_CODE_LIST.length <= PLACEMENT_CODES_MAX);
  assert.equal(new Set(HOME_AD_CODE_LIST).size, HOME_AD_CODE_LIST.length);
  for (const code of HOME_AD_CODE_LIST) {
    assert.match(code, /^[a-z0-9_-]{2,80}$/);
    assert.ok(HOME_AD_SIZES[code].width > 0 && HOME_AD_SIZES[code].height > 0, code);
  }
  // ① mặc định thấp để không đẩy ảnh LCP; ⑤ khớp cột 300px.
  assert.deepEqual(HOME_AD_SIZES[HOME_AD_CODES.top], { width: 970, height: 90 });
  assert.equal(HOME_AD_SIZES[HOME_AD_CODES.sidebar].width, 300);
  assert.equal(HOME_AD_SIZES[HOME_AD_CODES.heroSide].width, 300);
});

test('acceptAdSlots: bỏ mã lạ, URL không an toàn, target không https, kích thước hỏng; trùng mã lấy slot đầu', () => {
  const ads = acceptAdSlots([
    slot('home_top', { width: 970, height: 90 }),
    slot('home_top', { creativeUrl: 'https://cdn.example.test/second.png' }),
    slot('khac_trang'),
    slot('home_mid', { creativeUrl: 'javascript:alert(1)' }),
    slot('home_bottom', { targetUrl: 'http://example.com/' }),
    slot('home_feed', { width: 0 }),
    slot('home_sidebar', { targetUrl: null }),
    null as unknown as PublicAdSlot,
  ]);
  assert.deepEqual(Object.keys(ads).sort(), ['home_sidebar', 'home_top']);
  assert.equal(ads.home_top?.creativeUrl, 'https://cdn.example.test/home_top.png');
  assert.deepEqual(acceptAdSlots(undefined), {});
});

test('prepareHome: không banner ② → 5 tin nổi bật; có banner ② → 4, tin dư xuống Tin mới đúng thứ tự thời gian', () => {
  const raw: HomePage = {
    featuredArticles: [article(1, 5), article(2, 10), article(3, 15), article(4, 20), article(5, 300)],
    recentArticles: [article(11, 100), article(12, 200), article(13, 400), article(14, 500)],
    featuredVideos: [],
  };
  const full = prepareHome(raw);
  assert.equal(full.featuredArticles.length, HOME_LIMITS.featured);
  assert.deepEqual(full.recentArticles.map((a) => a.id), ['a11', 'a12', 'a13', 'a14']);

  const withAd = prepareHome(raw, { featured: homeFeaturedLimit(acceptAdSlots([slot(HOME_AD_CODES.heroSide)])) });
  assert.deepEqual(withAd.featuredArticles.map((a) => a.id), ['a1', 'a2', 'a3', 'a4']);
  assert.deepEqual(withAd.recentArticles.map((a) => a.id), ['a11', 'a12', 'a5', 'a13', 'a14']);
});

test('prepareHome: tin chuyển xuống không bị lặp, vẫn lọc fail-closed và cắt 12', () => {
  const moved = article(5, 1);
  const raw: HomePage = {
    featuredArticles: [article(1, 0), article(2, 0), article(3, 0), article(4, 0), moved],
    recentArticles: [moved, ...Array.from({ length: 14 }, (_, i) => article(20 + i, 10 + i)), article(99, 0, { contentKind: 'SPONSORED', sponsorLabel: ' ' })],
    featuredVideos: [],
  };
  const out = prepareHome(raw, { featured: 4 });
  assert.equal(out.recentArticles.filter((a) => a.id === 'a5').length, 1);
  assert.equal(out.recentArticles[0]!.id, 'a5');
  assert.equal(out.recentArticles.length, HOME_LIMITS.recent);
  assert.ok(!out.recentArticles.some((a) => a.id === 'a99'));
  assert.ok(!out.recentArticles.some((a) => out.featuredArticles.some((f) => f.id === a.id)));
});

test('homeAdPlan: không có slot → không vùng nào (trang y hệt bản không quảng cáo)', () => {
  const plan = homeAdPlan(page(5, 12, 8), {});
  assert.deepEqual(plan, { top: {}, heroSide: undefined, mid: {}, feed: {}, sidebar: undefined, bottom: {} });
});

test('homeAdPlan: đủ nội dung + đủ slot → đủ 6 vùng, mỗi vùng đúng mã desktop/mobile', () => {
  const plan = homeAdPlan(page(4, 12, 8), allAds());
  assert.equal(plan.top.desktop?.code, 'home_top');
  assert.equal(plan.top.mobile?.code, 'home_top_mobile');
  assert.equal(plan.heroSide?.code, 'home_hero_side');
  assert.equal(plan.mid.desktop?.code, 'home_mid');
  assert.equal(plan.mid.mobile?.code, 'home_mid_mobile');
  assert.equal(plan.feed.desktop?.code, 'home_feed');
  assert.equal(plan.feed.mobile?.code, 'home_feed_mobile');
  assert.equal(plan.sidebar?.code, 'home_sidebar');
  assert.equal(plan.bottom.desktop?.code, 'home_bottom');
  assert.equal(plan.bottom.mobile?.code, 'home_bottom_mobile');
});

test('homeAdPlan: vùng phụ thuộc nội dung xung quanh', () => {
  const ads = allAds();
  // Trang rỗng: không quảng cáo nào.
  assert.deepEqual(homeAdPlan(page(0, 0, 0), ads), { top: {}, mid: {}, feed: {}, bottom: {} });
  // ② cần ít nhất một tin phụ.
  assert.equal(homeAdPlan(page(1, 12, 8), ads).heroSide, undefined);
  // ④ chỉ khi Tin mới dài hơn HOME_FEED_AD_AFTER; ⑤ đi cùng Tin mới.
  assert.deepEqual(homeAdPlan(page(4, HOME_FEED_AD_AFTER, 8), ads).feed, {});
  assert.equal(homeAdPlan(page(4, HOME_FEED_AD_AFTER + 1, 8), ads).feed.desktop?.code, 'home_feed');
  const noRecent = homeAdPlan(page(4, 0, 8), ads);
  assert.equal(noRecent.sidebar, undefined);
  assert.deepEqual(noRecent.mid, {});
  assert.equal(noRecent.bottom.desktop?.code, 'home_bottom');
  // ③ cần Tin mới đủ dài (mật độ mobile ≤ ~30%).
  assert.deepEqual(homeAdPlan(page(4, HOME_MID_AD_MIN_RECENT - 1, 8), ads).mid, {});
  assert.equal(homeAdPlan(page(4, HOME_MID_AD_MIN_RECENT, 8), ads).mid.mobile?.code, 'home_mid_mobile');
  // Không có Video: ③ sẽ đứng sát ② → bỏ ③; không có ② thì ③ vẫn hiện.
  assert.deepEqual(homeAdPlan(page(4, 12, 0), ads).mid, {});
  const { [HOME_AD_CODES.heroSide]: _hero, ...withoutHero } = ads;
  assert.equal(homeAdPlan(page(4, 12, 0), withoutHero).mid.desktop?.code, 'home_mid');
});

test('demo-ads: chỉ nhận mã trang chủ, slot mẫu qua được acceptAdSlots, SVG đúng kích thước', () => {
  assert.equal(parseDemoAds(undefined), null);
  assert.deepEqual(parseDemoAds('1'), [...HOME_AD_CODE_LIST]);
  assert.deepEqual(parseDemoAds(['home_mid,home_top,khac']), ['home_top', 'home_mid']);
  assert.equal(parseDemoAds('khac'), null);
  const slots = demoAdSlots('http://localhost:3002', HOME_AD_CODE_LIST);
  assert.equal(Object.keys(acceptAdSlots(slots)).length, HOME_AD_CODE_LIST.length);
  assert.match(demoAdSvg('home_top.svg') ?? '', /width="970" height="90"/);
  assert.equal(demoAdSvg('home_top.png'), null);
  assert.equal(demoAdSvg('../etc.svg'), null);
});

import type { ArticleSummary, HomePage, PublicAdSlot, VideoSummary } from './contracts.ts';
import { safeHttpUrl } from './routes.ts';

// Phần thuần của trang chủ (UX-PUB-01): lọc nội dung và quyết định vùng quảng cáo nào được hiện.
// Không gọi mạng, không phụ thuộc Next → kiểm bằng node:test (apps/web/test/home-model.test.ts).

export const HOME_LIMITS = { featured: 5, videos: 8, recent: 12 } as const;

/**
 * Mã placement quảng cáo trang chủ — ĐỀ XUẤT, chờ WBS-131 (PO + Commercial + Legal) chốt
 * (UX-PUB-01 mục "Điểm còn mở" số 3). Đổi mã ở đây, không rải chuỗi ra component.
 * Phạm vi: WBS-133 (P0) chỉ cam kết 1–2 slot tĩnh; FE dựng sẵn 6 vùng, vùng nào hiện là do placement nào được tạo.
 *
 * Hợp đồng hiện tại cho mỗi mã đúng một width/height → desktop và mobile là hai mã (hậu tố `_mobile`):
 * mã `_mobile` hiện ở < 768px, mã không hậu tố hiện từ 768px (tablet dùng banner desktop thu nhỏ theo khung).
 * Ngoại lệ: ② dùng một mã cho mọi màn hình (cùng 300×250); ⑤ chỉ có ở ≥ 1024px.
 */
export const HOME_AD_CODES = {
  top: 'home_top', //                  ① dưới header, trên Tin nổi bật
  topMobile: 'home_top_mobile',
  heroSide: 'home_hero_side', //       ② cột phải Tin nổi bật
  mid: 'home_mid', //                  ③ giữa Video nổi bật và Tin mới
  midMobile: 'home_mid_mobile',
  feed: 'home_feed', //                ④ sau tin mới thứ HOME_FEED_AD_AFTER
  feedMobile: 'home_feed_mobile',
  sidebar: 'home_sidebar', //          ⑤ cột phải Tin mới, đứng yên khi cuộn
  bottom: 'home_bottom', //            ⑥ trên footer
  bottomMobile: 'home_bottom_mobile',
} as const;
export type HomeAdCode = (typeof HOME_AD_CODES)[keyof typeof HOME_AD_CODES];
export const HOME_AD_CODE_LIST: readonly HomeAdCode[] = Object.values(HOME_AD_CODES);

/**
 * Kích thước đề xuất khi tạo placement trong CMS. FE không ép kích thước: AdSlot giữ chỗ theo width/height API trả,
 * nên ① đổi sang 970×250 cho chiến dịch lớn chỉ cần sửa placement, không cần deploy.
 */
export const HOME_AD_SIZES: Readonly<Record<HomeAdCode, { width: number; height: number }>> = {
  home_top: { width: 970, height: 90 },
  home_top_mobile: { width: 320, height: 100 },
  home_hero_side: { width: 300, height: 250 },
  home_mid: { width: 970, height: 250 },
  home_mid_mobile: { width: 300, height: 250 },
  home_feed: { width: 728, height: 90 },
  home_feed_mobile: { width: 300, height: 250 },
  home_sidebar: { width: 300, height: 600 },
  home_bottom: { width: 970, height: 90 },
  home_bottom_mobile: { width: 320, height: 100 },
};

/** OpenAPI v0.3 `PlacementCodes`: tối đa 10 mã mỗi lần gọi `GET /ads/placements`. Trang chủ đang dùng đủ 10. */
export const PLACEMENT_CODES_MAX = 10;
/** Slot ④ chèn sau bài thứ N của Tin mới (đề xuất, PO duyệt). Số chẵn để khớp lưới 2 cột desktop. */
export const HOME_FEED_AD_AFTER = 6;
/**
 * ③ chỉ hiện khi Tin mới có từ N bài. Đo ở 390px (10/11/2026, dữ liệu mẫu): đủ 5 vùng mobile chiếm ~28% chiều cao
 * nội dung khi Tin mới 11–12 bài, nhưng 31% khi chỉ 7 bài → Tin mới ngắn thì bỏ ③ để giữ dưới ~30%.
 */
export const HOME_MID_AD_MIN_RECENT = 10;

export type HomeAds = Partial<Record<HomeAdCode, PublicAdSlot>>;

/** Giữ slot thuộc danh sách mã đã xin, creative là URL http(s), target chỉ https, kích thước dương. Trùng mã: lấy slot đầu. */
export function acceptAdSlots(items: readonly PublicAdSlot[] | null | undefined, codes: readonly HomeAdCode[] = HOME_AD_CODE_LIST): HomeAds {
  const allowed = new Set<string>(codes);
  const out: HomeAds = {};
  for (const slot of items ?? []) {
    if (!slot || !allowed.has(slot.code)) continue;
    const code = slot.code as HomeAdCode;
    if (out[code]) continue;
    if (!safeHttpUrl(slot.creativeUrl) || (slot.targetUrl && !safeHttpUrl(slot.targetUrl, { httpsOnly: true }))) continue;
    if (!(Number.isFinite(slot.width) && slot.width > 0 && Number.isFinite(slot.height) && slot.height > 0)) continue;
    out[code] = slot;
  }
  return out;
}

/** Có banner ② thì cột phải còn chỗ cho 3 tin phụ (thay vì 4) để hai cột vẫn cân. */
export function homeFeaturedLimit(ads: HomeAds): number {
  return ads[HOME_AD_CODES.heroSide] ? HOME_LIMITS.featured - 1 : HOME_LIMITS.featured;
}

type WithKind = { contentKind?: unknown; sponsorLabel?: string | null };

/** Fail-closed: chỉ giữ item có contentKind hợp lệ; SPONSORED bắt buộc có nhãn (AIR-011). */
export function isDisplayable(item: WithKind): boolean {
  if (item.contentKind === 'EDITORIAL') return true;
  if (item.contentKind === 'SPONSORED') return typeof item.sponsorLabel === 'string' && item.sponsorLabel.trim().length > 0;
  return false;
}

const publishedTime = (a: ArticleSummary | null | undefined) => {
  const t = Date.parse(a?.publishedAt ?? '');
  return Number.isNaN(t) ? Number.NEGATIVE_INFINITY : t;
};

/** Chèn bài vào danh sách đang xếp mới → cũ, giữ nguyên thứ tự backend trả cho phần còn lại. */
function insertByNewest(list: readonly ArticleSummary[], extra: readonly ArticleSummary[]): ArticleSummary[] {
  const out = [...list];
  for (const item of extra) {
    const at = out.findIndex((x) => publishedTime(x) < publishedTime(item));
    if (at === -1) out.push(item);
    else out.splice(at, 0, item);
  }
  return out;
}

/**
 * Lọc, loại trùng (Tin mới không lặp Tin nổi bật), cắt theo giới hạn.
 * `featured` < 5 (có banner ②): tin nổi bật dư không bị mất mà chuyển xuống Tin mới theo thời gian xuất bản.
 */
export function prepareHome(raw: Partial<HomePage>, { featured = HOME_LIMITS.featured }: { featured?: number } = {}): HomePage {
  const clean = <T extends WithKind & { id: string }>(items: readonly T[] | undefined, limit: number, skip = new Set<string>()) => {
    const out: T[] = [];
    for (const item of items ?? []) {
      if (out.length >= limit) break;
      if (!item?.id || skip.has(item.id) || !isDisplayable(item)) continue;
      skip.add(item.id);
      out.push(item);
    }
    return out;
  };
  const pool = clean<ArticleSummary>(raw.featuredArticles, HOME_LIMITS.featured);
  const featuredArticles = pool.slice(0, Math.max(0, featured));
  const moved = pool.slice(featuredArticles.length);
  const recentSource = moved.length > 0 ? insertByNewest(raw.recentArticles ?? [], moved) : raw.recentArticles;
  const recentArticles = clean<ArticleSummary>(recentSource, HOME_LIMITS.recent, new Set(featuredArticles.map((a) => a.id)));
  const featuredVideos = clean<VideoSummary>(raw.featuredVideos, HOME_LIMITS.videos);
  return { featuredArticles, recentArticles, featuredVideos };
}

/** Cặp slot của một vùng: `desktop` hiện từ 768px, `mobile` dưới 768px. */
export interface AdPair {
  desktop?: PublicAdSlot;
  mobile?: PublicAdSlot;
}

export interface HomeAdPlan {
  top: AdPair;
  heroSide?: PublicAdSlot;
  mid: AdPair;
  feed: AdPair;
  sidebar?: PublicAdSlot;
  bottom: AdPair;
}

/**
 * Vùng nào được hiện, dựa trên nội dung thật của trang (không có banner → không có khung, FR-ADS-002):
 * - trang rỗng → không quảng cáo nào;
 * - ② cần ít nhất một tin phụ (cột phải không chỉ có quảng cáo);
 * - ③ cần khối phía trên (Tin nổi bật hoặc Video) và Tin mới đủ dài phía dưới (HOME_MID_AD_MIN_RECENT — giữ mật độ
 *   mobile ≤ ~30%); không có Video mà có ② thì bỏ ③ vì hai banner sẽ đứng sát nhau;
 * - ④ chỉ khi Tin mới dài hơn HOME_FEED_AD_AFTER (banner nằm giữa các tin, không ở cuối danh sách);
 * - ⑤ đi cùng vùng Tin mới.
 */
export function homeAdPlan(home: HomePage, ads: HomeAds): HomeAdPlan {
  const none: AdPair = {};
  const pair = (desktop: HomeAdCode, mobile: HomeAdCode): AdPair => ({
    ...(ads[desktop] && { desktop: ads[desktop] }),
    ...(ads[mobile] && { mobile: ads[mobile] }),
  });
  const hasRecent = home.recentArticles.length > 0;
  const hasVideos = home.featuredVideos.length > 0;
  if (home.featuredArticles.length === 0 && !hasRecent && !hasVideos) return { top: none, mid: none, feed: none, bottom: none };

  const heroSide = home.featuredArticles.length > 1 ? ads[HOME_AD_CODES.heroSide] : undefined;
  const midAllowed =
    (home.featuredArticles.length > 0 || hasVideos) && home.recentArticles.length >= HOME_MID_AD_MIN_RECENT && (hasVideos || !heroSide);
  return {
    top: pair(HOME_AD_CODES.top, HOME_AD_CODES.topMobile),
    heroSide,
    mid: midAllowed ? pair(HOME_AD_CODES.mid, HOME_AD_CODES.midMobile) : none,
    feed: home.recentArticles.length > HOME_FEED_AD_AFTER ? pair(HOME_AD_CODES.feed, HOME_AD_CODES.feedMobile) : none,
    sidebar: hasRecent ? ads[HOME_AD_CODES.sidebar] : undefined,
    bottom: pair(HOME_AD_CODES.bottom, HOME_AD_CODES.bottomMobile),
  };
}

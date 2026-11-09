import { parseSiteEnv, type SiteDefinition } from '@tinhte/sites';
import { apiGet } from './api';
import type { ArticleSummary, Category, CategoryList, HomePage, PublicAdSlot, PublicAdSlotList, VideoSummary } from './contracts';
import { demoCategories, demoHome } from './demo-home';
import { safeHttpUrl } from './routes';

// Trang chủ (UX-PUB-01, doc/ux/UX-PUB-01_TRANG_CHU_v0.1_DRAFT.md).

export const HOME_LIMITS = { featured: 5, videos: 8, recent: 12 } as const;
/** Mã placement đề xuất — WBS-131 chốt. */
export const HOME_AD_CODES = { feed: 'home_feed', sidebar: 'home_sidebar' } as const;
/** Slot home_feed chèn sau bài thứ N của Tin mới (đề xuất, PO duyệt). Số chẵn để khớp lưới 2 cột desktop. */
export const HOME_FEED_AD_AFTER = 6;

type WithKind = { contentKind?: unknown; sponsorLabel?: string | null };

/** Fail-closed: chỉ giữ item có contentKind hợp lệ; SPONSORED bắt buộc có nhãn (AIR-011). */
export function isDisplayable(item: WithKind): boolean {
  if (item.contentKind === 'EDITORIAL') return true;
  if (item.contentKind === 'SPONSORED') return typeof item.sponsorLabel === 'string' && item.sponsorLabel.trim().length > 0;
  return false;
}

/** Lọc, loại trùng (Tin mới không lặp Tin nổi bật), cắt theo giới hạn. */
export function prepareHome(raw: Partial<HomePage>): HomePage {
  const clean = <T extends WithKind & { id: string }>(items: T[] | undefined, limit: number, skip = new Set<string>()) => {
    const out: T[] = [];
    for (const item of items ?? []) {
      if (out.length >= limit) break;
      if (!item?.id || skip.has(item.id) || !isDisplayable(item)) continue;
      skip.add(item.id);
      out.push(item);
    }
    return out;
  };
  const seen = new Set<string>();
  const featuredArticles = clean<ArticleSummary>(raw.featuredArticles, HOME_LIMITS.featured, seen);
  const recentArticles = clean<ArticleSummary>(raw.recentArticles, HOME_LIMITS.recent, seen);
  const featuredVideos = clean<VideoSummary>(raw.featuredVideos, HOME_LIMITS.videos);
  return { featuredArticles, recentArticles, featuredVideos };
}

export type HomeData =
  | { status: 'ok'; home: HomePage; ads: Record<string, PublicAdSlot>; demo: boolean }
  | { status: 'error' };

/** Chuyên mục cho thanh điều hướng. Lỗi/rỗng → ẩn nav (local: dùng danh sách mẫu). */
export async function loadNavCategories(site: SiteDefinition): Promise<Category[]> {
  try {
    const list = await apiGet<CategoryList>(site, '/categories', { revalidate: 300 });
    return list.items.filter((c) => c.isEnabled !== false);
  } catch {
    return parseSiteEnv(process.env.TT_SITE_ENV) === 'local' ? demoCategories : [];
  }
}

export async function loadHome(site: SiteDefinition): Promise<HomeData> {
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  const [home, ads] = await Promise.allSettled([
    apiGet<HomePage>(site, '/home'),
    apiGet<PublicAdSlotList>(site, `/ads/placements?placementCodes=${Object.values(HOME_AD_CODES).join(',')}`),
  ]);

  const slots: Record<string, PublicAdSlot> = {};
  if (ads.status === 'fulfilled') {
    for (const slot of ads.value.items) {
      // Creative/target phải là URL an toàn; target chỉ https (AdPlacement.targetUrl pattern).
      if (!safeHttpUrl(slot.creativeUrl) || (slot.targetUrl && !safeHttpUrl(slot.targetUrl, { httpsOnly: true }))) continue;
      if (!(slot.width > 0 && slot.height > 0)) continue;
      slots[slot.code] = slot;
    }
  }

  if (home.status === 'fulfilled') return { status: 'ok', home: prepareHome(home.value), ads: slots, demo: false };
  // Chỉ máy dev: API chưa có /home thì dựng giao diện bằng dữ liệu mẫu, có dải cảnh báo.
  if (env === 'local') {
    return { status: 'ok', home: prepareHome(demoHome()), ads: slots, demo: true };
  }
  return { status: 'error' };
}

import { headers } from 'next/headers';
import { hostFor, originFor, parseSiteEnv, type SiteDefinition, type SiteEnv } from '@tinhte/sites';
import { apiGet } from './api';
import type { Category, CategoryList, HomePage, PublicAdSlotList } from './contracts';
import { demoAdSlots } from './demo-ads';
import { demoCategories, demoHome } from './demo-home';
import { acceptAdSlots, HOME_AD_CODE_LIST, homeFeaturedLimit, prepareHome, type HomeAdCode, type HomeAds } from './home-model';

// Trang chủ (UX-PUB-01, doc/ux/UX-PUB-01_TRANG_CHU_v0.1_DRAFT.md). Phần thuần (lọc, mã quảng cáo, vùng nào hiện)
// ở home-model.ts; file này chỉ gọi API.
export { HOME_LIMITS, isDisplayable, prepareHome } from './home-model';

export type HomeData =
  | { status: 'ok'; home: HomePage; ads: HomeAds; demo: boolean; demoAds: boolean }
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

/** `demoAds`: mã banner mẫu từ `?demo-ads=` — chỉ có tác dụng ở môi trường local. */
export async function loadHome(site: SiteDefinition, { demoAds = null }: { demoAds?: HomeAdCode[] | null } = {}): Promise<HomeData> {
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  const [home, ads] = await Promise.allSettled([
    apiGet<HomePage>(site, '/home'),
    // Lỗi hoặc cờ ads.display_slots OFF (danh sách rỗng) → không slot nào → không khung trống (FR-ADS-002).
    apiGet<PublicAdSlotList>(site, `/ads/placements?placementCodes=${HOME_AD_CODE_LIST.join(',')}`),
  ]);

  let slots = acceptAdSlots(ads.status === 'fulfilled' ? ads.value.items : []);
  const demoOrigin = env === 'local' && demoAds ? await requestOrigin(site, env) : null;
  if (demoOrigin && demoAds) slots = acceptAdSlots(demoAdSlots(demoOrigin, demoAds));
  const usingDemoAds = demoOrigin !== null;
  const prepare = (raw: Partial<HomePage>) => prepareHome(raw, { featured: homeFeaturedLimit(slots) });

  if (home.status === 'fulfilled') return { status: 'ok', home: prepare(home.value), ads: slots, demo: false, demoAds: usingDemoAds };
  // Chỉ máy dev: API chưa có /home thì dựng giao diện bằng dữ liệu mẫu, có dải cảnh báo.
  if (env === 'local') {
    return { status: 'ok', home: prepare(demoHome()), ads: slots, demo: true, demoAds: usingDemoAds };
  }
  return { status: 'error' };
}

/** Origin trình duyệt đang dùng (kể cả cổng dev), chỉ khi Host đúng là host của site trong Registry (+ cổng số). */
async function requestOrigin(site: SiteDefinition, env: SiteEnv): Promise<string | null> {
  const [name, port, ...rest] = ((await headers()).get('host') ?? '').toLowerCase().split(':');
  if (rest.length > 0 || name !== hostFor(site, env) || (port !== undefined && !/^\d{1,5}$/.test(port))) return null;
  return `${new URL(originFor(site, env)).protocol}//${name}${port ? `:${port}` : ''}`;
}

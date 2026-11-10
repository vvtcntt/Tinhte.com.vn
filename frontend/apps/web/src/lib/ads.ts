import type { AdBreakpoint, PublicAdCreative, PublicAdSlot } from './contracts.ts';
import { safeHttpUrl } from './routes.ts';

// Quảng cáo tĩnh nhiều kích thước (OpenAPI v0.3 DRAFT đợt 3: PublicAdSlot.creatives[], mỗi breakpoint tối đa một banner).
// Phần thuần, không phụ thuộc Next → kiểm bằng node:test (apps/web/test/ads.test.ts). Trang chủ: home-model.ts; trang bài: article.ts.

/** Thứ tự màn hình nhỏ → lớn. Mốc trùng globals.css (`.ad-slot--no-*`): MOBILE < 768px, TABLET 768–1023px, DESKTOP ≥ 1024px. */
export const AD_BREAKPOINTS: readonly AdBreakpoint[] = ['MOBILE', 'TABLET', 'DESKTOP'];

/**
 * Khoảng màn hình của từng breakpoint, dùng cho `<source media>` để ở màn hình bị ẩn trình duyệt chọn GIF trống thay vì tải creative.
 * MOBILE/DESKTOP viết bằng min-width + `not` để hai nửa bù nhau đúng mốc; TABLET cần cả hai đầu nên dùng max-width .98px
 * (độ rộng lẻ giữa 1023.98 và 1024px chỉ ảnh hưởng ảnh nào được tải, khung vẫn do CSS ẩn/hiện).
 */
export const AD_RANGE: Readonly<Record<AdBreakpoint, string>> = {
  MOBILE: 'not all and (min-width: 768px)',
  TABLET: '(min-width: 768px) and (max-width: 1023.98px)',
  DESKTOP: '(min-width: 1024px)',
};

export type AdSlots<C extends string = string> = Partial<Record<C, PublicAdSlot>>;

const isSize = (n: unknown) => typeof n === 'number' && Number.isFinite(n) && n > 0;

/**
 * Giữ slot thuộc danh sách mã đã xin; trong mỗi slot chỉ giữ banner hợp lệ: breakpoint đã biết, creative là URL http(s),
 * target chỉ https (hoặc không có), kích thước dương. Trùng breakpoint → lấy banner đầu. Trùng mã → lấy slot đầu.
 * Slot không còn banner nào bị bỏ (không có banner thì không có khung — FR-ADS-002). Fail-closed với dữ liệu lạ.
 */
export function acceptAdSlots<C extends string>(items: readonly PublicAdSlot[] | null | undefined, codes: readonly C[]): AdSlots<C> {
  const allowed = new Set<string>(codes);
  const out: AdSlots<C> = {};
  for (const slot of items ?? []) {
    if (!slot || !allowed.has(slot.code) || out[slot.code as C]) continue;
    const seen = new Set<AdBreakpoint>();
    const creatives: PublicAdCreative[] = [];
    for (const c of Array.isArray(slot.creatives) ? slot.creatives : []) {
      if (!c || !AD_BREAKPOINTS.includes(c.breakpoint) || seen.has(c.breakpoint)) continue;
      const creativeUrl = safeHttpUrl(c.creativeUrl);
      if (!creativeUrl || (c.targetUrl && !safeHttpUrl(c.targetUrl, { httpsOnly: true }))) continue;
      if (!isSize(c.width) || !isSize(c.height)) continue;
      seen.add(c.breakpoint);
      creatives.push({ ...c, creativeUrl, altText: typeof c.altText === 'string' ? c.altText : '' });
    }
    if (creatives.length === 0) continue;
    creatives.sort((a, b) => AD_BREAKPOINTS.indexOf(a.breakpoint) - AD_BREAKPOINTS.indexOf(b.breakpoint));
    out[slot.code as C] = { ...slot, creatives };
  }
  return out;
}

/** Một khung quảng cáo sẽ render: một banner + các breakpoint nó hiện (có thể không liền nhau). */
export interface AdView {
  code: string;
  label: string;
  creative: PublicAdCreative;
  breakpoints: AdBreakpoint[];
}

/** Một mã trong vùng + các breakpoint mã đó được hiện ở vùng này. */
export interface AdZoneEntry {
  code: string;
  breakpoints: readonly AdBreakpoint[];
}

/**
 * Breakpoint không có banner riêng thì mượn banner của breakpoint kề trong CÙNG mã: TABLET ↔ DESKTOP (banner desktop thu nhỏ vừa khung
 * trên tablet — hành vi đợt 2). MOBILE không mượn và không cho mượn: banner desktop quá rộng cho mobile và ngược lại quá nhỏ.
 */
const BORROW: Readonly<Record<AdBreakpoint, readonly AdBreakpoint[]>> = { MOBILE: [], TABLET: ['DESKTOP'], DESKTOP: ['TABLET'] };

const sameCreative = (a: PublicAdCreative, b: PublicAdCreative) =>
  a.creativeUrl === b.creativeUrl && a.width === b.width && a.height === b.height && a.altText === b.altText && (a.targetUrl ?? null) === (b.targetUrl ?? null);

/**
 * Vùng quảng cáo → danh sách khung. Với từng breakpoint: duyệt các mã theo thứ tự, lấy banner đúng breakpoint, nếu không có thì banner
 * mượn (BORROW) của cùng mã; mã chỉ được xét ở breakpoint nó được phép. Cùng mã + cùng banner ở nhiều breakpoint gộp một khung.
 * Rỗng → vùng không render gì.
 */
export function resolveAdZone(slots: AdSlots, entries: readonly AdZoneEntry[]): AdView[] {
  const views: AdView[] = [];
  for (const bp of AD_BREAKPOINTS) {
    for (const { code, breakpoints } of entries) {
      if (!breakpoints.includes(bp)) continue;
      const slot = slots[code];
      if (!slot) continue;
      const creative = [bp, ...BORROW[bp]].map((b) => slot.creatives.find((c) => c.breakpoint === b)).find(Boolean);
      if (!creative) continue;
      const same = views.find((v) => v.code === code && sameCreative(v.creative, creative));
      if (same) same.breakpoints.push(bp);
      else views.push({ code, label: slot.label?.trim() || 'Quảng cáo', creative, breakpoints: [bp] });
      break;
    }
  }
  return views;
}

/** Các breakpoint vùng có banner. */
export function coveredBreakpoints(views: readonly AdView[]): Set<AdBreakpoint> {
  return new Set(views.flatMap((v) => v.breakpoints));
}

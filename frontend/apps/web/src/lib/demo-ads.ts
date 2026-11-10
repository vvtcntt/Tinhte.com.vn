import type { PublicAdSlot } from './contracts.ts';
import { HOME_AD_CODE_LIST, HOME_AD_CODES, HOME_AD_SIZES, type HomeAdCode } from './home-model.ts';

// Banner mẫu CHỈ để xem các vùng quảng cáo trang chủ trên máy dev (`TT_SITE_ENV=local`), bật bằng `/?demo-ads=1`
// (hoặc liệt kê mã: `/?demo-ads=home_top,home_mid`). Backend chưa có GET /ads/placements nên đây là cách duy nhất
// để thấy bố cục có banner. home.ts chỉ gọi khi env là local; route ảnh trả 404 ở mọi môi trường khác.

export const DEMO_ADS_PARAM = 'demo-ads';

/** Giá trị `?demo-ads=` → danh sách mã muốn hiện; null = không bật. */
export function parseDemoAds(value: string | string[] | undefined): HomeAdCode[] | null {
  const raw = Array.isArray(value) ? value[0] : value;
  if (raw === undefined) return null;
  if (raw === '' || raw === '1' || raw === 'all') return [...HOME_AD_CODE_LIST];
  const wanted = new Set(raw.split(',').map((s) => s.trim()));
  const codes = HOME_AD_CODE_LIST.filter((c) => wanted.has(c));
  return codes.length > 0 ? codes : null;
}

/** Slot mẫu đúng hình dạng PublicAdSlot; creative là SVG do route `/demo-ads/{code}.svg` sinh ra trên cùng origin. */
export function demoAdSlots(origin: string, codes: readonly HomeAdCode[]): PublicAdSlot[] {
  return codes.map((code) => ({
    code,
    label: 'Quảng cáo',
    creativeUrl: `${origin}/demo-ads/${code}.svg`,
    altText: `Banner mẫu ${code}`,
    targetUrl: `https://example.com/?placement=${code}`,
    ...HOME_AD_SIZES[code],
    rel: 'sponsored noopener',
  }));
}

const ZONE_MARK: Record<string, string> = {
  [HOME_AD_CODES.top]: '①',
  [HOME_AD_CODES.heroSide]: '②',
  [HOME_AD_CODES.mid]: '③',
  [HOME_AD_CODES.feed]: '④',
  [HOME_AD_CODES.sidebar]: '⑤',
  [HOME_AD_CODES.bottom]: '⑥',
};

/** SVG giữ chỗ ghi rõ vùng, mã, kích thước. `file` dạng `home_top.svg`; mã lạ → null. */
export function demoAdSvg(file: string): string | null {
  const code = HOME_AD_CODE_LIST.find((c) => file === `${c}.svg`);
  if (!code) return null;
  const { width: w, height: h } = HOME_AD_SIZES[code];
  const mark = ZONE_MARK[code.replace(/_mobile$/, '')] ?? '';
  const big = Math.round(Math.min(h * 0.24, w * 0.07, 30));
  const small = Math.round(Math.max(11, big * 0.55));
  const gap = Math.round(big * 0.75);
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">
<defs><pattern id="s" width="14" height="14" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="7" height="14" fill="#dfe8f5"/></pattern></defs>
<rect width="${w}" height="${h}" fill="#eef3fa"/><rect width="${w}" height="${h}" fill="url(#s)"/>
<rect x="1" y="1" width="${w - 2}" height="${h - 2}" fill="none" stroke="#1c6fc5" stroke-width="2" stroke-dasharray="8 6"/>
<g font-family="system-ui,-apple-system,'Segoe UI',Roboto,Arial,sans-serif" text-anchor="middle" fill="#123c69">
<text x="${w / 2}" y="${h / 2 - gap / 2}" font-size="${big}" font-weight="700">${mark} ${code}</text>
<text x="${w / 2}" y="${h / 2 + gap}" font-size="${small}" font-weight="600" fill="#5b6472">${w}×${h} · BANNER MẪU (LOCAL)</text>
</g></svg>`;
}

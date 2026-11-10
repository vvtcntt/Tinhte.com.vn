import type { PublicAdSlot } from '../../lib/contracts';
import type { AdPair } from '../../lib/home-model';
import { safeHttpUrl } from '../../lib/routes';

/**
 * Màn hình slot được hiện: `all` mọi nơi; `desktop` từ 768px; `mobile` dưới 768px; `wide` từ 1024px.
 * Mốc trùng với globals.css (`.ad-slot--{media}`).
 */
export type AdMedia = 'all' | 'desktop' | 'mobile' | 'wide';

// Media query của khoảng màn hình slot bị ẩn. Viết bằng min-width + `not` để hai nửa luôn bù nhau (không có độ rộng lẻ
// như 767.5px nào hiện cả hai hay ẩn cả hai).
const HIDDEN_WHEN: Record<Exclude<AdMedia, 'all'>, string> = {
  desktop: 'not all and (min-width: 768px)',
  mobile: '(min-width: 768px)',
  wide: 'not all and (min-width: 1024px)',
};
// GIF 1×1 trong suốt. Ở màn hình slot bị ẩn, <picture> chọn ảnh này nên trình duyệt không tải creative —
// kể cả slot không lazy (①). CSP `img-src` đã cho phép `data:` (packages/sites securityHeaders).
const BLANK_GIF = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

// Slot quảng cáo tĩnh (P0-ADS, WBS-133). Chỉ render khi API trả slot → cờ OFF không để lại khung trống.
// SSR với width/height + aspect-ratio → giữ chỗ trước khi ảnh tải, không gây CLS. Creative hiện đúng kích thước
// (không phóng to), thu nhỏ khi khung hẹp hơn.
export function AdSlot({
  slot,
  media = 'all',
  eager = false,
  className = '',
}: {
  slot: PublicAdSlot | undefined;
  media?: AdMedia;
  /** Slot ở màn hình đầu (①): không lazy-load. Không đặt fetchpriority cao để không tranh với ảnh LCP. */
  eager?: boolean;
  className?: string;
}) {
  if (!slot) return null;
  const creative = safeHttpUrl(slot.creativeUrl);
  if (!creative) return null;
  const target = safeHttpUrl(slot.targetUrl, { httpsOnly: true });
  // Nhãn luôn hiện (FR-ADS-001): API không gửi nhãn thì dùng nhãn mặc định.
  const label = slot.label?.trim() || 'Quảng cáo';
  const hiddenWhen = media === 'all' ? null : HIDDEN_WHEN[media];
  const picture = (
    <picture>
      {hiddenWhen && <source media={hiddenWhen} srcSet={BLANK_GIF} />}
      <img
        src={creative}
        alt={slot.altText}
        width={slot.width}
        height={slot.height}
        loading={eager ? 'eager' : 'lazy'}
        decoding="async"
        style={{ aspectRatio: `${slot.width} / ${slot.height}` }}
      />
    </picture>
  );
  const box = { maxWidth: slot.width };
  return (
    <aside className={`ad-slot ad-slot--${media} ${className}`.trim()} aria-label={label} data-placement={slot.code}>
      <span className="ad-slot__label">{label}</span>
      {target ? (
        <a className="ad-slot__media" style={box} href={target} target="_blank" rel="sponsored noopener">
          {picture}
        </a>
      ) : (
        <div className="ad-slot__media" style={box}>
          {picture}
        </div>
      )}
    </aside>
  );
}

/** Một vùng có bản desktop (≥ 768px) và mobile (< 768px) — hai mã riêng vì mỗi mã chỉ một kích thước. */
export function AdPairSlot({ pair, eager = false, className = '' }: { pair: AdPair; eager?: boolean; className?: string }) {
  return (
    <>
      <AdSlot slot={pair.desktop} media="desktop" eager={eager} className={className} />
      <AdSlot slot={pair.mobile} media="mobile" eager={eager} className={className} />
    </>
  );
}

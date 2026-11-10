import { AD_BREAKPOINTS, AD_RANGE, type AdView } from '../../lib/ads';
import { safeHttpUrl } from '../../lib/routes';

// GIF 1×1 trong suốt. Ở màn hình khung bị ẩn, <picture> chọn ảnh này nên trình duyệt không tải creative —
// kể cả khung không lazy (①). CSP `img-src` đã cho phép `data:` (packages/sites securityHeaders).
const BLANK_GIF = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

/**
 * Một khung quảng cáo tĩnh (P0-ADS, WBS-133): một banner hiện ở các breakpoint của nó, ẩn ở breakpoint khác bằng CSS
 * (`.ad-slot--no-{mobile|tablet|desktop}`, cùng mốc với lib/ads.ts AD_RANGE). SSR với width/height + aspect-ratio → giữ chỗ trước khi
 * ảnh tải, không gây CLS. Creative hiện đúng kích thước (không phóng to), thu nhỏ khi khung hẹp hơn.
 */
export function AdSlot({
  view,
  eager = false,
  className = '',
}: {
  view: AdView;
  /** Khung ở màn hình đầu (①): không lazy-load. Không đặt fetchpriority cao để không tranh với ảnh LCP. */
  eager?: boolean;
  className?: string;
}) {
  const { creative, breakpoints } = view;
  // Đã lọc ở acceptAdSlots; kiểm lại để component không bao giờ render URL lạ dù được gọi từ chỗ khác.
  const src = safeHttpUrl(creative.creativeUrl);
  if (!src) return null;
  const target = safeHttpUrl(creative.targetUrl, { httpsOnly: true });
  // Nhãn luôn hiện (FR-ADS-001): API không gửi nhãn thì dùng nhãn mặc định.
  const label = view.label.trim() || 'Quảng cáo';
  const hidden = AD_BREAKPOINTS.filter((bp) => !breakpoints.includes(bp));
  const hiddenWhen = hidden.map((bp) => AD_RANGE[bp]).join(', ');
  const classes = ['ad-slot', ...hidden.map((bp) => `ad-slot--no-${bp.toLowerCase()}`), className].filter(Boolean).join(' ');
  const picture = (
    <picture>
      {hiddenWhen && <source media={hiddenWhen} srcSet={BLANK_GIF} />}
      <img
        src={src}
        alt={creative.altText}
        width={creative.width}
        height={creative.height}
        loading={eager ? 'eager' : 'lazy'}
        decoding="async"
        style={{ aspectRatio: `${creative.width} / ${creative.height}` }}
      />
    </picture>
  );
  const box = { maxWidth: creative.width };
  return (
    <aside className={classes} aria-label={label} data-placement={view.code} data-breakpoints={breakpoints.join(' ').toLowerCase()}>
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

/**
 * Một vùng quảng cáo (①–⑥, cuối bài): mỗi banner khác nhau một khung, mỗi breakpoint tối đa một khung hiện (lib/ads.ts resolveAdZone).
 * Rỗng → không render gì (không khung trống, FR-ADS-002).
 */
export function AdZone({ views, eager = false, className = '' }: { views: readonly AdView[]; eager?: boolean; className?: string }) {
  return (
    <>
      {views.map((view) => (
        <AdSlot key={`${view.code}:${view.breakpoints.join('-')}`} view={view} eager={eager} className={className} />
      ))}
    </>
  );
}

import type { PublicAdSlot } from '../../lib/contracts';
import { safeHttpUrl } from '../../lib/routes';

// Slot quảng cáo tĩnh (P0-ADS, WBS-133). Chỉ render khi API trả slot → cờ OFF không để lại khung trống.
// SSR với aspect-ratio từ width/height → giữ chỗ trước khi ảnh tải, không gây CLS.
export function AdSlot({ slot, className = '' }: { slot: PublicAdSlot | undefined; className?: string }) {
  if (!slot) return null;
  const creative = safeHttpUrl(slot.creativeUrl);
  if (!creative) return null;
  const target = safeHttpUrl(slot.targetUrl, { httpsOnly: true });
  const image = (
    <img
      src={creative}
      alt={slot.altText}
      width={slot.width}
      height={slot.height}
      loading="lazy"
      decoding="async"
      style={{ aspectRatio: `${slot.width} / ${slot.height}` }}
    />
  );
  return (
    <aside className={`ad-slot ${className}`} aria-label={slot.label} data-placement={slot.code}>
      <span className="ad-slot__label">{slot.label}</span>
      {target ? (
        <a href={target} target="_blank" rel="sponsored noopener">
          {image}
        </a>
      ) : (
        image
      )}
    </aside>
  );
}

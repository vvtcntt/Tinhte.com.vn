import type { ReactNode } from 'react';
import type { ContentKind } from '../../lib/contracts';
import { fullDateTime, relativeTime } from '../../lib/format';
import { safeHttpUrl } from '../../lib/routes';

/** Nhãn tài trợ: bắt buộc với SPONSORED, không có tùy chọn ẩn (ContentKind, FR-ADS-001, FR-NEWS-008). */
export function SponsorBadge({ kind, label }: { kind: ContentKind; label?: string | null }) {
  if (kind !== 'SPONSORED') return null;
  return <span className="sponsor-badge">{label}</span>;
}

export function TimeAgo({ iso }: { iso: string }) {
  return (
    <time dateTime={iso} title={fullDateTime(iso)}>
      {relativeTime(iso)}
    </time>
  );
}

/** Ảnh có tỉ lệ cố định; null/URL không an toàn → nền thay thế, bố cục không đổi. */
export function Thumb({
  src,
  ratio,
  priority = false,
  sizes,
}: {
  src?: string | null;
  ratio: '16/9' | '9/16';
  priority?: boolean;
  sizes?: string;
}) {
  const url = safeHttpUrl(src);
  return (
    <div className={`thumb thumb--${ratio.replace('/', 'x')}`}>
      {url ? (
        <img
          src={url}
          alt=""
          sizes={sizes}
          loading={priority ? 'eager' : 'lazy'}
          fetchPriority={priority ? 'high' : 'auto'}
          decoding="async"
        />
      ) : (
        <span className="thumb__empty" aria-hidden="true" />
      )}
    </div>
  );
}

export function SectionHeading({ id, title, action }: { id: string; title: string; action?: ReactNode }) {
  return (
    <div className="section-heading">
      <h2 id={id}>{title}</h2>
      {action}
    </div>
  );
}

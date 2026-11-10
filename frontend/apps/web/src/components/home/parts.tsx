import type { ReactNode } from 'react';
import type { ArticleSummary, ContentKind } from '../../lib/contracts';
import { fullDateTime, relativeTime } from '../../lib/format';
import { safeHttpUrl } from '../../lib/routes';
import { LogoMark } from '../Logo';

/** Nhãn tài trợ: bắt buộc với SPONSORED, không có tùy chọn ẩn (ContentKind, FR-ADS-001, FR-NEWS-008). */
export function SponsorBadge({ kind, label }: { kind: ContentKind; label?: string | null }) {
  if (kind !== 'SPONSORED') return null;
  return <span className="sponsor-badge">{label}</span>;
}

/**
 * Dòng nhỏ phía trên tiêu đề thẻ bài: tên chuyên mục (chữ thường, KHÔNG phải link — cả thẻ đã là một link, link lồng nhau không hợp lệ)
 * và nhãn tài trợ (luôn hiện khi SPONSORED, không có props để ẩn). Không có gì để hiện → không render.
 */
export function StoryKicker({ article, showCategory = true }: { article: ArticleSummary; showCategory?: boolean }) {
  const category = showCategory ? article.category?.name?.trim() : '';
  if (!category && article.contentKind !== 'SPONSORED') return null;
  return (
    <p className="story-kicker">
      {category && <span className="story-kicker__category">{category}</span>}
      <SponsorBadge kind={article.contentKind} label={article.sponsorLabel} />
    </p>
  );
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
        <span className="thumb__empty" aria-hidden="true">
          <LogoMark className="thumb__mark" />
        </span>
      )}
    </div>
  );
}

export function SectionHeading({ id, title, action }: { id: string; title: string; action?: ReactNode }) {
  return (
    <div className="section-heading">
      <h2 id={id}>{title}</h2>
      {action && <div className="section-heading__actions">{action}</div>}
    </div>
  );
}

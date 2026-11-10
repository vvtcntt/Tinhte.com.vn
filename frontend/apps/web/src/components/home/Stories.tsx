import Link from 'next/link';
import type { ArticleSummary } from '../../lib/contracts';
import { routes } from '../../lib/routes';
import { StoryKicker, Thumb, TimeAgo } from './parts';

/** Bài chính của vùng Tin nổi bật — ảnh là ứng viên LCP nên tải ưu tiên. */
export function LeadStory({ article }: { article: ArticleSummary }) {
  return (
    <article className="lead-story">
      <Link href={routes.article(article.slug)} className="card-link">
        <Thumb src={article.thumbnailUrl} ratio="16/9" priority sizes="(min-width: 1024px) 820px, 100vw" />
        <div className="lead-story__body">
          <StoryKicker article={article} />
          <h3 className="lead-story__title">{article.title}</h3>
          {article.summary && <p className="lead-story__summary">{article.summary}</p>}
          <p className="meta">
            <TimeAgo iso={article.publishedAt} />
          </p>
        </div>
      </Link>
    </article>
  );
}

/**
 * `compact`: tin phụ nổi bật (ảnh nhỏ, không sapo). `row`: danh sách Tin mới (có sapo).
 * `showCategory={false}` cho trang của chính chuyên mục đó (nhãn lặp lại vô ích).
 */
export function StoryCard({ article, variant, showCategory = true }: { article: ArticleSummary; variant: 'compact' | 'row'; showCategory?: boolean }) {
  return (
    <article className={`story story--${variant}`}>
      <Link href={routes.article(article.slug)} className="card-link">
        <Thumb src={article.thumbnailUrl} ratio="16/9" sizes="(min-width: 768px) 240px, 120px" />
        <div className="story__body">
          <StoryKicker article={article} showCategory={showCategory} />
          <h3 className="story__title">{article.title}</h3>
          {variant === 'row' && article.summary && <p className="story__summary">{article.summary}</p>}
          <p className="meta">
            <TimeAgo iso={article.publishedAt} />
          </p>
        </div>
      </Link>
    </article>
  );
}

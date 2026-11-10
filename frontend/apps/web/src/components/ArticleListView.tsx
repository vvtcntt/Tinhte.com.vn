import Link from 'next/link';
import type { ReactNode } from 'react';
import type { ArticleSummary } from '../lib/contracts';
import { StoryCard } from './home/Stories';

/**
 * Khung trang danh sách bài (Tin mới, chuyên mục): tiêu đề, danh sách thẻ, "Trang sau" theo cursor.
 * Phân trang bằng link thường (SSR, không cần JS); "Về trang đầu" khi đang ở trang sau.
 */
export function ArticleListView({
  title,
  intro,
  breadcrumb,
  items,
  basePath,
  nextCursor,
  isFirstPage,
  demo,
}: {
  title: string;
  intro?: ReactNode;
  breadcrumb?: ReactNode;
  items: ArticleSummary[];
  basePath: string;
  nextCursor: string | null;
  isFirstPage: boolean;
  demo: boolean;
}) {
  return (
    <div className="container list-page">
      {demo && (
        <p className="demo-banner" role="note">
          <strong>Dữ liệu mẫu</strong> — API chưa phản hồi. Chỉ hiện ở môi trường local.
        </p>
      )}
      {breadcrumb}
      <header className="list-page__header">
        <h1 className="list-page__title">{title}</h1>
        {intro}
      </header>

      {items.length === 0 ? (
        <div className="state-block">
          <h2>Chưa có bài viết</h2>
          <p>{isFirstPage ? 'Mục này chưa có bài nào được xuất bản.' : 'Không còn bài nào ở trang này.'}</p>
          {!isFirstPage && (
            <Link className="button" href={basePath}>
              Về trang đầu
            </Link>
          )}
        </div>
      ) : (
        <div className="list-page__items">
          {items.map((a) => (
            <StoryCard key={a.id} article={a} variant="row" />
          ))}
        </div>
      )}

      {(nextCursor || !isFirstPage) && (
        <nav className="pager" aria-label="Phân trang">
          {!isFirstPage && (
            <Link className="button" href={basePath}>
              ← Trang đầu
            </Link>
          )}
          {nextCursor && (
            <Link className="button" href={`${basePath}?cursor=${encodeURIComponent(nextCursor)}`} rel="next">
              Trang sau →
            </Link>
          )}
        </nav>
      )}
    </div>
  );
}

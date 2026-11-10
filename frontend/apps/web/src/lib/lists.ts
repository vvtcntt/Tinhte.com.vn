import { parseSiteEnv, type SiteDefinition } from '@tinhte/sites';
import { ApiError, apiRequest } from './api';
import type { ArticleSummary } from './contracts';
import { demoHome } from './demo-home';
import { isDisplayable } from './home';

// Danh sách bài công khai: /articles (Tin mới — listPublicArticles) và /categories/{slug} (getCategoriesSlugArticles; WBS-028).

export const LIST_PAGE_SIZE = 20;
const SLUG = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
/** Cursor mờ của API (HMAC, ≤ 512 ký tự) — chỉ kiểm hình dạng, API tự kiểm chữ ký. */
const CURSOR = /^[A-Za-z0-9_.-]{1,512}$/;

export type ListResult =
  | { kind: 'ok'; items: ArticleSummary[]; nextCursor: string | null; demo: boolean }
  /** Cursor hỏng/hết hạn (400 INVALID_CURSOR) → về trang đầu. */
  | { kind: 'bad-cursor' }
  | { kind: 'not-found' }
  | { kind: 'error' };

export function isValidSlug(slug: string): boolean {
  return slug.length <= 120 && SLUG.test(slug);
}

export async function loadArticleList(site: SiteDefinition, opts: { category?: string; cursor?: string }): Promise<ListResult> {
  if (opts.category !== undefined && !isValidSlug(opts.category)) return { kind: 'not-found' };
  if (opts.cursor !== undefined && !CURSOR.test(opts.cursor)) return { kind: 'bad-cursor' };

  const query = new URLSearchParams({ limit: String(LIST_PAGE_SIZE) });
  if (opts.cursor) query.set('cursor', opts.cursor);
  const path = opts.category ? `/categories/${encodeURIComponent(opts.category)}/articles?${query}` : `/articles?${query}`;

  let response: Response;
  try {
    response = await apiRequest(site, path, { revalidate: 30 });
  } catch (error) {
    if (error instanceof ApiError && parseSiteEnv(process.env.TT_SITE_ENV) === 'local') {
      // Chỉ máy dev, API tắt: dữ liệu mẫu một trang, không phân trang.
      const home = demoHome();
      const items = [...home.featuredArticles, ...home.recentArticles].sort((a, b) => b.publishedAt.localeCompare(a.publishedAt));
      return { kind: 'ok', items, nextCursor: null, demo: true };
    }
    return { kind: 'error' };
  }

  if (response.status === 404) return { kind: 'not-found' };
  if (response.status === 400 && opts.cursor) return { kind: 'bad-cursor' };
  if (!response.ok) return { kind: 'error' };
  const body = (await response.json()) as { items: ArticleSummary[]; nextCursor?: string | null };
  return { kind: 'ok', items: body.items.filter(isDisplayable), nextCursor: body.nextCursor ?? null, demo: false };
}

/** Đọc ?cursor= từ searchParams của Next (chuỗi đơn; mảng/rỗng → bỏ qua). */
export function cursorParam(value: string | string[] | undefined): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

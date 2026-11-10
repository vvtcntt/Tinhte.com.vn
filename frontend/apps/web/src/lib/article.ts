import { cache } from 'react';
import { parseSiteEnv, type SiteDefinition } from '@tinhte/sites';
import { ApiError, apiGet, apiRequest } from './api';
import { acceptAdSlots, AD_BREAKPOINTS, resolveAdZone, type AdView } from './ads';
import type { Article, ArticleSummary, HomePage, PublicAdSlotList } from './contracts';
import { demoHome } from './demo-home';
import { isDisplayable } from './home';

// Trang bài công khai (UX-PUB-03, FR-NEWS-001/002, FR-DISC-007). Thiết kế: doc/ux/UX-PUB-03_TRANG_BAI_v0.1_DRAFT.md

/** Slug hợp lệ (FR-NEWS-002, CHECK ck_articles_slug_format): chặn đường dẫn rác trước khi gọi API. */
const SLUG = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
export const ARTICLE_AD_CODE = 'article_bottom';
export const RELATED_LIMIT = 6;

export type ArticleResult =
  | { kind: 'ok'; article: Article; demo: boolean }
  | { kind: 'moved'; slug: string }
  /** 404 hoặc 410 (đã gỡ / hết quyền): cả hai hiển thị trang không tìm thấy, HTTP 404 — không lộ nội dung đã gỡ. */
  | { kind: 'not-found' }
  | { kind: 'error' };

/** Một lần gọi cho cả generateMetadata và trang (React cache theo request). */
export const loadArticle = cache(async (site: SiteDefinition, slug: string): Promise<ArticleResult> => {
  if (slug.length > 250 || !SLUG.test(slug)) return { kind: 'not-found' };
  const path = `/articles/${encodeURIComponent(slug)}`;
  let response: Response;
  try {
    response = await apiRequest(site, path);
  } catch (error) {
    if (error instanceof ApiError && parseSiteEnv(process.env.TT_SITE_ENV) === 'local') {
      const demo = demoArticle(slug);
      return demo ? { kind: 'ok', article: demo, demo: true } : { kind: 'not-found' };
    }
    return { kind: 'error' };
  }

  if (response.status === 308 || response.status === 301) {
    // Slug cũ → API trả Location tương đối /api/v1/articles/{slug-mới} (cùng origin, ADR-0001).
    const next = response.headers.get('location')?.split('/').filter(Boolean).at(-1);
    const target = next ? decodeURIComponent(next) : null;
    return target && SLUG.test(target) && target !== slug ? { kind: 'moved', slug: target } : { kind: 'not-found' };
  }
  if (response.status === 404 || response.status === 410) return { kind: 'not-found' };
  if (!response.ok) return { kind: 'error' };

  const article = (await response.json()) as Article;
  // Fail-closed như trang chủ: SPONSORED thiếu nhãn hoặc contentKind lạ → không hiển thị.
  if (!isDisplayable(article)) return { kind: 'not-found' };
  return { kind: 'ok', article: { ...article, bodyBlocks: Array.isArray(article.bodyBlocks) ? article.bodyBlocks : [] }, demo: false };
});

/** Tin mới để "Đọc thêm" (từ GET /home, đã lọc ở backend). Không có danh sách liên quan theo chủ đề trong hợp đồng v0.3. */
export async function loadRelated(site: SiteDefinition, excludeId: string, demo: boolean): Promise<ArticleSummary[]> {
  let home: HomePage | null = null;
  if (demo) home = demoHome();
  else {
    try {
      home = await apiGet<HomePage>(site, '/home');
    } catch {
      return [];
    }
  }
  return [...home.featuredArticles, ...home.recentArticles]
    .filter((a) => a.id !== excludeId && isDisplayable(a))
    .sort((a, b) => b.publishedAt.localeCompare(a.publishedAt))
    .slice(0, RELATED_LIMIT);
}

/** Slot cuối bài: một mã cho mọi màn hình, banner theo breakpoint (lib/ads.ts). Lỗi/cờ OFF/không banner → không khung. */
export async function loadArticleAd(site: SiteDefinition): Promise<AdView[]> {
  try {
    const list = await apiGet<PublicAdSlotList>(site, `/ads/placements?placementCodes=${ARTICLE_AD_CODE}`);
    return resolveAdZone(acceptAdSlots(list.items, [ARTICLE_AD_CODE]), [{ code: ARTICLE_AD_CODE, breakpoints: AD_BREAKPOINTS }]);
  } catch {
    return [];
  }
}

/** Chỉ máy dev khi API tắt: dựng bài từ dữ liệu mẫu của trang chủ để link trên trang chủ mẫu không gãy. */
function demoArticle(slug: string): Article | null {
  const home = demoHome();
  const found = [...home.featuredArticles, ...home.recentArticles].find((a) => a.slug === slug);
  if (!found) return null;
  return {
    ...found,
    revisionId: '00000000-0000-4000-8000-000000000000',
    authorDisplayName: 'Ban biên tập (mẫu)',
    categorySlug: 'dien-thoai',
    relatedVideoIds: [],
    bodyBlocks: [
      { type: 'paragraph', text: found.summary ?? '' },
      { type: 'heading', level: 2, text: 'Điểm chính' },
      { type: 'paragraph', text: 'Đây là nội dung mẫu để dựng giao diện khi API chưa chạy. Không phải bài thật.' },
      { type: 'quote', text: 'Trích dẫn mẫu: khối quote hiển thị khác đoạn văn thường.' },
      { type: 'heading', level: 3, text: 'Tiêu đề cấp 3' },
      { type: 'paragraph', text: 'Đoạn có xuống dòng.\nDòng thứ hai giữ nguyên ngắt dòng của biên tập viên.' },
    ],
  };
}

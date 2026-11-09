// Kiểu DTO public theo OpenAPI v0.3 DRAFT (doc/07_AI_REVIEW/v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml).
// Contract-first: đổi ở đây chỉ sau khi đã sửa spec.

export type ContentKind = 'EDITORIAL' | 'SPONSORED';

export interface ArticleSummary {
  id: string;
  slug: string;
  title: string;
  summary?: string | null;
  publishedAt: string;
  thumbnailUrl?: string | null;
  contentKind: ContentKind;
  sponsorLabel?: string | null;
}

export interface VideoSummary {
  id: string;
  slug: string;
  title: string;
  description?: string | null;
  publishedAt: string;
  posterUrl?: string | null;
  durationMs?: number | null;
  contentKind: ContentKind;
  sponsorLabel?: string | null;
}

/** Khối nội dung bài (bodyBlocks). Hiện CMS soạn: paragraph, heading (level 2|3), quote. Kiểu khác: bỏ qua khi hiển thị. */
export type BodyBlock = { type: string; text?: string; level?: number; [key: string]: unknown };

/** Article (getPublicArticle; FR-NEWS-001). */
export interface Article extends ArticleSummary {
  revisionId: string;
  authorDisplayName?: string | null;
  categorySlug?: string | null;
  bodyBlocks: BodyBlock[];
  relatedVideoIds: string[];
}

/** HomePage (FR-DISC-003). */
export interface HomePage {
  featuredArticles: ArticleSummary[];
  recentArticles: ArticleSummary[];
  featuredVideos: VideoSummary[];
}

export interface Category {
  id: string;
  slug: string;
  name: string;
  isEnabled?: boolean;
}

export interface CategoryList {
  items: Category[];
}

/** PublicAdSlot (AIR-013): không có trạng thái/lịch nội bộ. */
export interface PublicAdSlot {
  code: string;
  label: string;
  creativeUrl: string;
  altText: string;
  targetUrl?: string | null;
  width: number;
  height: number;
  rel: 'sponsored noopener';
}

export interface PublicAdSlotList {
  items: PublicAdSlot[];
}

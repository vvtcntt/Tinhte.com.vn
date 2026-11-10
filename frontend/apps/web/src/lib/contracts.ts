// Kiểu DTO public theo OpenAPI v0.3 DRAFT (doc/07_AI_REVIEW/v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml).
// Contract-first: đổi ở đây chỉ sau khi đã sửa spec.

export type ContentKind = 'EDITORIAL' | 'SPONSORED';

/** CategoryRef (DRAFT đợt 3): chuyên mục tối thiểu trên thẻ bài. */
export interface CategoryRef {
  id: string;
  slug: string;
  name: string;
}

export interface ArticleSummary {
  id: string;
  slug: string;
  title: string;
  summary?: string | null;
  publishedAt: string;
  thumbnailUrl?: string | null;
  contentKind: ContentKind;
  sponsorLabel?: string | null;
  /** DRAFT đợt 3: chuyên mục theo bản đã xuất bản; null/thiếu → thẻ không có nhãn chuyên mục. */
  category?: CategoryRef | null;
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

/** AdBreakpoint: MOBILE < 768px, TABLET 768–1023px, DESKTOP ≥ 1024px. */
export type AdBreakpoint = 'MOBILE' | 'TABLET' | 'DESKTOP';

/** PublicAdCreative: một banner cho một breakpoint; width/height là kích thước thật để giữ chỗ (không CLS). */
export interface PublicAdCreative {
  breakpoint: AdBreakpoint;
  creativeUrl: string;
  altText: string;
  targetUrl?: string | null;
  width: number;
  height: number;
}

/** PublicAdSlot (AIR-013; DRAFT đợt 3): không có trạng thái/lịch/đặt chỗ nội bộ, tối đa một banner mỗi breakpoint. */
export interface PublicAdSlot {
  code: string;
  label: string;
  creatives: PublicAdCreative[];
  rel: 'sponsored noopener';
}

export interface PublicAdSlotList {
  items: PublicAdSlot[];
}

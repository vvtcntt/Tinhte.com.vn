// Kiểu + hàm thuần cho màn Bài viết (WBS-030/031) — theo schema AdminArticle/ArticleRevision của hợp đồng v0.3.

export interface AdminArticle {
  id: string;
  title: string;
  status: string;
  contentKind: 'EDITORIAL' | 'SPONSORED' | string;
  workingRevisionId: string | null;
  publicRevisionId: string | null;
  ownerId?: string;
  assigneeId: string | null;
  rightsStatus: string;
  rightsExpiresAt: string | null;
  scheduledAt: string | null;
  updatedAt: string;
  rowVersion: number;
  slug: string;
}

export interface ArticleRevision {
  revisionId: string;
  articleId: string;
  number: number;
  authorId?: string;
  status: string;
  createdAt: string;
  bodyBlocks?: Block[];
  title: string;
  contentKind: string;
  changeReason: string | null;
  summary: string | null;
  categoryId: string | null;
  updatedAt: string;
  publishedAt: string | null;
  diff?: { compareTo: string; changes: { path: string; before: unknown; after: unknown }[] };
}

export interface Category {
  id: string;
  slug: string;
  name: string;
  isEnabled: boolean;
  rowVersion: number;
}

export type Block = { type: string; text?: string; level?: number; [key: string]: unknown };

export const ARTICLE_STATUSES = ['DRAFT', 'IN_REVIEW', 'CHANGES_REQUESTED', 'REJECTED', 'APPROVED', 'SCHEDULED', 'PUBLISHED', 'UNPUBLISHED', 'RETRACTED'] as const;

const STATUS_LABEL: Record<string, string> = {
  DRAFT: 'Nháp',
  IN_REVIEW: 'Chờ duyệt',
  CHANGES_REQUESTED: 'Cần sửa',
  REJECTED: 'Bị từ chối',
  APPROVED: 'Đã duyệt',
  SCHEDULED: 'Đã lên lịch',
  PUBLISHED: 'Đã xuất bản',
  UNPUBLISHED: 'Đã gỡ',
  RETRACTED: 'Đã rút',
  SUPERSEDED: 'Đã thay thế',
};

export function statusLabel(status: string): string {
  return STATUS_LABEL[status] ?? status;
}

export function statusTone(status: string): 'ok' | 'warn' | '' {
  if (status === 'PUBLISHED' || status === 'APPROVED') return 'ok';
  if (status === 'CHANGES_REQUESTED' || status === 'REJECTED' || status === 'UNPUBLISHED' || status === 'RETRACTED') return 'warn';
  return '';
}

/** Trạng thái sửa được bằng PATCH (khớp ArticleStatuses.Editable ở backend); còn lại phải qua workflow duyệt (WBS-024). */
export const EDITABLE_STATUSES: ReadonlySet<string> = new Set(['DRAFT', 'CHANGES_REQUESTED', 'REJECTED', 'PUBLISHED', 'UNPUBLISHED']);

/** Quyền sửa theo dữ liệu (giống ArticleService.RequireEditPermission): bài của mình → write_own; bài người khác → write_any. */
export function canEditArticle(
  can: (permission: 'content.write_own' | 'content.write_any') => boolean,
  userId: string,
  article: Pick<AdminArticle, 'ownerId'>,
): boolean {
  return can('content.write_any') || (can('content.write_own') && article.ownerId === userId);
}

// ------------------------------------------------------------ nội dung: văn bản ↔ khối
// Trình soạn tạm (trước khi có editor rich-text theo UX-CMS-02): đoạn cách nhau bằng dòng trống,
// "## " / "### " là tiêu đề, "> " là trích dẫn. Khối kiểu khác (ảnh, nhúng…) → không chuyển được, chỉ xem.

const KNOWN = new Set(['paragraph', 'heading', 'quote']);

export function textToBlocks(text: string): Block[] {
  return text
    .replace(/\r\n/g, '\n')
    .split(/\n{2,}/)
    .map((chunk) => chunk.trim())
    .filter((chunk) => chunk.length > 0)
    .map((chunk): Block => {
      const heading = /^(#{2,3})\s+(.*)$/s.exec(chunk);
      if (heading) return { type: 'heading', level: heading[1].length, text: heading[2].trim() };
      if (chunk.startsWith('> ')) return { type: 'quote', text: chunk.replace(/^> ?/gm, '').trim() };
      return { type: 'paragraph', text: chunk };
    });
}

export function blocksToText(blocks: readonly Block[]): { text: string; supported: boolean } {
  const supported = blocks.every((b) => KNOWN.has(b.type) && typeof b.text === 'string');
  if (!supported) return { text: '', supported: false };
  const text = blocks
    .map((b) => {
      if (b.type === 'heading') return `${'#'.repeat(b.level === 3 ? 3 : 2)} ${b.text}`;
      if (b.type === 'quote') return (b.text as string).split('\n').map((line) => `> ${line}`).join('\n');
      return b.text as string;
    })
    .join('\n\n');
  return { text, supported: true };
}

export function formatTime(value: string | null | undefined): string {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('vi-VN', { dateStyle: 'short', timeStyle: 'short' });
}

/** Giá trị trong diff hiển thị gọn (chuỗi giữ nguyên, object → JSON rút gọn). */
export function diffValue(value: unknown): string {
  if (value === null || value === undefined) return '(trống)';
  if (typeof value === 'string') return value;
  const json = JSON.stringify(value);
  return json.length > 300 ? `${json.slice(0, 300)}…` : json;
}

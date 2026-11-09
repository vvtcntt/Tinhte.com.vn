// Hàng đợi duyệt bài (WBS-031: "Role filtered lists and actions") — định nghĩa thuần, dựng trên getAdminArticles
// (lọc status / ownerId / assigneeId, sắp updatedAt_asc = chờ lâu nhất trước). Quyền chỉ để ẩn/hiện; API vẫn kiểm.
import type { Access, PermissionKey } from './rbac.ts';

export type QueueKey = 'to-review' | 'to-fix' | 'to-publish' | 'my-submitted';

export interface QueueDef {
  key: QueueKey;
  label: string;
  description: string;
  /** Tham số query của GET /admin/articles (không gồm cursor/limit). */
  params: (userId: string) => Record<string, string>;
  /** Tab này có hiện với phiên hiện tại không. */
  visible: (can: (p: PermissionKey) => Access) => boolean;
  /** Đếm vào huy hiệu trên menu (việc đang chờ chính người này xử lý). */
  actionable: boolean;
  empty: string;
}

const writer = (can: (p: PermissionKey) => Access) => can('content.write_own') !== 'denied' || can('content.write_any') !== 'denied';

export const QUEUES: readonly QueueDef[] = [
  {
    key: 'to-review',
    label: 'Chờ duyệt',
    description: 'Phiên bản đã gửi duyệt, chờ lâu nhất ở trên cùng. Bạn không duyệt được bài của chính mình.',
    params: () => ({ status: 'IN_REVIEW', sort: 'updatedAt_asc' }),
    visible: (can) => can('content.review') !== 'denied' || can('content.approve') !== 'denied',
    actionable: true,
    empty: 'Không có bài nào đang chờ duyệt.',
  },
  {
    key: 'to-fix',
    label: 'Cần tôi sửa',
    description: 'Bài được trả lại hoặc bị từ chối mà bạn là người phụ trách tiếp theo. Sửa rồi gửi duyệt lại.',
    params: (userId) => ({ status: 'CHANGES_REQUESTED,REJECTED', assigneeId: userId, sort: 'updatedAt_asc' }),
    visible: writer,
    actionable: true,
    empty: 'Không có bài nào cần bạn sửa.',
  },
  {
    key: 'to-publish',
    label: 'Chờ xuất bản',
    description: 'Phiên bản đã được duyệt, chưa xuất bản.',
    params: () => ({ status: 'APPROVED', sort: 'updatedAt_asc' }),
    visible: (can) => can('content.publish') !== 'denied',
    actionable: true,
    empty: 'Không có bài nào đã duyệt đang chờ xuất bản.',
  },
  {
    key: 'my-submitted',
    label: 'Tôi đã gửi',
    description: 'Bài của bạn đang chờ người khác duyệt.',
    params: (userId) => ({ status: 'IN_REVIEW', ownerId: userId, sort: 'updatedAt_desc' }),
    visible: writer,
    actionable: false,
    empty: 'Bạn không có bài nào đang chờ duyệt.',
  },
];

export function visibleQueues(can: (p: PermissionKey) => Access): QueueDef[] {
  return QUEUES.filter((q) => q.visible(can));
}

export function queueUrl(queue: QueueDef, userId: string, limit: number, cursor?: string | null): string {
  const params = new URLSearchParams({ ...queue.params(userId), limit: String(limit) });
  if (cursor) params.set('cursor', cursor);
  return `/admin/articles?${params}`;
}

/** Số hiển thị trên huy hiệu: đếm tối đa một trang; còn trang sau thì "99+". */
export function badgeText(count: number, hasMore: boolean, max = 99): string | null {
  if (count <= 0) return null;
  return hasMore || count > max ? `${max}+` : String(count);
}

/** "chờ 3 giờ", "chờ 2 ngày" — thời gian từ lần cập nhật gần nhất (gửi duyệt, trả bài…). */
export function waitingFor(since: string, now: Date = new Date()): string {
  const ms = now.getTime() - new Date(since).getTime();
  if (!Number.isFinite(ms) || ms < 0) return 'vừa xong';
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 1) return 'vừa xong';
  if (minutes < 60) return `${minutes} phút`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} giờ`;
  return `${Math.floor(hours / 24)} ngày`;
}

/** Chờ quá lâu (mặc định 24 giờ) → tô cảnh báo. SLA thật do PO chốt. */
export function isOverdue(since: string, now: Date = new Date(), hours = 24): boolean {
  return now.getTime() - new Date(since).getTime() > hours * 3_600_000;
}

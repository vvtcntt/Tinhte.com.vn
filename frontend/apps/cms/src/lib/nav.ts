import type { PermissionKey } from './rbac.ts';

export interface Section {
  href: string;
  label: string;
  /** Quyền tối thiểu để thấy mục này. null = mọi staff. */
  permission: PermissionKey | null;
  wbs: string;
  summary: string;
}

// Các màn hình theo WBS. Chỉ "Tổng quan" có nội dung ở bước này; còn lại giữ chỗ để kiểm điều hướng + phân quyền.
export const SECTIONS: Section[] = [
  { href: '/', label: 'Tổng quan', permission: null, wbs: 'WBS-031', summary: 'Phiên làm việc, vai trò và quyền của bạn.' },
  { href: '/articles', label: 'Bài viết', permission: 'content.read', wbs: 'WBS-030, WBS-031', summary: 'Soạn, lưu nháp, xem trước và hàng đợi duyệt bài.' },
  { href: '/review', label: 'Duyệt bài', permission: 'content.read', wbs: 'WBS-031', summary: 'Hàng đợi duyệt, trả bài và xuất bản theo vai trò.' },
  { href: '/videos', label: 'Video', permission: 'content.read', wbs: 'WBS-046', summary: 'Tải lên, theo dõi xử lý, thư viện media.' },
  { href: '/taxonomy', label: 'Chuyên mục & tag', permission: 'taxonomy.manage', wbs: 'WBS-032', summary: 'Chuyên mục, tag, nguồn và thông tin bản quyền.' },
  { href: '/moderation', label: 'Kiểm duyệt', permission: 'moderation.act', wbs: 'WBS-065', summary: 'Hàng đợi báo cáo và quyết định kiểm duyệt.' },
  { href: '/users', label: 'Tài khoản', permission: 'users.read', wbs: 'WBS-115', summary: 'Tài khoản staff, đình chỉ, cấp/thu hồi vai trò.' },
  { href: '/audit', label: 'Nhật ký thao tác', permission: 'audit.read', wbs: 'WBS-019, WBS-138', summary: 'Nhật ký các thao tác đặc quyền.' },
];

export function sectionFor(pathname: string): Section | undefined {
  return SECTIONS.find((s) => (s.href === '/' ? pathname === '/' : pathname === s.href || pathname.startsWith(`${s.href}/`)));
}

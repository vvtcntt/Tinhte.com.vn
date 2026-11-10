// Màn Quảng cáo (P0-ADS, FR-ADS-002, WBS-136) — phần thuần (không DOM) để kiểm bằng node:test.
// Hợp đồng: /admin/ad-placements*, /admin/ad-bookings/* (OpenAPI v0.3 mục 16/19 — DRAFT). Quyền ads.manage / ads.approve và vai trò AD_OPS
// là ĐỀ XUẤT trong config/rbac/rbac.json, chờ BA/PO duyệt. CMS chỉ ẩn/hiện theo quyền — API vẫn kiểm (401/403).
import { ApiError } from './api.ts';
import type { PermissionKey } from './rbac.ts';
import { fromLocalInput, toLocalInput } from './videos.ts';

// ---------------------------------------------------------------- kiểu theo hợp đồng

export const AD_STATUSES = ['DRAFT', 'APPROVED', 'ACTIVE', 'PAUSED', 'EXPIRED'] as const;
export type AdStatus = (typeof AD_STATUSES)[number];

/** AdBreakpoint: MOBILE < 768px, TABLET 768–1023px, DESKTOP ≥ 1024px (UX-PUB-01). */
export const AD_BREAKPOINTS = ['MOBILE', 'TABLET', 'DESKTOP'] as const;
export type AdBreakpoint = (typeof AD_BREAKPOINTS)[number];

export interface AdSize {
  breakpoint: AdBreakpoint;
  width: number;
  height: number;
}

export interface AdCreative extends AdSize {
  mediaAssetId: string;
  altText: string;
  /** Chỉ đọc: URL công khai khi media phục vụ được (READY + IMAGE + PUBLIC + quyền CLEARED còn hạn), ngược lại null. */
  creativeUrl?: string | null;
}

export interface AdPlacement {
  id: string;
  siteKey: string;
  code: string;
  label: string;
  status: AdStatus;
  sizes: AdSize[];
  approvedBy?: string | null;
  approvedAt?: string | null;
  rowVersion: number;
}

export interface AdBooking {
  id: string;
  placementId: string;
  status: AdStatus;
  priority: number;
  advertiserName: string | null;
  targetUrl: string | null;
  startsAt: string | null;
  endsAt: string | null;
  creatives: AdCreative[];
  approvedBy?: string | null;
  approvedAt?: string | null;
  rowVersion: number;
}

export interface ItemList<T> {
  items: T[];
}

// ---------------------------------------------------------------- nhãn

const STATUS_LABEL: Record<AdStatus, string> = {
  DRAFT: 'Nháp',
  APPROVED: 'Đã duyệt',
  ACTIVE: 'Đang chạy',
  PAUSED: 'Tạm dừng',
  EXPIRED: 'Đã kết thúc',
};

const BREAKPOINT_LABEL: Record<AdBreakpoint, string> = { MOBILE: 'Di động', TABLET: 'Máy tính bảng', DESKTOP: 'Máy tính' };
const BREAKPOINT_RANGE: Record<AdBreakpoint, string> = { MOBILE: '< 768px', TABLET: '768–1023px', DESKTOP: '≥ 1024px' };

export const adStatusLabel = (s: string): string => STATUS_LABEL[s as AdStatus] ?? s;
export const breakpointLabel = (b: string): string => BREAKPOINT_LABEL[b as AdBreakpoint] ?? b;
export const breakpointRange = (b: string): string => BREAKPOINT_RANGE[b as AdBreakpoint] ?? '';

export function adStatusTone(s: string): 'ok' | 'warn' | 'bad' | '' {
  return s === 'ACTIVE' ? 'ok' : s === 'APPROVED' || s === 'PAUSED' ? 'warn' : '';
}

// ---------------------------------------------------------------- vòng đời (cùng quy tắc với AdStatuses của backend)

/** DRAFT → APPROVED, APPROVED → ACTIVE, ACTIVE ↔ PAUSED, APPROVED/PAUSED → DRAFT, mọi trạng thái → EXPIRED (cuối); giữ nguyên luôn được. */
export function canTransition(from: AdStatus, to: AdStatus): boolean {
  if (from === to || to === 'EXPIRED') return true;
  return (from === 'DRAFT' && to === 'APPROVED') || (from === 'APPROVED' && to === 'ACTIVE') || (from === 'ACTIVE' && to === 'PAUSED')
    || (from === 'PAUSED' && to === 'ACTIVE') || ((from === 'APPROVED' || from === 'PAUSED') && to === 'DRAFT');
}

/** Vào APPROVED/ACTIVE cần quyền duyệt (ads.approve, MFA); dừng/về nháp/kết thúc chỉ cần ads.manage. */
export const needsApproval = (from: AdStatus, to: AdStatus): boolean => from !== to && (to === 'APPROVED' || to === 'ACTIVE');

export const transitionPermission = (from: AdStatus, to: AdStatus): PermissionKey => (needsApproval(from, to) ? 'ads.approve' : 'ads.manage');

export interface StatusAction {
  to: AdStatus;
  label: string;
  danger?: boolean;
}

/** Nút đổi trạng thái theo vòng đời; "Kết thúc" là bước không quay lại được. */
export function statusActions(status: AdStatus): StatusAction[] {
  const actions: StatusAction[] = {
    DRAFT: [{ to: 'APPROVED', label: 'Duyệt' }],
    APPROVED: [{ to: 'ACTIVE', label: 'Bắt đầu chạy' }, { to: 'DRAFT', label: 'Về nháp' }],
    ACTIVE: [{ to: 'PAUSED', label: 'Tạm dừng' }],
    PAUSED: [{ to: 'ACTIVE', label: 'Chạy lại' }, { to: 'DRAFT', label: 'Về nháp' }],
    EXPIRED: [],
  }[status] as StatusAction[];
  return status === 'EXPIRED' ? actions : [...actions, { to: 'EXPIRED', label: 'Kết thúc', danger: true }];
}

/** Banner, link, nhà quảng cáo, lịch chỉ sửa khi DRAFT; ưu tiên sửa mọi lúc (putAdminAdBooking). */
export const isContentLocked = (status: AdStatus): boolean => status !== 'DRAFT';

// ---------------------------------------------------------------- kích thước

export const sizeKey = (s: AdSize): string => `${s.breakpoint}:${s.width}x${s.height}`;
export const formatSize = (s: Pick<AdSize, 'width' | 'height'>): string => `${s.width}×${s.height}`;

export function sortSizes(sizes: AdSize[]): AdSize[] {
  const order = (b: string) => AD_BREAKPOINTS.indexOf(b as AdBreakpoint);
  return [...sizes].sort((a, b) => order(a.breakpoint) - order(b.breakpoint) || a.width - b.width || a.height - b.height);
}

/** "970x250", "970 × 250", "970*250" → {970, 250}; ngoài 1–4000 → null. */
export function parseSize(text: string): { width: number; height: number } | null {
  const m = /^\s*(\d{1,4})\s*[x×*]\s*(\d{1,4})\s*$/i.exec(text);
  if (!m) return null;
  const width = Number(m[1]);
  const height = Number(m[2]);
  return width >= 1 && width <= 4000 && height >= 1 && height <= 4000 ? { width, height } : null;
}

export function sizesSummary(sizes: AdSize[]): string {
  if (sizes.length === 0) return 'Chưa có kích thước';
  return AD_BREAKPOINTS.filter((b) => sizes.some((s) => s.breakpoint === b))
    .map((b) => `${breakpointLabel(b)} ${sortSizes(sizes.filter((s) => s.breakpoint === b)).map(formatSize).join(', ')}`)
    .join(' · ');
}

// ---------------------------------------------------------------- form vị trí

export const CODE_PATTERN = /^[a-z0-9_-]{2,80}$/;
export const MAX_SIZES = 12;

export interface PlacementDraft {
  code: string;
  label: string;
  sizes: AdSize[];
}

export function placementDraftErrors(d: PlacementDraft, requireSize: boolean): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!CODE_PATTERN.test(d.code)) errors.code = 'Mã 2–80 ký tự: chữ thường không dấu, số, "-" hoặc "_".';
  if (!d.label.trim() || d.label.trim().length > 150) errors.label = 'Nhãn 1–150 ký tự.';
  const keys = d.sizes.map(sizeKey);
  if (new Set(keys).size !== keys.length) errors.sizes = 'Có kích thước bị trùng.';
  else if (d.sizes.length > MAX_SIZES) errors.sizes = `Tối đa ${MAX_SIZES} kích thước.`;
  else if (requireSize && d.sizes.length === 0) errors.sizes = 'Cần ít nhất một kích thước.';
  return errors;
}

export function placementsQuery(siteKey: string): string {
  return siteKey ? `/admin/ad-placements?siteKey=${encodeURIComponent(siteKey)}` : '/admin/ad-placements';
}

/** AdPlacementUpdate (thay toàn bộ). Đổi trạng thái dùng giá trị đã lưu để không vô tình lưu sửa đổi chưa bấm Lưu. */
export function placementUpdateBody(draft: PlacementDraft, status: AdStatus): Record<string, unknown> {
  return { code: draft.code, label: draft.label.trim(), status, sizes: sortSizes(draft.sizes) };
}

/** Kích thước đang có banner dùng (không gỡ được — API trả 409). */
export function sizesInUse(bookings: AdBooking[]): Set<string> {
  return new Set(bookings.flatMap((b) => b.creatives.map(sizeKey)));
}

// ---------------------------------------------------------------- form đặt chỗ

export interface CreativeDraft {
  enabled: boolean;
  /** sizeKey của kích thước đã chọn. */
  size: string;
  mediaAssetId: string;
  altText: string;
}

export interface BookingDraft {
  priority: string;
  advertiserName: string;
  targetUrl: string;
  startsAt: string;
  endsAt: string;
  creatives: Record<AdBreakpoint, CreativeDraft>;
}

const sizesFor = (placement: Pick<AdPlacement, 'sizes'>, bp: AdBreakpoint) => sortSizes(placement.sizes.filter((s) => s.breakpoint === bp));

export function emptyBookingDraft(placement: Pick<AdPlacement, 'sizes'>): BookingDraft {
  const creatives = {} as Record<AdBreakpoint, CreativeDraft>;
  for (const bp of AD_BREAKPOINTS) {
    const first = sizesFor(placement, bp)[0];
    creatives[bp] = { enabled: false, size: first ? sizeKey(first) : '', mediaAssetId: '', altText: '' };
  }
  return { priority: '0', advertiserName: '', targetUrl: '', startsAt: '', endsAt: '', creatives };
}

export function draftFromBooking(b: AdBooking, placement: Pick<AdPlacement, 'sizes'>): BookingDraft {
  const draft = emptyBookingDraft(placement);
  for (const c of b.creatives) draft.creatives[c.breakpoint] = { enabled: true, size: sizeKey(c), mediaAssetId: c.mediaAssetId, altText: c.altText };
  return {
    ...draft,
    priority: String(b.priority),
    advertiserName: b.advertiserName ?? '',
    targetUrl: b.targetUrl ?? '',
    startsAt: toLocalInput(b.startsAt),
    endsAt: toLocalInput(b.endsAt),
  };
}

/** Khớp pattern ^https:// của hợp đồng và CHECK ck_ad_bookings_target_https: URL tuyệt đối https, không khoảng trắng. */
export function isHttpsUrl(value: string): boolean {
  if (!/^https:\/\/\S+$/.test(value)) return false;
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && url.hostname.length > 0;
  } catch {
    return false;
  }
}

/** Kiểm phía trình duyệt cho thông điệp sớm; API vẫn kiểm lại (422/UNSAFE_URL). */
export function bookingDraftErrors(d: BookingDraft, placement: Pick<AdPlacement, 'sizes'>, status: AdStatus = 'DRAFT'): Record<string, string> {
  const errors: Record<string, string> = {};
  const priority = Number(d.priority);
  if (!/^\d{1,3}$/.test(d.priority.trim()) || priority > 100) errors.priority = 'Ưu tiên là số nguyên 0–100.';
  if (d.advertiserName.trim().length > 180) errors.advertiserName = 'Tối đa 180 ký tự.';
  const target = d.targetUrl.trim();
  if (target && !isHttpsUrl(target)) errors.targetUrl = 'Link đích phải là địa chỉ https:// đầy đủ.';
  else if (target.length > 2000) errors.targetUrl = 'Tối đa 2000 ký tự.';
  else if (!target && (status === 'APPROVED' || status === 'ACTIVE')) errors.targetUrl = 'Cần link đích trước khi duyệt/chạy.';
  if (d.startsAt && d.endsAt && new Date(d.endsAt) <= new Date(d.startsAt)) errors.endsAt = 'Kết thúc phải sau bắt đầu.';

  const enabled = AD_BREAKPOINTS.filter((bp) => d.creatives[bp].enabled);
  if (enabled.length === 0) errors.creatives = 'Cần ít nhất một banner.';
  for (const bp of enabled) {
    const c = d.creatives[bp];
    if (!sizesFor(placement, bp).some((s) => sizeKey(s) === c.size)) errors[`creatives.${bp}.size`] = 'Chọn một kích thước của vị trí.';
    if (!c.mediaAssetId) errors[`creatives.${bp}.media`] = 'Chọn ảnh từ thư viện.';
    if (!c.altText.trim() || c.altText.trim().length > 250) errors[`creatives.${bp}.altText`] = 'Mô tả ảnh (alt) 1–250 ký tự.';
  }
  return errors;
}

function creativeBody(bp: AdBreakpoint, c: CreativeDraft) {
  const [, dims] = c.size.split(':');
  const [width, height] = (dims ?? '').split('x').map(Number);
  return { breakpoint: bp, width, height, mediaAssetId: c.mediaAssetId, altText: c.altText.trim() };
}

/** AdBookingCreate (thêm status → AdBookingUpdate). */
export function bookingBody(d: BookingDraft): Record<string, unknown> {
  return {
    priority: Number(d.priority),
    advertiserName: d.advertiserName.trim() || null,
    targetUrl: d.targetUrl.trim() || null,
    startsAt: fromLocalInput(d.startsAt),
    endsAt: fromLocalInput(d.endsAt),
    creatives: AD_BREAKPOINTS.filter((bp) => d.creatives[bp].enabled).map((bp) => creativeBody(bp, d.creatives[bp])),
  };
}

/** Đổi trạng thái hoặc ưu tiên của đặt chỗ đã lưu: gửi lại đúng nội dung đã lưu (nội dung khác chỉ đổi được khi DRAFT). */
export function bookingStatusBody(b: AdBooking, status: AdStatus, priority: number = b.priority): Record<string, unknown> {
  return {
    status,
    priority,
    advertiserName: b.advertiserName,
    targetUrl: b.targetUrl,
    startsAt: b.startsAt,
    endsAt: b.endsAt,
    creatives: b.creatives.map(({ breakpoint, width, height, mediaAssetId, altText }) => ({ breakpoint, width, height, mediaAssetId, altText })),
  };
}

/** Đặt chỗ có thể đang hiện trên trang (còn phụ thuộc cờ ads.display_slots và ưu tiên so với đặt chỗ khác). */
export function isLiveNow(placement: Pick<AdPlacement, 'status'>, b: AdBooking, now: Date = new Date()): boolean {
  return placement.status === 'ACTIVE' && b.status === 'ACTIVE'
    && (!b.startsAt || new Date(b.startsAt) <= now) && (!b.endsAt || new Date(b.endsAt) > now)
    && b.creatives.some((c) => !!c.creativeUrl);
}

export function scheduleText(b: Pick<AdBooking, 'startsAt' | 'endsAt'>): string {
  const f = (iso: string) => new Date(iso).toLocaleString('vi-VN', { dateStyle: 'short', timeStyle: 'short' });
  if (!b.startsAt && !b.endsAt) return 'Không giới hạn';
  if (!b.endsAt) return `Từ ${f(b.startsAt!)}`;
  if (!b.startsAt) return `Đến ${f(b.endsAt)}`;
  return `${f(b.startsAt)} – ${f(b.endsAt)}`;
}

// ---------------------------------------------------------------- lỗi

const FIELD_LABEL: Record<string, string> = {
  siteKey: 'Site',
  code: 'Mã vị trí',
  label: 'Nhãn',
  sizes: 'Kích thước',
  status: 'Trạng thái',
  priority: 'Ưu tiên',
  advertiserName: 'Nhà quảng cáo',
  targetUrl: 'Link đích',
  startsAt: 'Bắt đầu',
  endsAt: 'Kết thúc',
  creatives: 'Banner',
};

export function fieldLabel(key: string): string {
  const m = /^creatives\[(\d+)\](?:\.(\w+))?$/i.exec(key);
  if (m) return `Banner ${Number(m[1]) + 1}${m[2] ? ` (${m[2]})` : ''}`;
  return FIELD_LABEL[key] ?? FIELD_LABEL[key.charAt(0).toLowerCase() + key.slice(1)] ?? key;
}

/** Thông điệp tiếng Việt cho lỗi API của màn Quảng cáo. */
export function adErrorText(e: unknown): string {
  if (e instanceof ApiError) {
    switch (e.code) {
      case 'REVISION_CONFLICT':
        return 'Dữ liệu vừa được người khác sửa. Tải lại để xem bản mới nhất rồi làm lại thao tác.';
      case 'PRECONDITION_REQUIRED':
        return 'Thiếu phiên bản của dữ liệu (ETag). Tải lại trang rồi thử lại.';
      case 'UNSAFE_URL':
        return 'Link đích phải là địa chỉ https:// đầy đủ.';
      case 'STATE_CONFLICT':
        return `Không thực hiện được ở trạng thái hiện tại: ${e.problem.detail ?? 'tải lại để xem trạng thái mới nhất.'}`;
      case 'FORBIDDEN':
        return e.problem.title === 'MFA required'
          ? 'Thao tác này cần quyền duyệt quảng cáo trong phiên đã qua MFA. Đăng nhập lại với MFA.'
          : 'Vai trò của bạn không có quyền thực hiện thao tác này.';
      case 'NOT_FOUND':
        return 'Không tìm thấy vị trí hoặc đặt chỗ này (có thể đã bị đổi đường dẫn).';
      case 'DEPENDENCY_UNAVAILABLE':
        return 'Cơ sở dữ liệu chưa sẵn sàng. Thử lại sau.';
      default: {
        const fields = e.problem.errors
          ? Object.entries(e.problem.errors).map(([k, v]) => `${fieldLabel(k)}: ${v.join(' ')}`).join(' ')
          : '';
        return [e.problem.detail ?? e.problem.title, fields].filter(Boolean).join(' ');
      }
    }
  }
  return 'Không kết nối được API. Kiểm tra mạng rồi thử lại.';
}

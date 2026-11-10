// Danh mục nguồn nội dung + giấy phép (ADR-0007 §1, FR-NEWS-006) — kiểu theo components.schemas của hợp đồng v0.3 và quy tắc thuần.
// Xem: content.read; sửa nguồn: taxonomy.manage; sửa giấy phép: licenses.manage (MFA, chờ Legal Q2). API vẫn kiểm.

export type SourceKind = 'ORIGINAL' | 'AGENCY' | 'PARTNER' | 'PRESS_RELEASE' | 'OTHER';

export interface ContentSource {
  id: string;
  name: string;
  kind: SourceKind;
  url: string | null;
  isEnabled: boolean;
  rowVersion: number;
}

export interface License {
  id: string;
  code: string;
  name: string;
  usageScope: string;
  requiresAttribution: boolean;
  validFrom: string | null;
  validTo: string | null;
  evidenceRef: string | null;
  isEnabled: boolean;
  active: boolean;
  rowVersion: number;
}

export const SOURCE_KIND_LABEL: Record<SourceKind, string> = {
  ORIGINAL: 'Tự viết (Tinhte)',
  AGENCY: 'Hãng tin / báo',
  PARTNER: 'Đối tác nội dung',
  PRESS_RELEASE: 'Thông cáo báo chí',
  OTHER: 'Khác',
};

export const SOURCE_KINDS = Object.keys(SOURCE_KIND_LABEL) as SourceKind[];

export function sourceError(name: string, url: string): string | null {
  const n = name.trim();
  if (!n || n.length > 200) return 'Tên nguồn 1–200 ký tự.';
  const u = url.trim();
  if (u && (!/^https?:\/\/\S+$/.test(u) || u.length > 2048)) return 'URL phải bắt đầu bằng http:// hoặc https:// (tối đa 2048 ký tự).';
  return null;
}

/** Mã giấy phép: chữ HOA, số, _ . - (1–60), không đổi được sau khi tạo. */
export function licenseCodeError(code: string): string | null {
  return /^[A-Z0-9][A-Z0-9_.-]{0,59}$/.test(code) ? null : 'Mã gồm chữ HOA, số, "_", ".", "-" (1–60 ký tự), bắt đầu bằng chữ hoặc số.';
}

export function licenseError(input: { name: string; usageScope: string; evidenceRef: string; validFrom: string; validTo: string }): string | null {
  if (!input.name.trim()) return 'Nhập tên giấy phép.';
  if (!input.usageScope.trim()) return 'Nhập phạm vi được sử dụng.';
  if (!input.evidenceRef.trim()) return 'Nhập tham chiếu hợp đồng/bằng chứng (kho tài liệu).';
  if (input.validFrom && input.validTo && new Date(input.validTo) <= new Date(input.validFrom)) return 'Ngày hết hạn phải sau ngày bắt đầu.';
  return null;
}

/** Ô input type="date" (yyyy-mm-dd, giờ địa phương) ↔ RFC 3339 (đầu ngày theo giờ trình duyệt). Rỗng ↔ null. */
export function dateInputToIso(value: string): string | null {
  if (!value) return null;
  const d = new Date(`${value}T00:00:00`);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

export function isoToDateInput(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** Nhãn trạng thái giấy phép: đang hiệu lực / chưa tới hạn / hết hạn / đã tắt. */
export function licenseStatus(l: Pick<License, 'isEnabled' | 'active' | 'validFrom' | 'validTo'>, now: Date = new Date()): string {
  if (!l.isEnabled) return 'Đã tắt';
  if (l.active) return 'Đang hiệu lực';
  if (l.validFrom && new Date(l.validFrom) > now) return 'Chưa tới hạn';
  return 'Hết hạn';
}

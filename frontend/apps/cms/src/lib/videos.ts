// Màn Video + thư viện media (WBS-046) — phần thuần (không DOM) để kiểm bằng node:test.
// Hợp đồng: /admin/videos*, /admin/media* của OpenAPI v0.3 (AdminVideo, MediaStatus, UploadIntent, MediaJobAccepted).
import { ApiError } from './api.ts';

// ---------------------------------------------------------------- kiểu theo hợp đồng

export interface Sponsorship {
  sponsorName: string;
  sponsorReference: string;
  evidenceRef: string;
  validFrom: string;
  validTo: string;
  approvedBy?: string;
  approvedAt?: string;
}

export interface AdminVideo {
  id: string;
  title: string;
  status: string;
  contentKind: 'EDITORIAL' | 'SPONSORED' | string;
  sponsorship: Sponsorship | null;
  workingRevisionId: string | null;
  publicRevisionId: string | null;
  ownerId?: string;
  assigneeId: string | null;
  rightsStatus: string;
  rightsExpiresAt: string | null;
  scheduledAt: string | null;
  updatedAt: string;
  rowVersion: number;
  mediaId: string | null;
  summary?: string | null;
  // WBS-040 (CHANGELOG mục 13): trạng thái working revision, hồ sơ duyệt, lần gỡ gần nhất.
  revisionStatus?: string | null;
  submittedBy?: string | null;
  submittedAt?: string | null;
  reviewedBy?: string | null;
  reviewedAt?: string | null;
  reviewNote?: string | null;
  unpublishedAt?: string | null;
  unpublishReason?: string | null;
}

export interface MediaStatus {
  mediaId: string;
  status: string;
  generation: number;
  errorCode: string | null;
  mediaType?: 'VIDEO' | 'IMAGE' | 'SUBTITLE' | 'OTHER' | string;
  visibility: 'PRIVATE' | 'PUBLIC' | string;
  rightsStatus: string;
  licenseId: string | null;
  rightsExpiresAt: string | null;
  createdAt: string;
  fileName?: string | null;
  contentType?: string | null;
  sizeBytes?: number | null;
  createdBy?: string | null;
  updatedAt?: string;
}

export interface UploadIntent {
  mediaId: string;
  uploadUrl: string;
  expiresAt: string;
  uploadToken: string;
}

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

// ---------------------------------------------------------------- nhãn

export const CONTENT_STATUSES = ['DRAFT', 'IN_REVIEW', 'CHANGES_REQUESTED', 'REJECTED', 'APPROVED', 'SCHEDULED', 'PUBLISHED', 'UNPUBLISHED', 'RETRACTED'] as const;

const CONTENT_STATUS_LABEL: Record<string, string> = {
  DRAFT: 'Bản nháp',
  IN_REVIEW: 'Chờ duyệt',
  CHANGES_REQUESTED: 'Cần sửa',
  REJECTED: 'Bị từ chối',
  APPROVED: 'Đã duyệt',
  SCHEDULED: 'Đã lên lịch',
  PUBLISHED: 'Đã xuất bản',
  UNPUBLISHED: 'Đã gỡ',
  RETRACTED: 'Đã rút',
};

const MEDIA_STATUS_LABEL: Record<string, string> = {
  UPLOADING: 'Đang chờ tải lên',
  UPLOADED: 'Đã tải lên, chờ xử lý',
  VALIDATING: 'Đang kiểm tra tệp',
  PROCESSING: 'Đang xử lý',
  READY: 'Sẵn sàng',
  FAILED: 'Xử lý lỗi',
  QUARANTINED: 'Bị cách ly',
  REJECTED: 'Bị từ chối',
  RESTRICTED: 'Bị hạn chế',
};

const MEDIA_ERROR_LABEL: Record<string, string> = {
  UNSUPPORTED_FORMAT: 'Nội dung tệp không đúng định dạng đã khai báo (ví dụ tệp bị đổi đuôi).',
  OBJECT_MISSING: 'Không tìm thấy tệp đã tải lên trong kho lưu trữ.',
  UPLOAD_EXPIRED: 'Phiên tải lên đã hết hạn trước khi hoàn tất. Tải tệp lên lại.',
};

const RIGHTS_LABEL: Record<string, string> = {
  UNVERIFIED: 'Chưa xác minh quyền',
  CLEARED: 'Đã có quyền',
  RESTRICTED: 'Quyền bị hạn chế',
  EXPIRED: 'Hết hạn quyền',
  REVOKED: 'Quyền bị thu hồi',
};

const MEDIA_TYPE_LABEL: Record<string, string> = { VIDEO: 'Video', IMAGE: 'Ảnh', SUBTITLE: 'Phụ đề', OTHER: 'Khác' };

export const contentStatusLabel = (s: string): string => CONTENT_STATUS_LABEL[s] ?? s;
export const mediaStatusLabel = (s: string): string => MEDIA_STATUS_LABEL[s] ?? s;
export const mediaErrorLabel = (code: string | null): string | null => (code ? MEDIA_ERROR_LABEL[code] ?? `Mã lỗi: ${code}` : null);
export const rightsLabel = (s: string): string => RIGHTS_LABEL[s] ?? s;
export const mediaTypeLabel = (s: string | undefined): string => (s ? MEDIA_TYPE_LABEL[s] ?? s : '—');

/** Sắc thái badge theo trạng thái media: ok / warn / error / neutral. */
export function mediaTone(status: string): 'ok' | 'warn' | 'error' | 'neutral' {
  if (status === 'READY') return 'ok';
  if (MEDIA_FAILED.has(status)) return 'error';
  if (MEDIA_IN_FLIGHT.has(status)) return 'warn';
  return 'neutral';
}

// ---------------------------------------------------------------- trạng thái media

/** Đang chờ pipeline: CMS hỏi lại theo chu kỳ. */
const MEDIA_IN_FLIGHT = new Set(['UPLOADED', 'VALIDATING', 'PROCESSING']);
/** Kết cục xấu: không gắn vào video, phải tải tệp khác. */
const MEDIA_FAILED = new Set(['FAILED', 'QUARANTINED', 'REJECTED', 'RESTRICTED']);
const RIGHTS_BLOCKED = new Set(['RESTRICTED', 'EXPIRED', 'REVOKED']);

export const isMediaInFlight = (status: string): boolean => MEDIA_IN_FLIGHT.has(status);
export const isMediaFailed = (status: string): boolean => MEDIA_FAILED.has(status);

/** Cùng điều kiện với API (EnsureBindableMediaAsync): VIDEO, chưa hỏng, quyền chưa bị hạn chế/thu hồi/hết hạn. */
export function canAttachToVideo(media: MediaStatus, now: Date = new Date()): boolean {
  if (media.mediaType !== 'VIDEO' || MEDIA_FAILED.has(media.status) || RIGHTS_BLOCKED.has(media.rightsStatus)) return false;
  return !media.rightsExpiresAt || new Date(media.rightsExpiresAt).getTime() > now.getTime();
}

/** Lịch hỏi trạng thái: nhanh lúc đầu, giãn dần tới 10 giây (không dồn request khi xử lý lâu). */
export function pollDelayMs(attempt: number): number {
  const steps = [1000, 2000, 2000, 3000, 5000, 8000];
  return attempt < steps.length ? steps[Math.max(0, attempt)] : 10000;
}

// ---------------------------------------------------------------- tệp tải lên

/** Loại tệp API nhận (DRAFT, khớp MediaFormats của backend). */
export const UPLOAD_TYPES: Record<string, string> = {
  mp4: 'video/mp4',
  m4v: 'video/mp4',
  mov: 'video/quicktime',
  webm: 'video/webm',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  webp: 'image/webp',
  vtt: 'text/vtt',
};

export const VIDEO_ACCEPT = 'video/mp4,video/quicktime,video/webm,.mp4,.m4v,.mov,.webm';
export const ANY_ACCEPT = `${VIDEO_ACCEPT},image/jpeg,image/png,image/webp,text/vtt,.jpg,.jpeg,.png,.webp,.vtt`;

/** MIME gửi trong upload intent: lấy của trình duyệt nếu API nhận, không thì đoán theo đuôi (.vtt thường không có type). */
export function guessContentType(fileName: string, browserType: string): string | null {
  const known = new Set(Object.values(UPLOAD_TYPES));
  const type = browserType.split(';')[0].trim().toLowerCase();
  if (known.has(type)) return type;
  const ext = fileName.toLowerCase().split('.').pop() ?? '';
  return UPLOAD_TYPES[ext] ?? null;
}

/** "Video hay.mp4" → "Video hay" (gợi ý tiêu đề). */
export function titleFromFileName(fileName: string): string {
  const base = fileName.replace(/\.[^.]+$/, '').replace(/[_]+/g, ' ').trim();
  return (base || fileName).slice(0, 300);
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${unit === 0 ? value : value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

/** URL tải lên cùng origin CMS (kho dev) thì gửi kèm cookie + X-CSRF-Token; URL ký sẵn của kho ngoài thì KHÔNG gửi gì thêm. */
export function isSameOrigin(url: string, origin: string): boolean {
  try {
    return new URL(url, origin).origin === new URL(origin).origin;
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------- SHA-256 theo luồng

const K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
  0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
  0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
  0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

/**
 * SHA-256 tăng dần: băm tệp lớn theo từng khúc mà không nạp cả tệp vào bộ nhớ (crypto.subtle.digest cần cả ArrayBuffer).
 * Checksum gửi trong upload intent để API/kho lưu trữ kiểm tệp nhận được đúng tệp đã chọn.
 */
export class Sha256 {
  private readonly h = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
  private readonly block = new Uint8Array(64);
  private readonly w = new Uint32Array(64);
  private blockLength = 0;
  private total = 0;

  update(data: Uint8Array): this {
    let i = 0;
    this.total += data.length;
    if (this.blockLength > 0) {
      const take = Math.min(64 - this.blockLength, data.length);
      this.block.set(data.subarray(0, take), this.blockLength);
      this.blockLength += take;
      i = take;
      if (this.blockLength === 64) {
        this.compress(this.block, 0);
        this.blockLength = 0;
      }
    }
    for (; i + 64 <= data.length; i += 64) this.compress(data, i);
    if (i < data.length) {
      this.block.set(data.subarray(i), 0);
      this.blockLength = data.length - i;
    }
    return this;
  }

  digestHex(): string {
    const bitsHi = Math.floor(this.total / 0x20000000);
    const bitsLo = (this.total * 8) >>> 0;
    const pad = new Uint8Array(((this.blockLength < 56 ? 56 : 120) - this.blockLength) + 8);
    pad[0] = 0x80;
    const view = new DataView(pad.buffer);
    view.setUint32(pad.length - 8, bitsHi);
    view.setUint32(pad.length - 4, bitsLo);
    this.total -= pad.length;   // phần đệm không tính vào độ dài
    this.update(pad);
    return Array.from(this.h, (x) => x.toString(16).padStart(8, '0')).join('');
  }

  private compress(data: Uint8Array, offset: number): void {
    const w = this.w;
    for (let t = 0; t < 16; t++) {
      const j = offset + t * 4;
      w[t] = ((data[j] << 24) | (data[j + 1] << 16) | (data[j + 2] << 8) | data[j + 3]) >>> 0;
    }
    for (let t = 16; t < 64; t++) {
      const a = w[t - 15];
      const b = w[t - 2];
      const s0 = ((a >>> 7) | (a << 25)) ^ ((a >>> 18) | (a << 14)) ^ (a >>> 3);
      const s1 = ((b >>> 17) | (b << 15)) ^ ((b >>> 19) | (b << 13)) ^ (b >>> 10);
      w[t] = (w[t - 16] + s0 + w[t - 7] + s1) >>> 0;
    }
    let [a, b, c, d, e, f, g, h] = this.h;
    for (let t = 0; t < 64; t++) {
      const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
      const ch = (e & f) ^ (~e & g);
      const t1 = (h + S1 + ch + K[t] + w[t]) >>> 0;
      const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
      const maj = (a & b) ^ (a & c) ^ (b & c);
      const t2 = (S0 + maj) >>> 0;
      h = g;
      g = f;
      f = e;
      e = (d + t1) >>> 0;
      d = c;
      c = b;
      b = a;
      a = (t1 + t2) >>> 0;
    }
    const s = this.h;
    s[0] += a; s[1] += b; s[2] += c; s[3] += d; s[4] += e; s[5] += f; s[6] += g; s[7] += h;
  }
}

/** Băm một Blob/File theo khúc 4 MB, báo tiến độ 0..1. */
export async function sha256Blob(blob: Blob, onProgress?: (fraction: number) => void, signal?: AbortSignal): Promise<string> {
  const hash = new Sha256();
  const chunk = 4 * 1024 * 1024;
  for (let offset = 0; offset < blob.size; offset += chunk) {
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
    hash.update(new Uint8Array(await blob.slice(offset, offset + chunk).arrayBuffer()));
    onProgress?.(Math.min(1, (offset + chunk) / blob.size));
  }
  onProgress?.(1);
  return hash.digestHex();
}

// ---------------------------------------------------------------- truy vấn danh sách

export interface VideoListFilter {
  status: string;
  q: string;
  mine: boolean;
  sort: string;
}

export function videoListQuery(filter: VideoListFilter, userId: string, cursor: string | null, limit = 20): string {
  const params = new URLSearchParams({ limit: String(limit) });
  if (filter.status) params.set('status', filter.status);
  if (filter.q.trim()) params.set('q', filter.q.trim());
  if (filter.mine) params.set('ownerId', userId);
  if (filter.sort && filter.sort !== 'updatedAt_desc') params.set('sort', filter.sort);
  if (cursor) params.set('cursor', cursor);
  return `/admin/videos?${params}`;
}

export interface MediaListFilter {
  mediaType: string;
  rightsStatus: string;
  visibility: string;
  q: string;
}

export function mediaListQuery(filter: MediaListFilter, cursor: string | null, limit = 20): string {
  const params = new URLSearchParams({ limit: String(limit) });
  if (filter.mediaType) params.set('mediaType', filter.mediaType);
  if (filter.rightsStatus) params.set('rightsStatus', filter.rightsStatus);
  if (filter.visibility) params.set('visibility', filter.visibility);
  if (filter.q.trim()) params.set('q', filter.q.trim());
  if (cursor) params.set('cursor', cursor);
  return `/admin/media?${params}`;
}

// ---------------------------------------------------------------- form sửa video

export interface VideoDraft {
  title: string;
  summary: string;
  contentKind: string;
  sponsorship: { sponsorName: string; sponsorReference: string; evidenceRef: string; validFrom: string; validTo: string };
  changeReason: string;
}

/** datetime-local (giờ máy) ↔ ISO. */
export const toLocalInput = (iso: string | null | undefined): string => {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
};
export const fromLocalInput = (value: string): string | null => (value ? new Date(value).toISOString() : null);

export function draftFromVideo(video: AdminVideo): VideoDraft {
  const s = video.sponsorship;
  return {
    title: video.title,
    summary: video.summary ?? '',
    contentKind: video.contentKind,
    sponsorship: {
      sponsorName: s?.sponsorName ?? '',
      sponsorReference: s?.sponsorReference ?? '',
      evidenceRef: s?.evidenceRef ?? '',
      validFrom: toLocalInput(s?.validFrom),
      validTo: toLocalInput(s?.validTo),
    },
    changeReason: '',
  };
}

/**
 * Body PATCH tối thiểu (UpdateVideoRequest): chỉ trường đã đổi. null = không có gì để lưu.
 * Sapo rỗng gửi null (xóa). Đổi sang EDITORIAL thì API tự bỏ sponsorship.
 */
export function videoPatchBody(video: AdminVideo, draft: VideoDraft): Record<string, unknown> | null {
  const body: Record<string, unknown> = {};
  const original = draftFromVideo(video);
  if (draft.title.trim() !== video.title) body.title = draft.title.trim();
  if ((draft.summary.trim() || null) !== (video.summary ?? null)) body.summary = draft.summary.trim() || null;
  if (draft.contentKind !== video.contentKind) body.contentKind = draft.contentKind;
  if (draft.contentKind === 'SPONSORED') {
    const s = draft.sponsorship;
    const changed = (Object.keys(s) as (keyof VideoDraft['sponsorship'])[]).some((k) => s[k] !== original.sponsorship[k]);
    if (changed || body.contentKind) {
      body.sponsorship = {
        sponsorName: s.sponsorName.trim(),
        sponsorReference: s.sponsorReference.trim(),
        evidenceRef: s.evidenceRef.trim(),
        validFrom: fromLocalInput(s.validFrom),
        validTo: fromLocalInput(s.validTo),
      };
    }
  }
  if (Object.keys(body).length === 0) return null;
  if (draft.changeReason.trim()) body.changeReason = draft.changeReason.trim();
  return body;
}

// ---------------------------------------------------------------- duyệt / xuất bản / gỡ (WBS-040)
// Cùng quy tắc với VideoService.Workflow (C#). Chỉ để bật/tắt nút và gợi ý — API vẫn kiểm và trả lỗi.

/** ActionReason.reason của hợp đồng: 5–2000 ký tự (API trả 422 REASON_REQUIRED nếu thiếu). */
export const REASON_MIN = 5;
export const REASON_MAX = 2000;

export function reasonError(reason: string): string | null {
  const text = reason.trim();
  if (text.length < REASON_MIN) return `Nhập lý do (ít nhất ${REASON_MIN} ký tự).`;
  if (text.length > REASON_MAX) return `Lý do tối đa ${REASON_MAX} ký tự.`;
  return null;
}

/** Trạng thái video cho phép sửa/gửi duyệt (VideoStatuses.Editable). */
const EDITABLE_STATUSES = new Set(['DRAFT', 'CHANGES_REQUESTED', 'REJECTED', 'PUBLISHED', 'UNPUBLISHED']);

const REVISION_STATUS_LABEL: Record<string, string> = {
  DRAFT: 'Bản nháp',
  IN_REVIEW: 'Chờ duyệt',
  CHANGES_REQUESTED: 'Cần sửa',
  REJECTED: 'Bị từ chối',
  APPROVED: 'Đã duyệt, chờ xuất bản',
  PUBLISHED: 'Đang công khai',
  SUPERSEDED: 'Đã thay thế',
};
export const revisionStatusLabel = (s: string | null | undefined): string => (s ? REVISION_STATUS_LABEL[s] ?? s : '—');

type WorkflowVideo = Pick<AdminVideo, 'status' | 'revisionStatus' | 'workingRevisionId' | 'publicRevisionId' | 'mediaId'>;

/** Revision đang chờ quyết định (duyệt/xuất bản): không sửa nội dung/media cho tới khi có quyết định. */
export const isPendingDecision = (v: Pick<AdminVideo, 'revisionStatus' | 'workingRevisionId' | 'publicRevisionId'>): boolean =>
  (v.revisionStatus === 'IN_REVIEW' || v.revisionStatus === 'APPROVED') && v.workingRevisionId !== v.publicRevisionId;

/** Gửi duyệt được: working revision DRAFT chưa public, có media, video ở trạng thái sửa được (khớp SubmitAsync). */
export const canSubmitVideo = (v: WorkflowVideo): boolean =>
  v.revisionStatus === 'DRAFT' && !!v.workingRevisionId && v.workingRevisionId !== v.publicRevisionId && !!v.mediaId && EDITABLE_STATUSES.has(v.status);

/** Duyệt / yêu cầu sửa / từ chối: working revision IN_REVIEW (khớp RequirePendingReview). */
export const isInReview = (v: WorkflowVideo): boolean => v.revisionStatus === 'IN_REVIEW' && v.status !== 'RETRACTED';

/** Xuất bản: working revision APPROVED. */
export const isApproved = (v: WorkflowVideo): boolean => v.revisionStatus === 'APPROVED' && v.status !== 'RETRACTED';

/**
 * Lý do chưa xuất bản được (chốt chặn WBS-040): media READY, quyền media + video CLEARED còn hạn. Rỗng = có thể thử.
 * Quyền CLEARED do quy trình nguồn/giấy phép (WBS-023) đặt — CMS không tự đặt.
 */
export function publishBlockers(
  video: Pick<AdminVideo, 'mediaId' | 'rightsStatus' | 'rightsExpiresAt'>,
  media: Pick<MediaStatus, 'mediaId' | 'status' | 'mediaType' | 'rightsStatus' | 'rightsExpiresAt'> | null,
  now: Date = new Date(),
): string[] {
  const expired = (iso: string | null | undefined) => !!iso && new Date(iso).getTime() <= now.getTime();
  const out: string[] = [];
  if (!video.mediaId) out.push('Video chưa có tệp.');
  else if (media && media.mediaId === video.mediaId) {
    if (media.status !== 'READY' || (media.mediaType && media.mediaType !== 'VIDEO')) out.push(`Tệp chưa sẵn sàng (${mediaStatusLabel(media.status)}).`);
    if (media.rightsStatus !== 'CLEARED' || expired(media.rightsExpiresAt)) out.push(`Quyền của tệp: ${expired(media.rightsExpiresAt) ? 'hết hạn' : rightsLabel(media.rightsStatus)}.`);
  }
  if (video.rightsStatus !== 'CLEARED' || expired(video.rightsExpiresAt)) out.push(`Quyền của video: ${expired(video.rightsExpiresAt) ? 'hết hạn' : rightsLabel(video.rightsStatus)}.`);
  return out;
}

/** Thông điệp cho lỗi của các thao tác duyệt/xuất bản/gỡ. */
export function videoWorkflowErrorText(e: unknown): string {
  if (e instanceof ApiError) {
    switch (e.code) {
      case 'SOD_VIOLATION':
        return 'Bạn đã viết hoặc sửa phiên bản này nên không thể tự duyệt/xuất bản. Nhờ người duyệt khác.';
      case 'RIGHTS_INVALID':
        return `Chưa đủ quyền phát hành: ${e.problem.detail ?? 'quyền của video hoặc tệp chưa được xác nhận (CLEARED) hoặc đã hết hạn.'}`;
      case 'MEDIA_NOT_READY':
        return 'Tệp video chưa xử lý xong (hoặc đã hỏng). Chờ trạng thái “Sẵn sàng” rồi xuất bản.';
      case 'REASON_REQUIRED':
        return `Cần lý do ít nhất ${REASON_MIN} ký tự.`;
      case 'FEATURE_DISABLED':
        return 'Xuất bản nội dung tài trợ đang tắt (cờ ads.sponsored_publication).';
      case 'SPONSORSHIP_EXPIRED':
        return 'Thời hạn tài trợ chưa bắt đầu hoặc đã kết thúc.';
      case 'STATE_CONFLICT':
        return `Trạng thái video đã thay đổi: ${e.problem.detail ?? 'tải lại để xem bản mới nhất.'}`;
      default:
        return videoErrorText(e);
    }
  }
  return videoErrorText(e);
}

// ---------------------------------------------------------------- lỗi

const FIELD_LABEL: Record<string, string> = {
  title: 'Tiêu đề',
  summary: 'Mô tả',
  mediaId: 'Media',
  contentType: 'Loại tệp',
  fileName: 'Tên tệp',
  sizeBytes: 'Kích thước',
  checksumSha256: 'Checksum',
  uploadToken: 'Mã tải lên',
  changeReason: 'Lý do sửa',
  reason: 'Lý do',
  revisionId: 'Phiên bản',
};

export function formatWait(seconds: number): string {
  if (seconds < 60) return `${Math.ceil(seconds)} giây`;
  return `${Math.ceil(seconds / 60)} phút`;
}

/** Thông điệp tiếng Việt cho lỗi API của màn Video. <paramref name="retryAfterSeconds"/>: header Retry-After của 429 (nếu đọc được). */
export function videoErrorText(e: unknown, retryAfterSeconds?: number | null): string {
  if (e instanceof ApiError) {
    switch (e.code) {
      case 'REVISION_CONFLICT':
        return 'Video vừa được người khác sửa. Tải lại để xem bản mới nhất rồi áp lại thay đổi của bạn.';
      case 'PAYLOAD_TOO_LARGE':
        return e.problem.detail ?? 'Tệp vượt quá dung lượng cho phép.';
      case 'MEDIA_NOT_READY':
        return 'Kho lưu trữ chưa nhận được tệp. Tải lên lại rồi hoàn tất.';
      case 'RATE_LIMITED':
        return `Bạn đã tải lên quá nhiều trong thời gian ngắn hoặc còn nhiều tệp chưa hoàn tất.${retryAfterSeconds ? ` Thử lại sau khoảng ${formatWait(retryAfterSeconds)}.` : ' Thử lại sau ít phút.'}`;
      case 'DEPENDENCY_UNAVAILABLE':
        return 'Dịch vụ lưu trữ hoặc cơ sở dữ liệu chưa sẵn sàng. Thử lại sau.';
      case 'FORBIDDEN':
        return e.problem.detail ?? 'Bạn không có quyền thực hiện thao tác này.';
      case 'SPONSORSHIP_LABEL_REQUIRED':
        return 'Video tài trợ cần đủ thông tin nhà tài trợ, mã hợp đồng, bằng chứng và thời gian hiệu lực.';
      default: {
        const fields = e.problem.errors
          ? Object.entries(e.problem.errors).map(([k, v]) => `${FIELD_LABEL[k] ?? k}: ${v.join(' ')}`).join(' ')
          : '';
        return [e.problem.detail ?? e.problem.title, fields].filter(Boolean).join(' ');
      }
    }
  }
  if (e instanceof DOMException && e.name === 'AbortError') return 'Đã hủy.';
  return 'Không kết nối được API. Kiểm tra mạng rồi thử lại.';
}

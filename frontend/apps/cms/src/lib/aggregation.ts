// Radar tin GĐ1 trên CMS (WBS-144, ADR-0008, BA-01 mục 9) — kiểu dữ liệu theo components.schemas của hợp đồng v0.3 (tag CmsAggregation)
// và quy tắc thuần (không gọi mạng) để giao diện ẩn/hiện thao tác. API vẫn là nơi kiểm cuối cùng (trạng thái, quyền, cờ aggregation.radar).

export type SourceStatus = 'PROPOSED' | 'ENABLED' | 'PAUSED' | 'DEGRADED' | 'RETIRED';
export type UsageMode = 'M0' | 'M1' | 'M2' | 'M3';
export type FeedFormat = 'AUTO' | 'RSS2' | 'RSS1' | 'ATOM' | 'JSONFEED';
export type ClusterStatus = 'OPEN' | 'WATCHING' | 'DISMISSED' | 'COVERED' | 'CLOSED';
export type ClusterAction = 'watch' | 'reopen' | 'dismiss' | 'claim' | 'coverage' | 'split' | 'merge';

export interface CategoryMapping {
  feedCategory: string;
  categoryId: string;
}

export interface AggregationFeed {
  id: string;
  sourceId: string;
  url: string;
  format: FeedFormat;
  isEnabled: boolean;
  scheduleMinutes: number;
  categoryMapping: CategoryMapping[];
  health: string;
  lastFetchedAt: string | null;
  lastSuccessAt: string | null;
  nextFetchAt: string | null;
  consecutiveFailures: number;
  lastFailure: string | null;
  rowVersion: number;
}

export interface SourceSummary {
  id: string;
  name: string;
  status: SourceStatus;
  usageMode: UsageMode;
  licenseId: string | null;
  trustWeight: number;
  language: string;
  siteKeys: string[];
  feedCount: number;
  itemsLast24h: number;
  worstFeedHealth: string | null;
  statusChangedAt?: string | null;
  statusChangedBySystem?: boolean;
  statusReason?: string | null;
  rowVersion: number;
}

export interface ApprovalRecord {
  usageMode: UsageMode;
  licenseId: string | null;
  termsEvidenceRef: string;
  note: string | null;
  approvedBy: string;
  approvedAt: string;
}

export interface SourceDetail extends SourceSummary {
  termsEvidenceRef: string | null;
  proposedBy: string;
  approvedBy: string | null;
  approvedAt: string | null;
  feeds: AggregationFeed[];
  approvals: ApprovalRecord[];
}

export interface CategoryRule {
  categoryId: string;
  siteKey: string;
  categoryName: string;
  includeKeywords: string[];
  excludeKeywords: string[];
  entityKeywords: string[];
  updatedBy: string | null;
  updatedAt: string | null;
  rowVersion: number;
}

export interface AggregationItem {
  id: string;
  sourceId: string;
  sourceName: string;
  feedId: string;
  title: string;
  url: string;
  summary: string | null;
  author: string | null;
  publishedAt: string | null;
  firstSeenAt: string;
  timeReliable: boolean;
  language: string | null;
  feedCategories: string[];
  status: string;
  categoryId: string | null;
  confidence: number | null;
  needsReview: boolean;
  sensitive: boolean;
  version: number;
  clusterId: string | null;
}

export interface ClusterSummary {
  id: string;
  siteKey: string;
  title: string;
  representativeItemId: string;
  primaryCategoryId: string | null;
  categoryNeedsReview: boolean;
  score: number;
  status: ClusterStatus | string;
  sourceCount: number;
  independentSourceCount: number;
  itemCount: number;
  firstSeenAt: string;
  lastItemAt: string;
  labels: string[];
  maxUsageMode: UsageMode;
  assigneeId: string | null;
  coveredArticleId: string | null;
  rowVersion: number;
}

export interface ClusterActionRecord {
  action: string;
  actorId: string;
  at: string;
  reason: string | null;
  relatedClusterId: string | null;
  articleId: string | null;
}

export interface ClusterDetail extends ClusterSummary {
  items: AggregationItem[];
  actions: ClusterActionRecord[];
}

export interface FeedTestResult {
  ok: boolean;
  httpStatus: number | null;
  detectedFormat: FeedFormat | null;
  feedTitle: string | null;
  robotsAllowed: boolean;
  itemCount: number;
  sampleItems: { title: string; url: string; publishedAt: string | null; timeReliable: boolean }[];
  failure: string | null;
  durationMs: number;
}

export interface WorkerStatus {
  workerId: string;
  host: string | null;
  version: string | null;
  startedAt: string;
  lastSeenAt: string;
  healthy: boolean;
  lastError: string | null;
}

export interface AlertRecord {
  kind: 'SOURCE_AUTO_PAUSED' | 'SOURCE_DEGRADED' | string;
  sourceId: string;
  sourceName: string;
  message: string;
  createdAt: string;
  delivered: boolean;
}

export interface Stats {
  siteKey: string;
  date: string;
  timeZone: string;
  itemsToday: number;
  clustersToday: number;
  openClustersToday: number;
  fetchSuccessRate24h: number | null;
  sourcesByStatus: Record<string, number>;
  sources: { sourceId: string; name: string; status: SourceStatus; worstFeedHealth: string | null; itemsToday: number; lastSuccessAt: string | null }[];
  workers?: WorkerStatus[];
  alerts?: AlertRecord[];
}

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

// ---------------------------------------------------------------- nhãn hiển thị

export const SOURCE_STATUS_LABEL: Record<SourceStatus, string> = {
  PROPOSED: 'Chờ duyệt',
  ENABLED: 'Đang thu thập',
  PAUSED: 'Tạm dừng',
  DEGRADED: 'Suy giảm',
  RETIRED: 'Đã ngừng',
};

export const USAGE_MODE_LABEL: Record<UsageMode, string> = {
  M0: 'M0 — chỉ phát hiện (Radar nội bộ)',
  M1: 'M1 — link + nhắc tới ngắn tự viết',
  M2: 'M2 — dùng dữ kiện cho bài mới (cần giấy phép)',
  M3: 'M3 — khai thác theo giấy phép',
};

export const CLUSTER_STATUS_LABEL: Record<ClusterStatus, string> = {
  OPEN: 'Mới',
  WATCHING: 'Đang theo dõi',
  DISMISSED: 'Đã bỏ qua',
  COVERED: 'Đã đưa tin',
  CLOSED: 'Đã đóng',
};

export const CLUSTER_LABEL: Record<string, string> = {
  SINGLE_SOURCE: 'Một nguồn',
  SENSITIVE: 'Nhạy cảm',
  SOURCE_UPDATED: 'Nguồn đã cập nhật',
  NEW_SINCE_COVERED: 'Có tin mới sau khi đưa',
};

export const FEED_HEALTH_LABEL: Record<string, string> = {
  NEVER_FETCHED: 'Chưa đọc',
  OK: 'Tốt',
  DEGRADED: 'Lỗi gần đây',
  FAILING: 'Lỗi liên tục',
  BLOCKED_BY_ROBOTS: 'robots.txt chặn',
  DISABLED: 'Đã tắt',
};

export const FEED_FAILURE_LABEL: Record<string, string> = {
  TIMEOUT: 'Hết thời gian / không kết nối được',
  DNS: 'Không phân giải được tên miền',
  TLS: 'Lỗi chứng chỉ TLS',
  HTTP_STATUS: 'Máy chủ trả mã lỗi HTTP',
  TOO_LARGE: 'Feed quá lớn (> 5 MB)',
  UNSUPPORTED_CONTENT_TYPE: 'Không phải XML/JSON',
  PARSE_ERROR: 'Không đọc được nội dung feed',
  ROBOTS_DISALLOWED: 'robots.txt không cho phép',
  TOO_MANY_REDIRECTS: 'Chuyển hướng quá nhiều lần',
  RATE_LIMITED_BY_SOURCE: 'Nguồn yêu cầu đọc chậm lại',
};

export const ALERT_LABEL: Record<string, string> = {
  SOURCE_AUTO_PAUSED: 'Nguồn tự tạm dừng',
  SOURCE_DEGRADED: 'Nguồn suy giảm',
};

export const FEED_FORMATS: readonly FeedFormat[] = ['AUTO', 'RSS2', 'RSS1', 'ATOM', 'JSONFEED'];
export const CLUSTER_STATUSES: readonly ClusterStatus[] = ['OPEN', 'WATCHING', 'DISMISSED', 'COVERED', 'CLOSED'];
export const DEFAULT_CLUSTER_STATUSES: readonly ClusterStatus[] = ['OPEN', 'WATCHING'];

export function label(map: Record<string, string>, key: string | null | undefined): string {
  if (!key) return '—';
  return map[key] ?? key;
}

/** Nhãn trên thẻ cụm (BA-01 mục 9): nhãn của API + "Chỉ M0/M1" khi không nguồn nào ở M2/M3 (GĐ2 cần M2/M3 để soạn nháp AI). */
export function clusterBadges(cluster: Pick<ClusterSummary, 'labels' | 'maxUsageMode'>): string[] {
  const badges = cluster.labels.map((l) => CLUSTER_LABEL[l] ?? l);
  if (cluster.maxUsageMode === 'M0' || cluster.maxUsageMode === 'M1') badges.push('Chỉ M0/M1');
  return badges;
}

// ---------------------------------------------------------------- vòng đời cụm (cùng quy tắc AggregationClusterService)

/** Thao tác hợp lệ theo trạng thái — cùng quy tắc với API (409 STATE_CONFLICT nếu sai). */
export function allowedClusterActions(cluster: Pick<ClusterSummary, 'status' | 'assigneeId'>): ClusterAction[] {
  const actions: ClusterAction[] = [];
  const s = cluster.status;
  if (s === 'OPEN') actions.push('watch');
  if (s === 'DISMISSED' || s === 'WATCHING') actions.push('reopen');
  if (s === 'OPEN' || s === 'WATCHING') {
    actions.push('dismiss');
    if (cluster.assigneeId === null) actions.push('claim');   // người khác đã nhận → 409; chính mình đã nhận → không cần nhận lại
  }
  if (s !== 'COVERED') actions.push('coverage');
  if (s !== 'CLOSED') actions.push('split');
  if (s !== 'CLOSED' && s !== 'COVERED') actions.push('merge');
  return actions;
}

/** Tách: chọn ít nhất một mục và phải còn ít nhất một mục ở lại (API trả 422 nếu sai). */
export function splitError(selected: number, total: number): string | null {
  if (selected < 1) return 'Chọn ít nhất một mục để tách.';
  if (selected >= total) return 'Phải còn ít nhất một mục ở lại cụm. Muốn chuyển cả cụm, hãy dùng "Gộp vào cụm khác".';
  return null;
}

// ---------------------------------------------------------------- kiểm dữ liệu nhập

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isUuid(value: string): boolean {
  return UUID.test(value.trim());
}

/** ActionReason: 5–2000 ký tự sau khi bỏ khoảng trắng hai đầu (REASON_REQUIRED). */
export function reasonError(reason: string): string | null {
  const t = reason.trim();
  if (t.length < 5) return 'Lý do cần ít nhất 5 ký tự.';
  if (t.length > 2000) return 'Lý do tối đa 2000 ký tự.';
  return null;
}

/** URL feed: https tuyệt đối, ≤ 2048 ký tự. API còn kiểm địa chỉ nội bộ/DNS (UNSAFE_URL). */
export function feedUrlError(url: string): string | null {
  const t = url.trim();
  if (!t) return 'Nhập URL feed.';
  if (t.length > 2048) return 'URL tối đa 2048 ký tự.';
  try {
    const u = new URL(t);
    if (u.protocol !== 'https:') return 'Feed phải dùng https.';
  } catch {
    return 'URL không hợp lệ.';
  }
  return null;
}

/** Từ khóa luật chuyên mục: phân tách bằng dấu phẩy hoặc xuống dòng; bỏ trống, bỏ trùng (không phân biệt hoa thường), 1–100 ký tự, ≤ 200 từ. */
export function parseKeywords(text: string): { keywords: string[]; error: string | null } {
  const seen = new Set<string>();
  const keywords: string[] = [];
  for (const raw of text.split(/[,\n]/)) {
    const k = raw.trim();
    if (!k) continue;
    if (k.length > 100) return { keywords, error: `Từ khóa "${k.slice(0, 20)}…" dài quá 100 ký tự.` };
    const key = k.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    keywords.push(k);
  }
  if (keywords.length > 200) return { keywords, error: 'Tối đa 200 từ khóa mỗi danh sách.' };
  return { keywords, error: null };
}

export function keywordsText(keywords: readonly string[]): string {
  return keywords.join(', ');
}

/** Chế độ M2/M3 bắt buộc giấy phép (if/then của AggregationSourceApprovalRequest). */
export function approvalError(mode: UsageMode, licenseId: string, terms: string): string | null {
  if (!terms.trim()) return 'Nhập tham chiếu bằng chứng điều khoản của nguồn.';
  if ((mode === 'M2' || mode === 'M3') && !isUuid(licenseId)) return `Chế độ ${mode} cần mã giấy phép (UUID) đang hiệu lực.`;
  if (licenseId.trim() && !isUuid(licenseId)) return 'Mã giấy phép phải là UUID.';
  return null;
}

// ---------------------------------------------------------------- truy vấn

export interface RadarFilter {
  siteKey: string;
  window: 'DAY' | 'LAST_24H';
  date: string | null;
  categoryId: string | null;
  statuses: readonly string[];
  minScore: number | null;
}

/** Query của listAdminAggregationClusters. Ngày chỉ gửi khi window = DAY và đã chọn ngày (mặc định: hôm nay theo giờ tòa soạn). */
export function clustersQuery(filter: RadarFilter, limit: number, cursor?: string | null): string {
  const params = new URLSearchParams({ siteKey: filter.siteKey, window: filter.window, limit: String(limit) });
  if (filter.window === 'DAY' && filter.date) params.set('date', filter.date);
  if (filter.categoryId) params.set('categoryId', filter.categoryId);
  for (const s of filter.statuses) params.append('status', s);
  if (filter.minScore !== null && Number.isFinite(filter.minScore)) params.set('minScore', String(filter.minScore));
  if (cursor) params.set('cursor', cursor);
  return `/admin/aggregation/clusters?${params}`;
}

/** ETag từ rowVersion của danh sách (thao tác nhanh trên thẻ cụm không cần GET chi tiết). */
export function etagOf(rowVersion: number): string {
  return `W/"${rowVersion}"`;
}

/** Chỉ mở link http(s) của nguồn (không bao giờ javascript:, data:…). */
export function safeExternalUrl(value: string | null | undefined): string | null {
  if (!value) return null;
  try {
    const u = new URL(value);
    return u.protocol === 'https:' || u.protocol === 'http:' ? u.toString() : null;
  } catch {
    return null;
  }
}

export function percent(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : `${Math.round(value * 100)}%`;
}

/** "3 phút trước", "2 giờ trước", "5 ngày trước". */
export function ago(iso: string | null | undefined, now: Date = new Date()): string {
  if (!iso) return '—';
  const ms = now.getTime() - new Date(iso).getTime();
  if (!Number.isFinite(ms)) return '—';
  if (ms < 0) return 'sắp tới';
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 1) return 'vừa xong';
  if (minutes < 60) return `${minutes} phút trước`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} giờ trước`;
  return `${Math.floor(hours / 24)} ngày trước`;
}

export function dateTimeVi(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString('vi-VN', { dateStyle: 'short', timeStyle: 'short' });
}

// Gợi ý bằng AI trong màn sửa bài/video (FR-AI-001, P2 — cờ ai.assistance; quyền ĐỀ XUẤT ai.assist, chờ BA/PO).
// API: POST /admin/ai/metadata-suggestions (hợp đồng v0.3 §21). AI chỉ GỢI Ý: không ghi gì vào bài; người biên tập chọn rồi tự Lưu.
import { ApiError, apiFetch, type ApiRequest, type ApiResponse } from './api.ts';

export type AiTask = 'TAG' | 'SUMMARY';

export interface AiSuggestion {
  suggestions: string[];
  requiresHumanApproval: boolean;
}

export const AI_SUGGEST_PATH = '/admin/ai/metadata-suggestions';

export const AI_TASK_LABELS: Record<AiTask, string> = { SUMMARY: 'Gợi ý tóm tắt', TAG: 'Gợi ý tag' };

type Fetcher = <T>(path: string, request?: ApiRequest) => Promise<ApiResponse<T>>;

/**
 * Gọi API một lần. Mỗi lần bấm là một request mới (khóa Idempotency-Key mới): gửi lại cùng khóa chỉ phát lại kết quả cũ.
 * Lỗi giữ nguyên ApiError để màn hình hiện thông báo theo mã.
 */
export async function requestSuggestions(
  contentId: string,
  task: AiTask,
  fetcher: Fetcher = apiFetch,
  newKey: () => string = () => crypto.randomUUID(),
): Promise<string[]> {
  const response = await fetcher<AiSuggestion>(AI_SUGGEST_PATH, { method: 'POST', body: { contentId, task }, idempotencyKey: newKey() });
  return normalizeSuggestions(response.data);
}

/** Chỉ giữ chuỗi khác rỗng, bỏ trùng (không phân biệt hoa thường) — phòng khi API/proxy trả dữ liệu lạ. */
export function normalizeSuggestions(data: unknown): string[] {
  const list = data && typeof data === 'object' && Array.isArray((data as { suggestions?: unknown }).suggestions)
    ? ((data as { suggestions: unknown[] }).suggestions)
    : [];
  const seen = new Set<string>();
  const out: string[] = [];
  for (const item of list) {
    if (typeof item !== 'string') continue;
    const text = item.replace(/\s+/g, ' ').trim();
    const key = text.toLocaleLowerCase('vi');
    if (!text || seen.has(key)) continue;
    seen.add(key);
    out.push(text);
  }
  return out;
}

/** Thông báo cho người biên tập theo mã lỗi của hợp đồng. */
export function aiErrorText(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Không kết nối được API. Thử lại.';
  switch (error.code) {
    case 'FEATURE_DISABLED':
      return 'Tính năng gợi ý bằng AI đang tắt (chờ PO và Legal duyệt bật).';
    case 'FORBIDDEN':
      return 'Vai trò của bạn chưa được dùng gợi ý bằng AI.';
    case 'UNAUTHENTICATED':
      return 'Phiên đăng nhập đã hết. Đăng nhập lại rồi thử lại.';
    case 'NOT_FOUND':
      return 'Không tìm thấy bài/video này. Tải lại trang.';
    case 'VALIDATION_FAILED':
      return 'Bài chưa có nội dung để AI đọc. Viết và lưu nội dung trước.';
    case 'RATE_LIMITED':
      return 'Bạn đã dùng hết lượt gợi ý trong giờ này. Thử lại sau.';
    case 'DEPENDENCY_UNAVAILABLE':
      return 'Dịch vụ AI đang không phản hồi. Thử lại sau ít phút.';
    default:
      return error.problem.detail ?? error.problem.title;
  }
}

/** Danh sách tag để dán vào ô tag (phân cách bằng dấu phẩy). */
export function tagsToText(tags: readonly string[]): string {
  return tags.join(', ');
}

// ---------------------------------------------------------------- AI viết lại đoạn đã chọn (postAdminAiRewrite, CHANGELOG v0.3 mục 22)
// Cùng cờ ai.assistance + quyền ai.assist. Kết quả chỉ là ĐỀ XUẤT: người biên tập so sánh rồi chọn thay/chèn/bỏ, sau đó tự Lưu.

export type AiRewriteMode = 'IMPROVE' | 'SHORTEN' | 'EXPAND' | 'FIX_SPELLING' | 'NEUTRAL_TONE';

export const AI_REWRITE_PATH = '/admin/ai/rewrite';
export const AI_REWRITE_MAX = 6000;
export const AI_REWRITE_NOTE_MAX = 300;

export const AI_REWRITE_MODES: readonly { value: AiRewriteMode; label: string }[] = [
  { value: 'IMPROVE', label: 'Viết lại cho rõ ý' },
  { value: 'SHORTEN', label: 'Rút gọn' },
  { value: 'EXPAND', label: 'Viết dài hơn' },
  { value: 'FIX_SPELLING', label: 'Sửa chính tả, dấu câu' },
  { value: 'NEUTRAL_TONE', label: 'Giọng tin tức trung lập' },
];

/** Lỗi phát hiện trước khi gọi (server vẫn kiểm). */
export function rewriteInputError(text: string, note: string): string | null {
  if (!text.trim()) return 'Chọn một đoạn chữ trong nội dung trước.';
  if (text.length > AI_REWRITE_MAX) return `Đoạn chọn quá dài (${text.length} ký tự, tối đa ${AI_REWRITE_MAX}). Chọn ngắn hơn.`;
  if (note.length > AI_REWRITE_NOTE_MAX) return `Ghi chú tối đa ${AI_REWRITE_NOTE_MAX} ký tự.`;
  return null;
}

export async function requestRewrite(
  input: { contentId: string; text: string; mode: AiRewriteMode; note?: string },
  fetcher: Fetcher = apiFetch,
  newKey: () => string = () => crypto.randomUUID(),
): Promise<string> {
  const note = input.note?.trim() || null;
  const response = await fetcher<{ text?: unknown }>(AI_REWRITE_PATH, {
    method: 'POST',
    body: { contentId: input.contentId, text: input.text, mode: input.mode, note },
    idempotencyKey: newKey(),
  });
  const text = typeof response.data?.text === 'string' ? response.data.text.replace(/\r\n/g, '\n').trim() : '';
  if (!text) throw new ApiError({ title: 'AI không trả về nội dung', status: 503, code: 'DEPENDENCY_UNAVAILABLE' });
  return text;
}

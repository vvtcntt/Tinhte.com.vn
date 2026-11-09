// Client gọi API admin cùng origin (ADR-0001: https://{host cms}/api/v1, không CORS).
// - Cookie phiên __Host-tt_cms_session (HttpOnly) trình duyệt tự gửi.
// - Request ghi gửi X-CSRF-Token = cookie __Host-tt_cms_csrf (double-submit, ADR-0004 §4).
// - Lỗi trả về là Problem của hợp đồng → ném ApiError có code trong x-error-catalog.

export const API_PREFIX = '/api/v1';
export const CSRF_COOKIE = '__Host-tt_cms_csrf';
export const CSRF_HEADER = 'X-CSRF-Token';

export interface Problem {
  type?: string;
  title: string;
  status: number;
  code: string;
  detail?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly problem: Problem;

  constructor(problem: Problem) {
    super(problem.detail ?? problem.title);
    this.name = 'ApiError';
    this.status = problem.status;
    this.code = problem.code;
    this.problem = problem;
  }
}

/** Đọc một cookie (không HttpOnly) từ chuỗi document.cookie. */
export function readCookie(cookieHeader: string, name: string): string | null {
  for (const part of cookieHeader.split(';')) {
    const eq = part.indexOf('=');
    if (eq < 0) continue;
    if (part.slice(0, eq).trim() === name) return decodeURIComponent(part.slice(eq + 1).trim());
  }
  return null;
}

/** Chuẩn hóa phản hồi lỗi: Problem đúng schema, hoặc Problem tổng quát nếu proxy/edge trả HTML/text. */
export function toProblem(status: number, body: unknown): Problem {
  if (body && typeof body === 'object' && 'code' in body && 'title' in body) {
    const p = body as Problem;
    return { ...p, status: typeof p.status === 'number' ? p.status : status };
  }
  const code = status === 401 ? 'UNAUTHENTICATED' : status === 403 ? 'FORBIDDEN' : status === 404 ? 'NOT_FOUND'
    : status === 429 ? 'RATE_LIMITED' : status >= 500 ? 'DEPENDENCY_UNAVAILABLE' : 'BAD_REQUEST';
  return { title: `HTTP ${status}`, status, code };
}

/** returnTo phải là đường dẫn tương đối trên host CMS — cùng quy tắc với API (components.parameters.ReturnTo). */
export function sanitizeReturnTo(value: string | null | undefined): string {
  return value && value.length <= 500 && /^\/(?!\/)[^\s\\]*$/.test(value) ? value : '/';
}

export function loginUrl(returnTo: string): string {
  return `${API_PREFIX}/auth/staff/oidc/start?returnTo=${encodeURIComponent(sanitizeReturnTo(returnTo))}`;
}

export interface ApiRequest {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  /** Bắt buộc cho POST không idempotent theo hợp đồng; client tạo một lần và giữ khi thử lại. */
  idempotencyKey?: string;
  /** ETag W/"n" từ lần GET gần nhất (optimistic concurrency). */
  ifMatch?: string;
  signal?: AbortSignal;
}

export interface ApiResponse<T> {
  status: number;
  data: T | null;
  etag: string | null;
}

export async function apiFetch<T>(path: string, request: ApiRequest = {}): Promise<ApiResponse<T>> {
  const method = request.method ?? 'GET';
  const headers: Record<string, string> = { accept: 'application/json' };
  if (request.body !== undefined) headers['content-type'] = 'application/json';
  if (method !== 'GET') {
    const csrf = typeof document === 'undefined' ? null : readCookie(document.cookie, CSRF_COOKIE);
    if (csrf) headers[CSRF_HEADER] = csrf;
  }
  if (request.idempotencyKey) headers['Idempotency-Key'] = request.idempotencyKey;
  if (request.ifMatch) headers['If-Match'] = request.ifMatch;

  const response = await fetch(`${API_PREFIX}${path}`, {
    method,
    headers,
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    credentials: 'same-origin',
    cache: 'no-store',
    signal: request.signal,
  });

  const text = response.status === 204 ? '' : await response.text();
  let body: unknown = null;
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = null;
    }
  }
  if (!response.ok) throw new ApiError(toProblem(response.status, body));
  return { status: response.status, data: body as T | null, etag: response.headers.get('etag') };
}

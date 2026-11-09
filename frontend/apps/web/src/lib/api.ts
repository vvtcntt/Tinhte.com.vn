import { hostFor, parseSiteEnv, type SiteDefinition } from '@tinhte/sites';

// Gọi API public từ server (SSR). API phân giải site theo Host (ADR-0001) nên gửi X-Forwarded-Host
// suy ra từ Site Registry — không viết cứng hostname.
const API_ORIGIN = process.env.TT_API_ORIGIN ?? 'http://localhost:5080';

export class ApiError extends Error {
  constructor(
    readonly path: string,
    readonly status: number | null,
    message: string,
  ) {
    super(message);
  }
}

export async function apiGet<T>(site: SiteDefinition, path: string, opts: { revalidate?: number; timeoutMs?: number } = {}): Promise<T> {
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  let response: Response;
  try {
    response = await fetch(`${API_ORIGIN}/api/v1${path}`, {
      headers: { accept: 'application/json', 'x-forwarded-host': hostFor(site, env) },
      next: { revalidate: opts.revalidate ?? 60 },
      signal: AbortSignal.timeout(opts.timeoutMs ?? 3000),
    });
  } catch (error) {
    throw new ApiError(path, null, `API không phản hồi: ${(error as Error).message}`);
  }
  if (!response.ok) throw new ApiError(path, response.status, `API trả ${response.status}`);
  return (await response.json()) as T;
}

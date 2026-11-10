// Đường dẫn công khai — một chỗ duy nhất. Dạng URL bài/video còn OPEN (AIR-026, WBS-052):
// đổi ở đây khi PO/SEO chốt. Luôn là đường dẫn tương đối cùng origin của site (ADR-0001).

export const routes = {
  home: () => '/',
  article: (slug: string) => `/articles/${encodeURIComponent(slug)}`,
  latest: () => '/articles',
  category: (slug: string) => `/categories/${encodeURIComponent(slug)}`,
  shorts: () => '/shorts',
  video: (videoId: string) => `/shorts/${encodeURIComponent(videoId)}`,
  search: () => '/search',
  page: (slug: 'gioi-thieu' | 'dieu-khoan' | 'quyen-rieng-tu' | 'lien-he') => `/${slug}`,
  /** Trang giới thiệu bot thu thập tin — khớp Aggregation:BotInfoPath của worker (User-Agent TinhteNewsBot). */
  bot: () => '/bot',
};

/** Chỉ cho phép URL http(s) tuyệt đối (ảnh, creative). Chặn javascript:, data: … */
export function safeHttpUrl(value: string | null | undefined, { httpsOnly = false } = {}): string | null {
  if (!value) return null;
  try {
    const url = new URL(value);
    if (url.protocol === 'https:' || (!httpsOnly && url.protocol === 'http:')) return url.toString();
  } catch {
    // URL hỏng → coi như không có.
  }
  return null;
}

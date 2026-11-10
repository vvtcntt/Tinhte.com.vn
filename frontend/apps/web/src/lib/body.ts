// Đọc bodyBlocks an toàn cho trang công khai (BodyBlock — CHANGELOG v0.3 mục 22). Hàm thuần, có test.
// Dữ liệu là văn bản: không bao giờ chèn HTML. Liên kết chỉ http(s)/mailto; màu chỉ #rrggbb; sai dạng → bỏ định dạng, giữ chữ.
// Cùng quy tắc với apps/cms/src/lib/bodyBlocks.ts (hai app không dùng chung package) — sửa một bên thì sửa bên kia.

export type Mark = 'bold' | 'italic' | 'underline' | 'strike';
const MARKS: readonly Mark[] = ['bold', 'italic', 'underline', 'strike'];

export interface Run {
  text: string;
  marks: Mark[];
  href: string | null;
  color: string | null;
  highlight: string | null;
}

const HEX = /^#[0-9a-fA-F]{6}$/;

export function safeHref(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const href = value.trim();
  if (!href || href.length > 2000) return null;
  try {
    const url = new URL(href);
    if ((url.protocol === 'http:' || url.protocol === 'https:') && url.host) return href;
    if (url.protocol === 'mailto:' && url.pathname.includes('@')) return href;
    return null;
  } catch {
    return null;
  }
}

/** Các đoạn chữ của một khối/ô/mục. runs không khớp text → một run thường chứa text. */
export function runsOf(value: unknown): Run[] {
  const v = (value ?? {}) as { text?: unknown; runs?: unknown };
  const text = typeof v.text === 'string' ? v.text : '';
  const plain = text ? [{ text, marks: [], href: null, color: null, highlight: null }] : [];
  if (!Array.isArray(v.runs)) return plain;
  const runs: Run[] = [];
  for (const raw of v.runs as Record<string, unknown>[]) {
    if (!raw || typeof raw.text !== 'string' || !raw.text) return plain;
    runs.push({
      text: raw.text,
      marks: Array.isArray(raw.marks) ? MARKS.filter((m) => (raw.marks as unknown[]).includes(m)) : [],
      href: safeHref(raw.href),
      color: typeof raw.color === 'string' && HEX.test(raw.color) ? raw.color : null,
      highlight: typeof raw.highlight === 'string' && HEX.test(raw.highlight) ? raw.highlight : null,
    });
  }
  return runs.map((r) => r.text).join('') === text ? runs : plain;
}

export function alignOf(value: unknown): 'center' | 'right' | null {
  return value === 'center' || value === 'right' ? value : null;
}

/** Dòng của bảng (mảng các ô RichText). Sai dạng → null (bỏ cả bảng). */
export function tableRows(value: unknown): unknown[][] | null {
  if (!Array.isArray(value) || value.length === 0) return null;
  if (!value.every((r) => Array.isArray(r) && r.length > 0 && r.every((c) => c && typeof (c as { text?: unknown }).text === 'string'))) return null;
  return value as unknown[][];
}

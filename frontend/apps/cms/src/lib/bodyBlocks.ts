// Thân bài/video (bodyBlocks, schema BodyBlock — hợp đồng v0.3 CHANGELOG mục 22) ↔ tài liệu của trình soạn Tiptap.
// Hàm thuần, không import Tiptap: chạy được bằng `node --test`. Tài liệu Tiptap ở đây chỉ là JSON (JSONContent).
//
// Nguyên tắc:
// - bodyBlocks là DỮ LIỆU, không phải HTML. Mọi chữ đi qua `text`/`runs`; trang công khai tự escape.
// - Khối có `runs` thì `text` = nối các runs[].text → client cũ chỉ đọc `text` vẫn hiện đúng chữ.
// - Khối lạ (không thuộc 6 kiểu dưới) được GIỮ NGUYÊN qua trình soạn (nút rawBlock), không bao giờ mất khi lưu.
// - Liên kết chỉ http(s)/mailto; màu chỉ #rrggbb — khớp kiểm tra ở backend.

export type Mark = 'bold' | 'italic' | 'underline' | 'strike';
export const MARKS: readonly Mark[] = ['bold', 'italic', 'underline', 'strike'];
export type Align = 'left' | 'center' | 'right';

export interface TextRun {
  text: string;
  marks?: Mark[];
  href?: string;
  color?: string;
  highlight?: string;
}

export interface RichText {
  text: string;
  runs?: TextRun[];
}

export type BodyBlock =
  | ({ type: 'paragraph'; align?: Align } & RichText)
  | ({ type: 'heading'; level: 2 | 3; align?: Align } & RichText)
  | ({ type: 'quote' } & RichText)
  | { type: 'list'; ordered: boolean; items: RichText[] }
  | { type: 'image'; mediaId: string; alt: string; caption?: string; align?: Align }
  | { type: 'table'; headerRow?: boolean; caption?: string; rows: RichText[][] }
  | { type: string; [key: string]: unknown };

/** JSON tài liệu Tiptap/ProseMirror (tập con dùng ở đây). */
export interface PmNode {
  type: string;
  attrs?: Record<string, unknown>;
  content?: PmNode[];
  marks?: { type: string; attrs?: Record<string, unknown> }[];
  text?: string;
}

export const KNOWN_BLOCK_TYPES: ReadonlySet<string> = new Set(['paragraph', 'heading', 'quote', 'list', 'image', 'table']);

/** Giới hạn của server (BodyBlock). CMS chặn sớm để báo lỗi dễ hiểu hơn 422. */
export const LIMITS = { blocks: 2000, text: 20000, runs: 200, listItems: 200, tableRows: 100, tableCols: 20, alt: 300, caption: 500 } as const;

const HEX = /^#[0-9a-fA-F]{6}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Bảng màu chữ/nền của trình soạn (giới hạn để bài nhất quán, khác Kangaroo cho chọn tự do). */
export const TEXT_COLORS: readonly { value: string; label: string }[] = [
  { value: '#d32f2f', label: 'Đỏ' },
  { value: '#ef6c00', label: 'Cam' },
  { value: '#2e7d32', label: 'Xanh lá' },
  { value: '#1565c0', label: 'Xanh dương' },
  { value: '#6a1b9a', label: 'Tím' },
  { value: '#616161', label: 'Xám' },
];
export const HIGHLIGHT_COLORS: readonly { value: string; label: string }[] = [
  { value: '#fff59d', label: 'Vàng' },
  { value: '#c8e6c9', label: 'Xanh lá nhạt' },
  { value: '#bbdefb', label: 'Xanh dương nhạt' },
  { value: '#ffcdd2', label: 'Hồng' },
  { value: '#eeeeee', label: 'Xám nhạt' },
];

/** Chỉ nhận http(s) tuyệt đối hoặc mailto; còn lại (javascript:, data:, đường dẫn tương đối…) → null. */
export function safeHref(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const href = value.trim();
  if (!href || href.length > 2000) return null;
  try {
    const url = new URL(href);
    if (url.protocol === 'http:' || url.protocol === 'https:') return url.host ? href : null;
    if (url.protocol === 'mailto:') return url.pathname.includes('@') ? href : null;
    return null;
  } catch {
    return null;
  }
}

/** Người dùng gõ "tinhte.vn/abc" → thêm https://. Trả null nếu vẫn không hợp lệ. */
export function normalizeHrefInput(value: string): string | null {
  const v = value.trim();
  if (!v) return null;
  if (/^[a-z][a-z0-9+.-]*:/i.test(v)) return safeHref(v);
  if (/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v)) return safeHref(`mailto:${v}`);
  return safeHref(`https://${v}`);
}

const hex = (v: unknown): string | null => (typeof v === 'string' && HEX.test(v) ? v.toLowerCase() : null);
const align = (v: unknown): Align | undefined => (v === 'center' || v === 'right' ? v : undefined); // left = mặc định, không ghi

// ---------------------------------------------------------------- runs

function sameFormat(a: TextRun, b: TextRun): boolean {
  return (a.marks ?? []).join() === (b.marks ?? []).join() && a.href === b.href && a.color === b.color && a.highlight === b.highlight;
}

function isPlain(run: TextRun): boolean {
  return !run.marks?.length && !run.href && !run.color && !run.highlight;
}

/** Gộp runs liền kề cùng định dạng, bỏ run rỗng; tất cả là chữ thường → không cần runs. */
export function richText(runs: readonly TextRun[]): RichText {
  const merged: TextRun[] = [];
  for (const run of runs) {
    if (!run.text) continue;
    const last = merged[merged.length - 1];
    if (last && sameFormat(last, run)) last.text += run.text;
    else merged.push({ ...run, marks: run.marks ? [...run.marks] : undefined });
  }
  const text = merged.map((r) => r.text).join('');
  if (merged.every(isPlain)) return { text };
  return { text, runs: merged.map(cleanRun) };
}

function cleanRun(run: TextRun): TextRun {
  const out: TextRun = { text: run.text };
  if (run.marks?.length) out.marks = MARKS.filter((m) => run.marks!.includes(m));
  if (run.href) out.href = run.href;
  if (run.color) out.color = run.color;
  if (run.highlight) out.highlight = run.highlight;
  return out;
}

/** runs của khối đã lưu; runs không khớp text (dữ liệu cũ/hỏng) → bỏ runs, giữ text. */
function runsOf(value: { text?: unknown; runs?: unknown }): TextRun[] {
  const text = typeof value.text === 'string' ? value.text : '';
  if (!Array.isArray(value.runs)) return text ? [{ text }] : [];
  const runs: TextRun[] = [];
  for (const r of value.runs as Record<string, unknown>[]) {
    if (!r || typeof r.text !== 'string' || !r.text) continue;
    const run: TextRun = { text: r.text };
    const marks = Array.isArray(r.marks) ? MARKS.filter((m) => (r.marks as unknown[]).includes(m)) : [];
    if (marks.length) run.marks = marks;
    const href = safeHref(r.href);
    if (href) run.href = href;
    const color = hex(r.color);
    if (color) run.color = color;
    const highlight = hex(r.highlight);
    if (highlight) run.highlight = highlight;
    runs.push(run);
  }
  return runs.map((r) => r.text).join('') === text ? runs : text ? [{ text }] : [];
}

// ---------------------------------------------------------------- blocks → tài liệu Tiptap

function inline(runs: readonly TextRun[]): PmNode[] {
  const out: PmNode[] = [];
  for (const run of runs) {
    const marks: NonNullable<PmNode['marks']> = [];
    for (const m of run.marks ?? []) marks.push({ type: m });
    if (run.href) marks.push({ type: 'link', attrs: { href: run.href } });
    if (run.color) marks.push({ type: 'textStyle', attrs: { color: run.color } });
    if (run.highlight) marks.push({ type: 'highlight', attrs: { color: run.highlight } });
    run.text.split('\n').forEach((part, i) => {
      if (i > 0) out.push({ type: 'hardBreak' });
      if (part) out.push(marks.length ? { type: 'text', text: part, marks } : { type: 'text', text: part });
    });
  }
  return out;
}

function para(rt: { text?: unknown; runs?: unknown }, attrs?: Record<string, unknown>): PmNode {
  const content = inline(runsOf(rt));
  return { type: 'paragraph', ...(attrs ? { attrs } : {}), ...(content.length ? { content } : {}) };
}

export function blocksToDoc(blocks: readonly unknown[]): PmNode {
  const content: PmNode[] = [];
  for (const raw of blocks) {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) continue;
    const b = raw as Record<string, unknown>;
    switch (b.type) {
      case 'paragraph':
        if (typeof b.text === 'string') { content.push(para(b, { textAlign: align(b.align) ?? null })); continue; }
        break;
      case 'heading':
        if (typeof b.text === 'string') {
          const c = inline(runsOf(b));
          content.push({ type: 'heading', attrs: { level: b.level === 3 ? 3 : 2, textAlign: align(b.align) ?? null }, ...(c.length ? { content: c } : {}) });
          continue;
        }
        break;
      case 'quote':
        if (typeof b.text === 'string') { content.push({ type: 'blockquote', content: [para(b)] }); continue; }
        break;
      case 'list':
        if (Array.isArray(b.items) && b.items.every((i) => i && typeof (i as RichText).text === 'string')) {
          const items = (b.items as RichText[]).map((i) => ({ type: 'listItem', content: [para(i)] }));
          if (items.length) content.push({ type: b.ordered === true ? 'orderedList' : 'bulletList', content: items });
          continue;
        }
        break;
      case 'image':
        if (typeof b.mediaId === 'string' && UUID.test(b.mediaId)) {
          content.push({
            type: 'tinhteImage',
            attrs: { mediaId: b.mediaId, alt: typeof b.alt === 'string' ? b.alt : '', caption: typeof b.caption === 'string' ? b.caption : '', align: align(b.align) ?? null },
          });
          continue;
        }
        break;
      case 'table':
        if (Array.isArray(b.rows) && b.rows.length && b.rows.every((r) => Array.isArray(r) && r.length && r.every((c) => c && typeof (c as RichText).text === 'string'))) {
          const header = b.headerRow === true;
          const width = Math.max(...(b.rows as RichText[][]).map((r) => r.length));
          const rows = (b.rows as RichText[][]).map((row, ri) => ({
            type: 'tableRow',
            content: Array.from({ length: width }, (_, ci) => ({ type: header && ri === 0 ? 'tableHeader' : 'tableCell', content: [para(row[ci] ?? { text: '' })] })),
          }));
          content.push({ type: 'table', attrs: { caption: typeof b.caption === 'string' ? b.caption : '' }, content: rows });
          continue;
        }
        break;
      default:
        break;
    }
    // Khối lạ hoặc sai dạng: giữ nguyên.
    content.push({ type: 'rawBlock', attrs: { block: JSON.stringify(b) } });
  }
  if (content.length === 0) content.push({ type: 'paragraph' });
  return { type: 'doc', content };
}

// ---------------------------------------------------------------- tài liệu Tiptap → blocks

function runsFromInline(nodes: readonly PmNode[] | undefined): TextRun[] {
  const runs: TextRun[] = [];
  for (const n of nodes ?? []) {
    if (n.type === 'hardBreak') { runs.push({ text: '\n' }); continue; }
    if (n.type !== 'text' || !n.text) continue;
    const run: TextRun = { text: n.text };
    const marks: Mark[] = [];
    for (const m of n.marks ?? []) {
      if ((MARKS as readonly string[]).includes(m.type)) marks.push(m.type as Mark);
      else if (m.type === 'link') { const h = safeHref(m.attrs?.href); if (h) run.href = h; }
      else if (m.type === 'textStyle') { const c = hex(m.attrs?.color); if (c) run.color = c; }
      else if (m.type === 'highlight') { const c = hex(m.attrs?.color); if (c) run.highlight = c; }
    }
    if (marks.length) run.marks = MARKS.filter((m) => marks.includes(m));
    runs.push(run);
  }
  return runs;
}

/** Nối nhiều khối con (đoạn trong trích dẫn/ô bảng/mục danh sách) bằng xuống dòng. */
function joinChildren(children: readonly PmNode[] | undefined, out: { nested: PmNode[] }): TextRun[] {
  const runs: TextRun[] = [];
  for (const child of children ?? []) {
    if (child.type === 'bulletList' || child.type === 'orderedList') { out.nested.push(child); continue; }
    const r = child.type === 'paragraph' || child.type === 'heading' ? runsFromInline(child.content) : joinChildren(child.content, out);
    if (!r.length) continue;
    if (runs.length) runs.push({ text: '\n' });
    runs.push(...r);
  }
  return runs;
}

function trimRich(rt: RichText): RichText {
  // Bỏ xuống dòng thừa ở đầu/cuối (sinh ra khi gõ Shift+Enter cuối đoạn).
  if (!/^\n|\n$/.test(rt.text)) return rt;
  const runs = (rt.runs ?? [{ text: rt.text }]).map((r) => ({ ...r }));
  while (runs.length && runs[0].text.startsWith('\n')) { runs[0].text = runs[0].text.replace(/^\n+/, ''); if (!runs[0].text) runs.shift(); }
  while (runs.length && runs[runs.length - 1].text.endsWith('\n')) { const l = runs[runs.length - 1]; l.text = l.text.replace(/\n+$/, ''); if (!l.text) runs.pop(); }
  return richText(runs);
}

function listItems(list: PmNode): RichText[] {
  const items: RichText[] = [];
  for (const li of list.content ?? []) {
    const holder = { nested: [] as PmNode[] };
    const rt = trimRich(richText(joinChildren(li.content, holder)));
    if (rt.text.trim()) items.push(rt);
    // Danh sách lồng nhau: hợp đồng chỉ có danh sách phẳng → làm phẳng, không mất chữ.
    for (const nested of holder.nested) items.push(...listItems(nested));
  }
  return items;
}

export function docToBlocks(doc: PmNode): BodyBlock[] {
  const blocks: BodyBlock[] = [];
  for (const node of doc.content ?? []) {
    switch (node.type) {
      case 'paragraph': {
        const rt = trimRich(richText(runsFromInline(node.content)));
        if (!rt.text.trim()) break;
        const a = align(node.attrs?.textAlign);
        blocks.push({ type: 'paragraph', ...rt, ...(a ? { align: a } : {}) });
        break;
      }
      case 'heading': {
        const rt = trimRich(richText(runsFromInline(node.content)));
        if (!rt.text.trim()) break;
        const a = align(node.attrs?.textAlign);
        blocks.push({ type: 'heading', level: node.attrs?.level === 3 ? 3 : 2, ...rt, ...(a ? { align: a } : {}) });
        break;
      }
      case 'blockquote': {
        const holder = { nested: [] as PmNode[] };
        const rt = trimRich(richText(joinChildren(node.content, holder)));
        if (rt.text.trim()) blocks.push({ type: 'quote', ...rt });
        for (const nested of holder.nested) {
          const items = listItems(nested);
          if (items.length) blocks.push({ type: 'list', ordered: nested.type === 'orderedList', items });
        }
        break;
      }
      case 'bulletList':
      case 'orderedList': {
        const items = listItems(node);
        if (items.length) blocks.push({ type: 'list', ordered: node.type === 'orderedList', items });
        break;
      }
      case 'tinhteImage': {
        const mediaId = node.attrs?.mediaId;
        if (typeof mediaId !== 'string' || !UUID.test(mediaId)) break;
        const alt = String(node.attrs?.alt ?? '').trim().slice(0, LIMITS.alt);
        const caption = String(node.attrs?.caption ?? '').trim().slice(0, LIMITS.caption);
        const a = align(node.attrs?.align);
        blocks.push({ type: 'image', mediaId, alt, ...(caption ? { caption } : {}), ...(a ? { align: a } : {}) });
        break;
      }
      case 'table': {
        let headerRow = false;
        const rows: RichText[][] = (node.content ?? []).map((row, ri) => {
          const cells = row.content ?? [];
          if (ri === 0 && cells.length > 0 && cells.every((c) => c.type === 'tableHeader')) headerRow = true;
          return cells.map((cell) => trimRich(richText(joinChildren(cell.content, { nested: [] }))));
        }).filter((r) => r.length > 0);
        const caption = String(node.attrs?.caption ?? '').trim().slice(0, LIMITS.caption);
        if (rows.length && rows.some((r) => r.some((c) => c.text.trim()))) blocks.push({ type: 'table', ...(headerRow ? { headerRow } : {}), ...(caption ? { caption } : {}), rows });
        break;
      }
      case 'rawBlock': {
        try {
          const b = JSON.parse(String(node.attrs?.block ?? ''));
          if (b && typeof b === 'object' && !Array.isArray(b) && typeof b.type === 'string') blocks.push(b);
        } catch {
          // khối hỏng: bỏ
        }
        break;
      }
      default:
        break;
    }
  }
  return blocks;
}

// ---------------------------------------------------------------- tiện ích

/** Khóa so sánh "có thay đổi chưa" (JSON chuẩn hóa). Dùng làm Draft.body. */
export function blocksKey(blocks: readonly unknown[]): string {
  return JSON.stringify(blocks);
}

/** Draft.body đã lưu (JSON của bodyBlocks) → mảng; bản sao lưu cũ dạng văn bản (trình soạn tạm) → null để gọi textToBlocks. */
export function parseBlocksKey(value: string): unknown[] | null {
  if (!value.trimStart().startsWith('[')) return null;
  try {
    const parsed = JSON.parse(value);
    return Array.isArray(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/** Văn bản thuần của thân bài (đếm chữ, gửi AI, xem nhanh). */
export function blocksPlainText(blocks: readonly unknown[]): string {
  const parts: string[] = [];
  for (const raw of blocks) {
    const b = raw as Record<string, unknown>;
    if (!b || typeof b !== 'object') continue;
    if (typeof b.text === 'string' && b.type !== 'image') parts.push(b.text);
    if (Array.isArray(b.items)) for (const i of b.items as RichText[]) if (typeof i?.text === 'string') parts.push(i.text);
    if (Array.isArray(b.rows)) for (const r of b.rows as RichText[][]) if (Array.isArray(r)) parts.push(r.map((c) => c?.text ?? '').join(' '));
    if (b.type === 'image' && typeof b.caption === 'string') parts.push(b.caption);
  }
  return parts.join('\n\n');
}

export function wordCount(text: string): number {
  const m = text.trim().match(/\S+/g);
  return m ? m.length : 0;
}

/** Lỗi phát hiện được ở CMS trước khi gửi (server vẫn kiểm đầy đủ). Rỗng = ổn. */
export function blocksProblems(blocks: readonly BodyBlock[]): string[] {
  const problems: string[] = [];
  if (blocks.length > LIMITS.blocks) problems.push(`Nội dung quá dài: ${blocks.length} khối (tối đa ${LIMITS.blocks}).`);
  blocks.forEach((b, i) => {
    const at = `Khối ${i + 1}`;
    const r = b as Record<string, unknown>;
    if (typeof r.text === 'string' && r.text.length > LIMITS.text) problems.push(`${at}: đoạn quá dài (tối đa ${LIMITS.text} ký tự).`);
    if (Array.isArray(r.runs) && r.runs.length > LIMITS.runs) problems.push(`${at}: quá nhiều định dạng trong một đoạn (tối đa ${LIMITS.runs}).`);
    if (b.type === 'list' && Array.isArray(r.items) && r.items.length > LIMITS.listItems) problems.push(`${at}: danh sách tối đa ${LIMITS.listItems} mục.`);
    if (b.type === 'table' && Array.isArray(r.rows)) {
      const rows = r.rows as unknown[][];
      if (rows.length > LIMITS.tableRows) problems.push(`${at}: bảng tối đa ${LIMITS.tableRows} hàng.`);
      if (rows.some((row) => row.length > LIMITS.tableCols)) problems.push(`${at}: bảng tối đa ${LIMITS.tableCols} cột.`);
    }
  });
  return problems;
}

/** Nhắc nhở không chặn lưu. */
export function blocksWarnings(blocks: readonly BodyBlock[]): string[] {
  const missingAlt = blocks.filter((b) => b.type === 'image' && !String((b as { alt?: unknown }).alt ?? '').trim()).length;
  return missingAlt ? [`${missingAlt} ảnh chưa có mô tả (alt) — cần cho người đọc màn hình và SEO.`] : [];
}

/** URL xem ảnh trong CMS (getAdminMediaContent, cùng origin, cookie phiên). */
export function mediaContentPath(mediaId: string): string {
  return `/admin/media/${encodeURIComponent(mediaId)}/content`;
}

import type { ReactNode } from 'react';
import type { BodyBlock } from '../../lib/contracts';
import { alignOf, runsOf, tableRows } from '../../lib/body';

/**
 * Thân bài từ bodyBlocks (BodyBlock — CHANGELOG v0.3 mục 22). Chỉ hiển thị văn bản (React tự escape) — không bao giờ chèn HTML từ dữ liệu.
 * paragraph/heading/quote/list/table có định dạng chữ (runs). Ảnh (image) chưa hiển thị: chưa có hệ thống phân phối media công khai
 * (WBS-035) — bỏ qua thay vì hiện sai. Kiểu khối lạ cũng bị bỏ qua.
 */
function Text({ value }: { value: unknown }) {
  return (
    <>
      {runsOf(value).map((r, i) => {
        let node: ReactNode = r.text;
        if (r.marks.includes('bold')) node = <strong>{node}</strong>;
        if (r.marks.includes('italic')) node = <em>{node}</em>;
        if (r.marks.includes('underline')) node = <u>{node}</u>;
        if (r.marks.includes('strike')) node = <s>{node}</s>;
        if (r.color || r.highlight) node = <span style={{ color: r.color ?? undefined, backgroundColor: r.highlight ?? undefined }}>{node}</span>;
        if (r.href) node = <a href={r.href} rel="noopener noreferrer nofollow">{node}</a>;
        return <span key={i}>{node}</span>;
      })}
    </>
  );
}

const hasText = (v: unknown) => typeof (v as { text?: unknown })?.text === 'string' && ((v as { text: string }).text.trim().length > 0);

export function ArticleBody({ blocks }: { blocks: BodyBlock[] }) {
  return (
    <div className="article-body">
      {blocks.map((block, i) => {
        const align = alignOf(block.align);
        const cls = align ? `align-${align}` : undefined;
        switch (block.type) {
          case 'paragraph':
            return hasText(block) ? <p key={i} className={cls}><Text value={block} /></p> : null;
          case 'heading':
            if (!hasText(block)) return null;
            return block.level === 3 ? <h3 key={i} className={cls}><Text value={block} /></h3> : <h2 key={i} className={cls}><Text value={block} /></h2>;
          case 'quote':
            return hasText(block) ? <blockquote key={i}><Text value={block} /></blockquote> : null;
          case 'list': {
            const items = Array.isArray(block.items) ? block.items.filter(hasText) : [];
            if (!items.length) return null;
            const li = items.map((it, j) => <li key={j}><Text value={it} /></li>);
            return block.ordered === true ? <ol key={i}>{li}</ol> : <ul key={i}>{li}</ul>;
          }
          case 'table': {
            const rows = tableRows(block.rows);
            if (!rows) return null;
            const header = block.headerRow === true;
            const body = header ? rows.slice(1) : rows;
            return (
              <div key={i} className="article-table">
                <table>
                  {typeof block.caption === 'string' && block.caption.trim() && <caption>{block.caption}</caption>}
                  {header && <thead><tr>{rows[0].map((c, j) => <th key={j} scope="col"><Text value={c} /></th>)}</tr></thead>}
                  <tbody>{body.map((r, j) => <tr key={j}>{r.map((c, k) => <td key={k}><Text value={c} /></td>)}</tr>)}</tbody>
                </table>
              </div>
            );
          }
          default:
            return null; // image (chờ WBS-035) và kiểu lạ
        }
      })}
    </div>
  );
}

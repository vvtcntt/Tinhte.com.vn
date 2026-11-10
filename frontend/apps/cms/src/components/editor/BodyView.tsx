'use client';

// Xem trước bodyBlocks trong CMS — cùng quy tắc với trang công khai (ArticleBody của apps/web): chỉ văn bản, React tự escape,
// liên kết đã lọc http(s)/mailto, khối lạ hiện nhãn "chưa hiển thị" (trang công khai bỏ qua).

import { API_PREFIX } from '../../lib/api';
import { mediaContentPath, safeHref, type RichText, type TextRun } from '../../lib/bodyBlocks';

function Runs({ value }: { value: RichText }) {
  const runs: TextRun[] = Array.isArray(value.runs) && value.runs.map((r) => r.text).join('') === value.text ? value.runs : [{ text: value.text ?? '' }];
  return (
    <>
      {runs.map((r, i) => {
        let node: React.ReactNode = r.text;
        const marks = r.marks ?? [];
        if (marks.includes('bold')) node = <strong>{node}</strong>;
        if (marks.includes('italic')) node = <em>{node}</em>;
        if (marks.includes('underline')) node = <u>{node}</u>;
        if (marks.includes('strike')) node = <s>{node}</s>;
        const style = { color: r.color, backgroundColor: r.highlight };
        if (r.color || r.highlight) node = <span style={style}>{node}</span>;
        const href = safeHref(r.href);
        if (href) node = <a href={href} rel="noopener noreferrer nofollow" target="_blank">{node}</a>;
        return <span key={i} className="pv-runs" style={{ whiteSpace: 'pre-wrap' }}>{node}</span>;
      })}
    </>
  );
}

export function BodyView({ blocks }: { blocks: readonly unknown[] }) {
  if (blocks.length === 0) return <div className="preview muted">(Chưa có nội dung)</div>;
  return (
    <div className="preview">
      {blocks.map((raw, i) => {
        const b = raw as Record<string, unknown> & RichText;
        const align = b.align === 'center' || b.align === 'right' ? `align-${b.align}` : undefined;
        switch (b.type) {
          case 'paragraph':
            return <p key={i} className={align}><Runs value={b} /></p>;
          case 'heading':
            return b.level === 3 ? <h4 key={i} className={align}><Runs value={b} /></h4> : <h3 key={i} className={align}><Runs value={b} /></h3>;
          case 'quote':
            return <blockquote key={i}><Runs value={b} /></blockquote>;
          case 'list': {
            const items = Array.isArray(b.items) ? (b.items as RichText[]) : [];
            const li = items.map((it, j) => <li key={j}><Runs value={it} /></li>);
            return b.ordered ? <ol key={i}>{li}</ol> : <ul key={i}>{li}</ul>;
          }
          case 'image':
            return (
              <figure key={i} className={align}>
                {/* eslint-disable-next-line @next/next/no-img-element -- ảnh thư viện qua API cùng origin */}
                <img src={`${API_PREFIX}${mediaContentPath(String(b.mediaId))}`} alt={String(b.alt ?? '')} loading="lazy" />
                {typeof b.caption === 'string' && b.caption && <figcaption>{b.caption}</figcaption>}
              </figure>
            );
          case 'table': {
            const rows = Array.isArray(b.rows) ? (b.rows as RichText[][]) : [];
            const [head, ...rest] = b.headerRow ? rows : [null, ...rows];
            return (
              <table key={i}>
                {typeof b.caption === 'string' && b.caption && <caption>{b.caption}</caption>}
                {head && <thead><tr>{head.map((c, j) => <th key={j}><Runs value={c} /></th>)}</tr></thead>}
                <tbody>{(rest as RichText[][]).map((r, j) => <tr key={j}>{r.map((c, k) => <td key={k}><Runs value={c} /></td>)}</tr>)}</tbody>
              </table>
            );
          }
          default:
            return <p key={i} className="muted small">[Khối “{String(b.type)}” — trang công khai chưa hiển thị]</p>;
        }
      })}
    </div>
  );
}

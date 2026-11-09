import type { BodyBlock } from '../../lib/contracts';

/**
 * Thân bài từ bodyBlocks. Chỉ hiển thị văn bản (React tự escape) — không bao giờ chèn HTML từ dữ liệu.
 * Kiểu khối chưa hỗ trợ (ảnh, nhúng… — sau editor rich-text UX-CMS-02) bị bỏ qua thay vì hiện sai.
 */
export function ArticleBody({ blocks }: { blocks: BodyBlock[] }) {
  return (
    <div className="article-body">
      {blocks.map((block, i) => {
        const text = typeof block.text === 'string' ? block.text.trim() : '';
        if (!text) return null;
        switch (block.type) {
          case 'paragraph':
            return <p key={i}>{text}</p>;
          case 'heading':
            return block.level === 3 ? <h3 key={i}>{text}</h3> : <h2 key={i}>{text}</h2>;
          case 'quote':
            return <blockquote key={i}>{text}</blockquote>;
          default:
            return null;
        }
      })}
    </div>
  );
}

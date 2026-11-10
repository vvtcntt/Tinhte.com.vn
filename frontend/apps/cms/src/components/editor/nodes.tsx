'use client';

// Nút riêng của trình soạn Tinhte (ánh xạ với BodyBlock — CHANGELOG v0.3 mục 22).
import { mergeAttributes, Node } from '@tiptap/core';
import { Table } from '@tiptap/extension-table';
import { NodeViewWrapper, ReactNodeViewRenderer, type NodeViewProps } from '@tiptap/react';
import { useState } from 'react';
import { API_PREFIX } from '../../lib/api';
import { LIMITS, mediaContentPath } from '../../lib/bodyBlocks';

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    tinhteImage: {
      insertTinhteImage: (attrs: { mediaId: string; alt?: string; caption?: string }) => ReturnType;
    };
  }
}

/** Ảnh trong bài: chỉ giữ mediaId của thư viện media (không lưu URL — URL công khai do hệ thống phân phối media quyết định). */
export const TinhteImage = Node.create({
  name: 'tinhteImage',
  group: 'block',
  atom: true,
  draggable: true,
  selectable: true,

  addAttributes() {
    return {
      mediaId: { default: null },
      alt: { default: '' },
      caption: { default: '' },
      align: { default: null },
    };
  },

  parseHTML() {
    return [{ tag: 'figure[data-media-id]', getAttrs: (el) => ({ mediaId: (el as HTMLElement).dataset.mediaId ?? null }) }];
  },

  renderHTML({ HTMLAttributes }) {
    return ['figure', mergeAttributes({ 'data-media-id': HTMLAttributes.mediaId })];
  },

  addCommands() {
    return {
      insertTinhteImage:
        (attrs) =>
        ({ commands }) =>
          commands.insertContent({ type: this.name, attrs: { alt: '', caption: '', align: null, ...attrs } }),
    };
  },

  addNodeView() {
    return ReactNodeViewRenderer(ImageView);
  },
});

function ImageView({ node, updateAttributes, deleteNode, selected, editor }: NodeViewProps) {
  const [failed, setFailed] = useState(false);
  const mediaId = String(node.attrs.mediaId ?? '');
  const editable = editor.isEditable;
  const align = (node.attrs.align as string | null) ?? 'left';
  return (
    <NodeViewWrapper className={`rte-figure align-${align}${selected ? ' selected' : ''}`} data-drag-handle>
      {failed ? (
        <div className="rte-figure-missing" role="img" aria-label={node.attrs.alt || 'Ảnh'}>
          Không hiển thị được ảnh (đang xử lý, đã xóa hoặc chưa cấu hình kho media). Mã: {mediaId.slice(0, 8)}…
        </div>
      ) : (
        // eslint-disable-next-line @next/next/no-img-element -- ảnh của thư viện media qua API cùng origin, không qua next/image
        <img src={`${API_PREFIX}${mediaContentPath(mediaId)}`} alt={String(node.attrs.alt ?? '')} onError={() => setFailed(true)} loading="lazy" />
      )}
      <div className="rte-figure-fields" contentEditable={false}>
        <input
          aria-label="Mô tả ảnh (alt)"
          placeholder="Mô tả ảnh (alt) — bắt buộc nên có"
          value={String(node.attrs.alt ?? '')}
          maxLength={LIMITS.alt}
          disabled={!editable}
          onChange={(e) => updateAttributes({ alt: e.target.value })}
        />
        <input
          aria-label="Chú thích ảnh"
          placeholder="Chú thích (tùy chọn)"
          value={String(node.attrs.caption ?? '')}
          maxLength={LIMITS.caption}
          disabled={!editable}
          onChange={(e) => updateAttributes({ caption: e.target.value })}
        />
        {editable && (
          <span className="rte-figure-actions">
            {(['left', 'center', 'right'] as const).map((a) => (
              <button key={a} type="button" className={`rte-mini${align === a ? ' active' : ''}`} onClick={() => updateAttributes({ align: a === 'left' ? null : a })}>
                {a === 'left' ? 'Trái' : a === 'center' ? 'Giữa' : 'Phải'}
              </button>
            ))}
            <button type="button" className="rte-mini danger" onClick={deleteNode}>
              Xóa ảnh
            </button>
          </span>
        )}
      </div>
    </NodeViewWrapper>
  );
}

/** Khối không sửa được ở đây (kiểu lạ hoặc dữ liệu cũ sai dạng): giữ nguyên JSON, chỉ cho xóa. */
export const RawBlock = Node.create({
  name: 'rawBlock',
  group: 'block',
  atom: true,
  draggable: true,
  selectable: true,

  addAttributes() {
    return { block: { default: '{}' } };
  },

  parseHTML() {
    return [{ tag: 'div[data-raw-block]' }];
  },

  renderHTML({ HTMLAttributes }) {
    return ['div', mergeAttributes({ 'data-raw-block': '' }, { title: HTMLAttributes.block })];
  },

  addNodeView() {
    return ReactNodeViewRenderer(RawBlockView);
  },
});

function RawBlockView({ node, deleteNode, selected, editor }: NodeViewProps) {
  let type = '?';
  try {
    type = String(JSON.parse(String(node.attrs.block)).type ?? '?');
  } catch {
    // giữ '?'
  }
  return (
    <NodeViewWrapper className={`rte-raw${selected ? ' selected' : ''}`} data-drag-handle>
      <span>
        Khối <code>{type}</code> — trình soạn chưa sửa được kiểu này, sẽ được giữ nguyên khi lưu.
      </span>
      {editor.isEditable && (
        <button type="button" className="rte-mini danger" onClick={deleteNode}>
          Xóa khối
        </button>
      )}
    </NodeViewWrapper>
  );
}

/** Bảng có chú thích (BodyBlock.caption). Ô chỉ chứa đoạn văn (hợp đồng: ô là RichText). */
export const TinhteTable = Table.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      caption: { default: '', parseHTML: (el) => el.getAttribute('data-caption') ?? '', renderHTML: (attrs) => (attrs.caption ? { 'data-caption': attrs.caption } : {}) },
    };
  },
}).configure({ resizable: false, HTMLAttributes: { class: 'rte-table' } });

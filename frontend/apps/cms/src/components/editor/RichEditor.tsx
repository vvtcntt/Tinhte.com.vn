'use client';

// Trình soạn rich-text dùng chung của CMS (UX-CMS-02; theo nghiệp vụ trình soạn của Kangaroo-admin, dựng trên Tiptap).
// Khác Kangaroo: KHÔNG lưu HTML — giá trị vào/ra là bodyBlocks (JSON khối, CHANGELOG v0.3 mục 22), trang công khai tự escape.
// Giá trị là chuỗi JSON của mảng khối (blocksKey) để màn hình so sánh/sao lưu như mọi trường văn bản khác.

import { EditorContent, useEditor, type Editor } from '@tiptap/react';
import StarterKit from '@tiptap/starter-kit';
import TextAlign from '@tiptap/extension-text-align';
import { Color, TextStyle } from '@tiptap/extension-text-style';
import Highlight from '@tiptap/extension-highlight';
import { TableCell, TableHeader, TableRow } from '@tiptap/extension-table';
import { CharacterCount, Placeholder } from '@tiptap/extensions';
import { useCallback, useEffect, useReducer, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from 'react';
import {
  blocksKey, blocksPlainText, blocksToDoc, docToBlocks, HIGHLIGHT_COLORS, normalizeHrefInput, parseBlocksKey, safeHref, TEXT_COLORS,
  wordCount, type PmNode,
} from '../../lib/bodyBlocks';
import { RawBlock, TinhteImage, TinhteTable } from './nodes';
import { getSearchState, SearchReplace } from './searchReplace';
import { ImageDialog } from './ImageDialog';
import { AiRewritePanel } from './AiRewritePanel';

export interface RichEditorProps {
  /** JSON của bodyBlocks (blocksKey). */
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
  placeholder?: string;
  /** Bài/video đang sửa: cần cho AI viết lại (nhật ký AI ghi theo nội dung). Không có → ẩn AI. */
  contentId?: string;
  /** Được tải ảnh lên (media.upload). Không có quyền vẫn chọn được ảnh có sẵn trong thư viện. */
  canUpload?: boolean;
  /** Được dùng AI (ai.assist). */
  canUseAi?: boolean;
  label?: string;
}

const CellParagraphOnly = { content: 'paragraph+' };

function makeExtensions(placeholder: string) {
  return [
    StarterKit.configure({
      heading: { levels: [2, 3] }, // H1 là tiêu đề bài
      code: false,
      codeBlock: false,
      horizontalRule: false,
      link: {
        openOnClick: false,
        autolink: true,
        linkOnPaste: true,
        defaultProtocol: 'https',
        protocols: ['http', 'https', 'mailto'],
        isAllowedUri: (url) => safeHref(url) !== null,
        HTMLAttributes: { rel: 'noopener noreferrer nofollow', target: null },
      },
    }),
    TextAlign.configure({ types: ['heading', 'paragraph'], alignments: ['left', 'center', 'right'] }),
    TextStyle,
    Color,
    Highlight.configure({ multicolor: true }),
    TinhteTable,
    TableRow,
    TableHeader.extend(CellParagraphOnly),
    TableCell.extend(CellParagraphOnly),
    TinhteImage,
    RawBlock,
    SearchReplace,
    CharacterCount,
    Placeholder.configure({ placeholder }),
  ];
}

function toolbarState(editor: Editor | null) {
  if (!editor) return null;
  const color = editor.getAttributes('textStyle').color as string | undefined;
  const highlight = editor.getAttributes('highlight').color as string | undefined;
  return {
    paragraph: editor.isActive('paragraph') && !editor.isActive('blockquote') && !editor.isActive('listItem'),
    h2: editor.isActive('heading', { level: 2 }),
    h3: editor.isActive('heading', { level: 3 }),
    bold: editor.isActive('bold'),
    italic: editor.isActive('italic'),
    underline: editor.isActive('underline'),
    strike: editor.isActive('strike'),
    link: editor.isActive('link'),
    bulletList: editor.isActive('bulletList'),
    orderedList: editor.isActive('orderedList'),
    blockquote: editor.isActive('blockquote'),
    alignCenter: editor.isActive({ textAlign: 'center' }),
    alignRight: editor.isActive({ textAlign: 'right' }),
    alignLeft: !editor.isActive({ textAlign: 'center' }) && !editor.isActive({ textAlign: 'right' }),
    inTable: editor.isActive('table'),
    color: color ?? null,
    highlight: highlight ?? null,
    canUndo: editor.can().undo(),
    canRedo: editor.can().redo(),
    selectionEmpty: editor.state.selection.empty,
    words: editor.storage.characterCount?.words?.() ?? 0,
    chars: editor.storage.characterCount?.characters?.() ?? 0,
  };
}

/** Vẽ lại khi tài liệu/vùng chọn đổi (useEditorState của Tiptap không theo kịp lúc editor từ null → có, với immediatelyRender: false). */
function useEditorTick(editor: Editor | null): void {
  const [, tick] = useReducer((n: number) => n + 1, 0);
  useEffect(() => {
    if (!editor) return;
    editor.on('transaction', tick);
    tick();
    return () => { editor.off('transaction', tick); };
  }, [editor]);
}

function Btn({ title, active, disabled, onClick, children, wide }: { title: string; active?: boolean; disabled?: boolean; onClick: () => void; children: ReactNode; wide?: boolean }) {
  return (
    <button
      type="button"
      className={`rte-btn${active ? ' active' : ''}${wide ? ' wide' : ''}`}
      title={title}
      aria-label={title}
      aria-pressed={active}
      disabled={disabled}
      onMouseDown={(e) => e.preventDefault()} // giữ vùng chọn trong trình soạn
      onClick={onClick}
    >
      {children}
    </button>
  );
}

export function RichEditor({ value, onChange, disabled = false, placeholder = 'Viết nội dung…', contentId, canUpload = false, canUseAi = false, label = 'Nội dung' }: RichEditorProps) {
  const lastEmitted = useRef<string>(value);
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;
  const [panel, setPanel] = useState<null | 'link' | 'color' | 'highlight' | 'search' | 'replace' | 'table' | 'ai'>(null);
  const [imageDialog, setImageDialog] = useState<{ open: boolean; file: File | null }>({ open: false, file: null });
  const openImageWithFile = useRef<(file: File) => void>(() => undefined);
  openImageWithFile.current = (file) => setImageDialog({ open: true, file });

  const editor = useEditor({
    extensions: makeExtensions(placeholder),
    content: blocksToDoc(parseBlocksKey(value) ?? []) as never,
    editable: !disabled,
    immediatelyRender: false,
    editorProps: {
      attributes: { class: 'rte-content', 'aria-label': label, role: 'textbox', 'aria-multiline': 'true', spellcheck: 'true', lang: 'vi' },
      // Dán/thả ảnh: đi qua thư viện media (upload-intent → finalize), không nhúng base64.
      handlePaste: (_view, event) => {
        const file = Array.from(event.clipboardData?.files ?? []).find((f) => f.type.startsWith('image/'));
        if (!file) return false;
        event.preventDefault();
        openImageWithFile.current(file);
        return true;
      },
      handleDrop: (_view, event) => {
        const file = Array.from((event as DragEvent).dataTransfer?.files ?? []).find((f) => f.type.startsWith('image/'));
        if (!file) return false;
        event.preventDefault();
        openImageWithFile.current(file);
        return true;
      },
    },
    onUpdate: ({ editor: e }) => {
      const next = blocksKey(docToBlocks(e.getJSON() as PmNode));
      if (next === lastEmitted.current) return;
      lastEmitted.current = next;
      onChangeRef.current(next);
    },
  });

  // Giá trị đổi từ bên ngoài (tải lại, khôi phục bản sao lưu, xung đột 412): nạp lại tài liệu, không phát onChange.
  useEffect(() => {
    if (!editor || value === lastEmitted.current) return;
    lastEmitted.current = value;
    editor.commands.setContent(blocksToDoc(parseBlocksKey(value) ?? []) as never, { emitUpdate: false });
  }, [editor, value]);

  useEffect(() => {
    if (editor && editor.isEditable === disabled) editor.setEditable(!disabled, false);
  }, [editor, disabled]);

  useEditorTick(editor);
  const state = toolbarState(editor);

  const onKeyDown = useCallback((e: ReactKeyboardEvent) => {
    const mod = e.ctrlKey || e.metaKey;
    if (mod && e.key.toLowerCase() === 'f') { e.preventDefault(); setPanel('search'); }
    else if (mod && e.key.toLowerCase() === 'h') { e.preventDefault(); if (!disabled) setPanel('replace'); }
    else if (mod && e.key.toLowerCase() === 'k') { e.preventDefault(); if (!disabled) setPanel('link'); }
    else if (e.key === 'Escape' && panel) { setPanel(null); editor?.commands.focus(); }
  }, [disabled, editor, panel]);

  if (!editor || !state) {
    return <div className="rich-editor loading"><div className="rte-content muted">Đang tải trình soạn…</div></div>;
  }

  const off = disabled;
  const chain = () => editor.chain().focus();
  const toggle = (p: typeof panel) => setPanel((cur) => (cur === p ? null : p));

  return (
    <div className={`rich-editor${off ? ' disabled' : ''}`} onKeyDown={onKeyDown}>
      <div className="rte-toolbar" role="toolbar" aria-label="Định dạng nội dung">
        <div className="rte-group">
          <Btn title="Đoạn văn" active={state.paragraph} disabled={off} onClick={() => chain().setParagraph().run()} wide>Đoạn</Btn>
          <Btn title="Tiêu đề mục (H2)" active={state.h2} disabled={off} onClick={() => chain().toggleHeading({ level: 2 }).run()}>H2</Btn>
          <Btn title="Tiêu đề phụ (H3)" active={state.h3} disabled={off} onClick={() => chain().toggleHeading({ level: 3 }).run()}>H3</Btn>
        </div>
        <div className="rte-group">
          <Btn title="Đậm (Ctrl+B)" active={state.bold} disabled={off} onClick={() => chain().toggleBold().run()}><b>B</b></Btn>
          <Btn title="Nghiêng (Ctrl+I)" active={state.italic} disabled={off} onClick={() => chain().toggleItalic().run()}><i>I</i></Btn>
          <Btn title="Gạch chân (Ctrl+U)" active={state.underline} disabled={off} onClick={() => chain().toggleUnderline().run()}><u>U</u></Btn>
          <Btn title="Gạch ngang" active={state.strike} disabled={off} onClick={() => chain().toggleStrike().run()}><s>S</s></Btn>
          <Btn title="Liên kết (Ctrl+K)" active={state.link || panel === 'link'} disabled={off} onClick={() => toggle('link')}>🔗</Btn>
          <Btn title="Màu chữ" active={!!state.color || panel === 'color'} disabled={off} onClick={() => toggle('color')}>
            <span className="rte-swatch-a" style={{ borderBottomColor: state.color ?? 'currentColor' }}>A</span>
          </Btn>
          <Btn title="Tô nền chữ" active={!!state.highlight || panel === 'highlight'} disabled={off} onClick={() => toggle('highlight')}>
            <span className="rte-swatch-bg" style={{ background: state.highlight ?? 'transparent' }}>ab</span>
          </Btn>
        </div>
        <div className="rte-group">
          <Btn title="Danh sách chấm" active={state.bulletList} disabled={off} onClick={() => chain().toggleBulletList().run()}>••</Btn>
          <Btn title="Danh sách số" active={state.orderedList} disabled={off} onClick={() => chain().toggleOrderedList().run()}>1.</Btn>
          <Btn title="Trích dẫn" active={state.blockquote} disabled={off} onClick={() => chain().toggleBlockquote().run()}>❝</Btn>
        </div>
        <div className="rte-group">
          <Btn title="Căn trái" active={state.alignLeft} disabled={off} onClick={() => chain().unsetTextAlign().run()}>⇤</Btn>
          <Btn title="Căn giữa" active={state.alignCenter} disabled={off} onClick={() => chain().setTextAlign('center').run()}>↔</Btn>
          <Btn title="Căn phải" active={state.alignRight} disabled={off} onClick={() => chain().setTextAlign('right').run()}>⇥</Btn>
        </div>
        <div className="rte-group">
          <Btn title="Chèn ảnh" disabled={off} onClick={() => setImageDialog({ open: true, file: null })} wide>Ảnh</Btn>
          <Btn title="Bảng" active={state.inTable || panel === 'table'} disabled={off} onClick={() => (state.inTable ? toggle('table') : chain().insertTable({ rows: 3, cols: 2, withHeaderRow: true }).run())} wide>Bảng</Btn>
        </div>
        <div className="rte-group">
          <Btn title="Tìm (Ctrl+F)" active={panel === 'search'} onClick={() => toggle('search')}>🔍</Btn>
          <Btn title="Tìm và thay (Ctrl+H)" active={panel === 'replace'} disabled={off} onClick={() => toggle('replace')}>⇄</Btn>
          <Btn title="Hoàn tác (Ctrl+Z)" disabled={off || !state.canUndo} onClick={() => chain().undo().run()}>↶</Btn>
          <Btn title="Làm lại (Ctrl+Shift+Z)" disabled={off || !state.canRedo} onClick={() => chain().redo().run()}>↷</Btn>
        </div>
        {canUseAi && contentId && (
          <div className="rte-group">
            <Btn title="AI viết lại đoạn đã chọn" active={panel === 'ai'} disabled={off} onClick={() => toggle('ai')} wide>✦ AI viết lại</Btn>
          </div>
        )}
      </div>

      {panel === 'link' && <LinkPanel editor={editor} onClose={() => setPanel(null)} />}
      {(panel === 'color' || panel === 'highlight') && <ColorPanel editor={editor} kind={panel} onClose={() => setPanel(null)} />}
      {state.inTable && panel === 'table' && <TablePanel editor={editor} onClose={() => setPanel(null)} />}
      {(panel === 'search' || panel === 'replace') && <SearchPanel editor={editor} replace={panel === 'replace' && !off} onClose={() => { setPanel(null); editor.commands.setSearch({ term: '' }); editor.commands.focus(); }} />}
      {panel === 'ai' && contentId && <AiRewritePanel editor={editor} contentId={contentId} onClose={() => setPanel(null)} />}

      <EditorContent editor={editor} className="rte-body" />

      <div className="rte-status muted small">
        <span>{state.words.toLocaleString('vi-VN')} chữ · {state.chars.toLocaleString('vi-VN')} ký tự</span>
        {state.inTable && panel !== 'table' && !off && (
          <button type="button" className="rte-mini" onClick={() => setPanel('table')}>Sửa bảng…</button>
        )}
      </div>

      {imageDialog.open && (
        <ImageDialog
          canUpload={canUpload}
          initialFile={imageDialog.file}
          onClose={() => { setImageDialog({ open: false, file: null }); editor.commands.focus(); }}
          onInsert={(media, alt) => {
            editor.chain().focus().insertTinhteImage({ mediaId: media.mediaId, alt }).run();
            setImageDialog({ open: false, file: null });
          }}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------- liên kết

function LinkPanel({ editor, onClose }: { editor: Editor; onClose: () => void }) {
  const current = (editor.getAttributes('link').href as string | undefined) ?? '';
  const [href, setHref] = useState(current);
  const [error, setError] = useState<string | null>(null);
  const apply = () => {
    if (!href.trim()) {
      editor.chain().focus().extendMarkRange('link').unsetLink().run();
      return onClose();
    }
    const safe = normalizeHrefInput(href);
    if (!safe) return setError('Chỉ nhận liên kết http(s) hoặc địa chỉ email.');
    const { empty } = editor.state.selection;
    if (empty && !editor.isActive('link')) {
      // Không chọn chữ: chèn chính URL làm chữ của liên kết.
      editor.chain().focus().insertContent({ type: 'text', text: href.trim(), marks: [{ type: 'link', attrs: { href: safe } }] }).run();
    } else {
      editor.chain().focus().extendMarkRange('link').setLink({ href: safe }).run();
    }
    onClose();
  };
  return (
    <div className="rte-panel" role="group" aria-label="Liên kết">
      <input
        autoFocus
        value={href}
        placeholder="https://… hoặc email"
        onChange={(e) => { setHref(e.target.value); setError(null); }}
        onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); apply(); } }}
      />
      <button type="button" className="button small primary" onClick={apply}>Áp dụng</button>
      {current && (
        <button type="button" className="button small" onClick={() => { editor.chain().focus().extendMarkRange('link').unsetLink().run(); onClose(); }}>
          Bỏ liên kết
        </button>
      )}
      <button type="button" className="button small" onClick={onClose}>Đóng</button>
      {error && <span className="rte-error" role="alert">{error}</span>}
      <span className="muted small">Liên kết mở trong cùng tab, kèm rel=&quot;nofollow&quot;. Chỉ http(s)/mailto.</span>
    </div>
  );
}

// ---------------------------------------------------------------- màu

function ColorPanel({ editor, kind, onClose }: { editor: Editor; kind: 'color' | 'highlight'; onClose: () => void }) {
  const palette = kind === 'color' ? TEXT_COLORS : HIGHLIGHT_COLORS;
  const set = (value: string | null) => {
    const c = editor.chain().focus();
    if (kind === 'color') (value ? c.setColor(value) : c.unsetColor()).run();
    else (value ? c.setHighlight({ color: value }) : c.unsetHighlight()).run();
    onClose();
  };
  return (
    <div className="rte-panel" role="group" aria-label={kind === 'color' ? 'Màu chữ' : 'Màu nền chữ'}>
      <span className="muted small">{kind === 'color' ? 'Màu chữ' : 'Tô nền'}:</span>
      {palette.map((c) => (
        <button key={c.value} type="button" className="rte-swatch" title={c.label} aria-label={c.label} style={{ background: c.value }} onMouseDown={(e) => e.preventDefault()} onClick={() => set(c.value)} />
      ))}
      <button type="button" className="button small" onClick={() => set(null)}>Mặc định</button>
    </div>
  );
}

// ---------------------------------------------------------------- bảng

function TablePanel({ editor, onClose }: { editor: Editor; onClose: () => void }) {
  const table = findTable(editor);
  const caption = String(table?.node.attrs.caption ?? '');
  const run = (fn: (c: ReturnType<Editor['chain']>) => ReturnType<Editor['chain']>) => fn(editor.chain().focus()).run();
  const setCaption = (value: string) => {
    if (!table) return;
    editor.chain().command(({ tr }) => { tr.setNodeAttribute(table.pos, 'caption', value); return true; }).run();
  };
  return (
    <div className="rte-panel" role="group" aria-label="Sửa bảng">
      <button type="button" className="button small" onClick={() => run((c) => c.addRowBefore())}>+ Hàng trên</button>
      <button type="button" className="button small" onClick={() => run((c) => c.addRowAfter())}>+ Hàng dưới</button>
      <button type="button" className="button small" onClick={() => run((c) => c.addColumnBefore())}>+ Cột trái</button>
      <button type="button" className="button small" onClick={() => run((c) => c.addColumnAfter())}>+ Cột phải</button>
      <button type="button" className="button small" onClick={() => run((c) => c.deleteRow())}>− Hàng</button>
      <button type="button" className="button small" onClick={() => run((c) => c.deleteColumn())}>− Cột</button>
      <button type="button" className="button small" onClick={() => run((c) => c.toggleHeaderRow())}>Hàng tiêu đề</button>
      <input value={caption} placeholder="Chú thích bảng" maxLength={500} onChange={(e) => setCaption(e.target.value)} aria-label="Chú thích bảng" />
      <button type="button" className="button small danger" onClick={() => { run((c) => c.deleteTable()); onClose(); }}>Xóa bảng</button>
      <button type="button" className="button small" onClick={onClose}>Đóng</button>
    </div>
  );
}

function findTable(editor: Editor): { node: { attrs: Record<string, unknown> }; pos: number } | null {
  const { $from } = editor.state.selection;
  for (let d = $from.depth; d > 0; d--) {
    const node = $from.node(d);
    if (node.type.name === 'table') return { node, pos: $from.before(d) };
  }
  return null;
}

// ---------------------------------------------------------------- tìm / thay

function SearchPanel({ editor, replace, onClose }: { editor: Editor; replace: boolean; onClose: () => void }) {
  const initial = editor.state.selection.empty ? '' : editor.state.doc.textBetween(editor.state.selection.from, editor.state.selection.to, ' ').slice(0, 200);
  const [term, setTerm] = useState(initial);
  const [replacement, setReplacement] = useState('');
  const [caseSensitive, setCaseSensitive] = useState(false);
  const [wholeWord, setWholeWord] = useState(false);
  useEditorTick(editor);
  const st = getSearchState(editor.state);
  const s = { count: st?.matches.length ?? 0, index: st?.index ?? 0 };

  useEffect(() => {
    editor.commands.setSearch({ term, caseSensitive, wholeWord, index: 0 });
  }, [editor, term, caseSensitive, wholeWord]);

  const step = (d: 1 | -1) => editor.commands.searchStep(d);
  return (
    <div className="rte-panel" role="search" aria-label="Tìm trong nội dung">
      <input
        autoFocus
        value={term}
        placeholder="Tìm…"
        onChange={(e) => setTerm(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); step(e.shiftKey ? -1 : 1); } }}
        aria-label="Từ cần tìm"
      />
      <span className="muted small" aria-live="polite">{term ? (s?.count ? `${(s.index ?? 0) + 1}/${s.count}` : 'Không thấy') : ''}</span>
      <button type="button" className="button small" disabled={!s?.count} onClick={() => step(-1)} title="Kết quả trước (Shift+Enter)">↑</button>
      <button type="button" className="button small" disabled={!s?.count} onClick={() => step(1)} title="Kết quả tiếp (Enter)">↓</button>
      <label className="rte-check"><input type="checkbox" checked={caseSensitive} onChange={(e) => setCaseSensitive(e.target.checked)} /> Aa</label>
      <label className="rte-check"><input type="checkbox" checked={wholeWord} onChange={(e) => setWholeWord(e.target.checked)} /> Cả từ</label>
      {replace && (
        <>
          <input
            value={replacement}
            placeholder="Thay bằng…"
            onChange={(e) => setReplacement(e.target.value)}
            onKeyDown={(e) => {
              if (e.key !== 'Enter') return;
              e.preventDefault();
              if (e.ctrlKey || e.metaKey) editor.commands.replaceAll(replacement);
              else editor.commands.replaceCurrent(replacement);
            }}
            aria-label="Thay bằng"
          />
          <button type="button" className="button small" disabled={!s?.count} onClick={() => editor.commands.replaceCurrent(replacement)}>Thay</button>
          <button type="button" className="button small" disabled={!s?.count} onClick={() => editor.commands.replaceAll(replacement)}>Thay tất cả</button>
        </>
      )}
      <button type="button" className="button small" onClick={onClose} title="Đóng (Esc)">Đóng</button>
    </div>
  );
}

/** Văn bản thuần của giá trị (cho xem nhanh, đếm chữ ngoài trình soạn). */
export function valuePlainText(value: string): string {
  return blocksPlainText(parseBlocksKey(value) ?? []);
}

export { wordCount };

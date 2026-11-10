'use client';

// AI viết lại đoạn đã chọn (postAdminAiRewrite — cờ ai.assistance, quyền ĐỀ XUẤT ai.assist; CHANGELOG v0.3 mục 22).
// AI chỉ ĐỀ XUẤT: hiện cạnh bản gốc, người biên tập chọn Thay / Chèn bên dưới / Bỏ. Không có gì được lưu cho tới khi bấm Lưu.

import type { Editor } from '@tiptap/react';
import type { Node as PmNode } from '@tiptap/pm/model';
import { useState } from 'react';
import {
  AI_REWRITE_MODES, AI_REWRITE_NOTE_MAX, aiErrorText, requestRewrite, rewriteInputError, type AiRewriteMode,
} from '../../lib/ai';
import { ApiError } from '../../lib/api';

interface Pending {
  from: number;
  to: number;
  doc: PmNode;
  original: string;
}

/** Văn bản AI → nội dung chèn: đoạn cách nhau bằng dòng trống, xuống dòng đơn giữ nguyên. */
export function textToInsertContent(text: string, inline: boolean) {
  const lines = (s: string) => s.split('\n').flatMap((part, i) => [...(i > 0 ? [{ type: 'hardBreak' }] : []), ...(part ? [{ type: 'text', text: part }] : [])]);
  const paragraphs = text.split(/\n{2,}/).map((p) => p.trim()).filter(Boolean);
  if (inline && paragraphs.length <= 1) return lines(paragraphs[0] ?? '');
  return paragraphs.map((p) => ({ type: 'paragraph', content: lines(p) }));
}

export function AiRewritePanel({ editor, contentId, onClose }: { editor: Editor; contentId: string; onClose: () => void }) {
  const [mode, setMode] = useState<AiRewriteMode>('IMPROVE');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);
  const [result, setResult] = useState<string | null>(null);

  const run = async () => {
    const { from, to } = editor.state.selection;
    const original = editor.state.doc.textBetween(from, to, '\n\n', '\n').trim();
    const inputError = rewriteInputError(original, note);
    if (inputError) return setError(inputError);
    setBusy(true);
    setError(null);
    setResult(null);
    const snapshot: Pending = { from, to, doc: editor.state.doc, original };
    try {
      const text = await requestRewrite({ contentId, text: original, mode, note });
      setPending(snapshot);
      setResult(text);
    } catch (e) {
      setError(e instanceof ApiError && e.code === 'VALIDATION_FAILED' ? (e.problem.detail ?? 'Đoạn chọn không hợp lệ để gửi AI.') : aiErrorText(e));
    } finally {
      setBusy(false);
    }
  };

  // Tài liệu đổi sau khi gửi → vị trí đoạn gốc có thể lệch: không cho thay, chỉ chèn ở con trỏ hoặc sao chép.
  const stale = !!pending && pending.doc !== editor.state.doc;
  const singleBlock = !!pending && editor.state.doc.resolve(pending.from).sameParent(editor.state.doc.resolve(pending.to));

  const replace = () => {
    if (!pending || !result || stale) return;
    editor.chain().focus().insertContentAt({ from: pending.from, to: pending.to }, textToInsertContent(result, singleBlock) as never).run();
    setResult(null);
    setPending(null);
  };

  const insertBelow = () => {
    if (!result) return;
    const at = stale || !pending ? editor.state.selection.to : pending.to;
    const $at = editor.state.doc.resolve(Math.min(at, editor.state.doc.content.size));
    const after = $at.depth > 0 ? $at.after(1) : $at.pos;
    editor.chain().focus().insertContentAt(after, textToInsertContent(result, false) as never).run();
    setResult(null);
    setPending(null);
  };

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(result ?? '');
    } catch {
      // trình duyệt chặn clipboard: bỏ qua
    }
  };

  return (
    <div className="rte-panel rte-ai stack-sm" role="group" aria-label="AI viết lại">
      <div className="rte-ai-row">
        <select value={mode} onChange={(e) => setMode(e.target.value as AiRewriteMode)} aria-label="Cách viết lại">
          {AI_REWRITE_MODES.map((m) => <option key={m.value} value={m.value}>{m.label}</option>)}
        </select>
        <input value={note} onChange={(e) => setNote(e.target.value)} maxLength={AI_REWRITE_NOTE_MAX} placeholder="Yêu cầu thêm (tùy chọn), ví dụ: giữ nguyên số liệu" aria-label="Yêu cầu thêm" />
        <button type="button" className="button small primary" disabled={busy} onMouseDown={(e) => e.preventDefault()} onClick={() => void run()}>
          {busy ? 'Đang viết…' : 'Viết lại đoạn đã chọn'}
        </button>
        <button type="button" className="button small" onClick={onClose}>Đóng</button>
      </div>
      <p className="muted small">
        Đoạn bạn chọn được gửi tới nhà cung cấp AI (có ghi nhật ký). Kết quả chỉ là đề xuất — đọc kỹ, kiểm tra số liệu và tên riêng trước khi dùng.
      </p>
      {error && <div className="banner error" role="alert">{error}</div>}
      {result && pending && (
        <div className="rte-ai-compare">
          <div>
            <strong className="small">Bản gốc</strong>
            <div className="rte-ai-text muted">{pending.original}</div>
          </div>
          <div>
            <strong className="small">AI đề xuất</strong>
            <div className="rte-ai-text">{result}</div>
          </div>
          {stale && <p className="banner warn small">Nội dung đã thay đổi sau khi gửi nên không thay thế tự động được. Có thể chèn ở vị trí con trỏ hoặc sao chép.</p>}
          <div className="row-end">
            <button type="button" className="button small primary" disabled={stale} onClick={replace}>Thay đoạn đã chọn</button>
            <button type="button" className="button small" onClick={insertBelow}>Chèn bên dưới</button>
            <button type="button" className="button small" onClick={() => void copy()}>Sao chép</button>
            <button type="button" className="button small" onClick={() => { setResult(null); setPending(null); }}>Bỏ</button>
          </div>
        </div>
      )}
    </div>
  );
}

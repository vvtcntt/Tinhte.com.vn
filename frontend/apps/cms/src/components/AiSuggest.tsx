'use client';

import { useState } from 'react';
import { AI_TASK_LABELS, aiErrorText, requestSuggestions, tagsToText, type AiTask } from '../lib/ai';
import { access } from '../lib/rbac';
import { useStaff } from './session';

// Nút "Gợi ý bằng AI" đặt ngay dưới ô tóm tắt của màn sửa bài/video (FR-AI-001). Chỉ hiện khi vai trò có ai.assist
// (ẨN/HIỆN giao diện — API vẫn kiểm quyền và cờ ai.assistance). AI đọc BẢN ĐÃ LƯU; kết quả chỉ là gợi ý, không tự ghi vào bài.

interface Result {
  task: AiTask;
  items: string[];
}

export function AiSuggest({ contentId, dirty, canApply, onUseSummary }: {
  contentId: string;
  /** Có thay đổi chưa lưu: nhắc rằng AI chỉ thấy bản đã lưu. */
  dirty: boolean;
  /** Được sửa bài lúc này (để hiện nút "Dùng"). */
  canApply: boolean;
  onUseSummary: (summary: string) => void;
}) {
  const staff = useStaff();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState<AiTask | null>(null);
  const [result, setResult] = useState<Result | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  if (access(staff, 'ai.assist') !== 'allowed') return null;

  const run = async (task: AiTask) => {
    setBusy(task);
    setError(null);
    setResult(null);
    setCopied(false);
    try {
      const items = await requestSuggestions(contentId, task);
      if (items.length === 0) setError('AI không đưa ra được gợi ý nào. Thử lại hoặc bổ sung nội dung.');
      else setResult({ task, items });
    } catch (err) {
      setError(aiErrorText(err));
    } finally {
      setBusy(null);
    }
  };

  const copy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };

  if (!open) {
    return (
      <div className="ai-suggest-trigger">
        <button type="button" className="button small ai-button" onClick={() => setOpen(true)} aria-expanded={false}>
          <span aria-hidden="true">✦</span> Gợi ý bằng AI
        </button>
      </div>
    );
  }

  return (
    <section className="ai-suggest" aria-label="Gợi ý bằng AI">
      <div className="ai-suggest-head">
        <strong>
          <span aria-hidden="true">✦</span> Gợi ý bằng AI
        </strong>
        <span className="row-end">
          {(['SUMMARY', 'TAG'] as const).map((task) => (
            <button key={task} type="button" className="button small" disabled={busy !== null} onClick={() => void run(task)}>
              {busy === task ? 'Đang gợi ý…' : AI_TASK_LABELS[task]}
            </button>
          ))}
          <button type="button" className="button small" onClick={() => { setOpen(false); setResult(null); setError(null); }} aria-label="Đóng gợi ý AI">
            Đóng
          </button>
        </span>
      </div>

      {dirty && <p className="muted small">AI đọc bản đã lưu. Lưu trước nếu muốn AI thấy thay đổi mới nhất.</p>}
      {busy && <p className="muted small" role="status">Đang gửi nội dung cho AI, thường mất vài giây…</p>}
      {error && (
        <div className="banner error" role="alert">
          {error}
        </div>
      )}

      {result?.task === 'SUMMARY' && (
        <ol className="ai-list">
          {result.items.map((summary) => (
            <li key={summary}>
              <span>{summary}</span>
              {canApply && (
                <button type="button" className="button small" onClick={() => onUseSummary(summary)}>
                  Dùng
                </button>
              )}
            </li>
          ))}
        </ol>
      )}

      {result?.task === 'TAG' && (
        <div className="stack-sm">
          <ul className="chip-list" aria-label="Tag gợi ý">
            {result.items.map((tag) => (
              <li key={tag} className="chip">
                {tag}
              </li>
            ))}
          </ul>
          <span className="row-end">
            {copied && <span className="muted small" role="status">Đã sao chép</span>}
            <button type="button" className="button small" onClick={() => void copy(tagsToText(result.items))}>
              Sao chép tag
            </button>
          </span>
        </div>
      )}

      <p className="muted small">
        AI chỉ gợi ý — kiểm tra lại trước khi dùng. “Dùng” chỉ điền vào ô, bài chưa đổi cho tới khi bạn bấm Lưu. Nội dung được gửi tới nhà cung
        cấp AI bên ngoài và mỗi lần gọi đều được ghi nhật ký.
      </p>
    </section>
  );
}

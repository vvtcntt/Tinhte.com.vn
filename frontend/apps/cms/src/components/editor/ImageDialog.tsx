'use client';

// Chèn ảnh vào thân bài/video: tải lên thư viện media (upload-intent → PUT → finalize → chờ READY) hoặc chọn ảnh có sẵn.
// Chỉ chèn ảnh READY và quyền chưa bị hạn chế/thu hồi/hết hạn (FR-CMS-009) — API kiểm lại khi lưu.

import { useEffect, useRef, useState } from 'react';
import { API_PREFIX, apiFetch } from '../../lib/api';
import { LIMITS, mediaContentPath } from '../../lib/bodyBlocks';
import {
  canInsertImage, formatBytes, IMAGE_ACCEPT, mediaListQuery, rightsLabel, videoErrorText, type MediaStatus, type Page,
} from '../../lib/videos';
import { MediaStatusBadge, useMediaUpload } from '../VideosUpload';

function altFromFileName(name: string | null | undefined): string {
  return (name ?? '').replace(/\.[a-z0-9]+$/i, '').replace(/[-_]+/g, ' ').trim().slice(0, LIMITS.alt);
}

export function ImageDialog({ canUpload, initialFile, onClose, onInsert }: {
  canUpload: boolean;
  initialFile: File | null;
  onClose: () => void;
  onInsert: (media: MediaStatus, alt: string) => void;
}) {
  const [tab, setTab] = useState<'upload' | 'library'>(canUpload ? 'upload' : 'library');
  const dialogRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    dialogRef.current?.focus();
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="rte-overlay" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="rte-dialog card stack-sm" role="dialog" aria-modal="true" aria-labelledby="rte-image-title" ref={dialogRef} tabIndex={-1}>
        <div className="title-row">
          <h2 id="rte-image-title">Chèn ảnh</h2>
          <span className="tabs">
            {canUpload && (
              <button type="button" className={tab === 'upload' ? 'tab active' : 'tab'} onClick={() => setTab('upload')}>Tải lên</button>
            )}
            <button type="button" className={tab === 'library' ? 'tab active' : 'tab'} onClick={() => setTab('library')}>Thư viện</button>
          </span>
        </div>
        {tab === 'upload' && canUpload ? <UploadTab initialFile={initialFile} onInsert={onInsert} /> : <LibraryTab onInsert={onInsert} />}
        {!canUpload && initialFile && <p className="banner warn">Vai trò của bạn chưa được tải media lên (media.upload). Chọn ảnh có sẵn trong thư viện.</p>}
        <div className="row-end">
          <button type="button" className="button small" onClick={onClose}>Đóng</button>
        </div>
      </div>
    </div>
  );
}

function UploadTab({ initialFile, onInsert }: { initialFile: File | null; onInsert: (media: MediaStatus, alt: string) => void }) {
  const { state, start, retry, cancel, reset } = useMediaUpload();
  const [alt, setAlt] = useState(altFromFileName(initialFile?.name));
  const started = useRef(false);

  useEffect(() => {
    if (initialFile && !started.current) {
      started.current = true;
      start(initialFile);
    }
  }, [initialFile, start]);

  const media = state.media;
  const busy = ['hashing', 'intent', 'uploading', 'finalizing'].includes(state.phase);
  const ready = !!media && state.phase === 'ready';
  const insertable = ready && canInsertImage(media);

  if (state.phase === 'idle') {
    return (
      <label className="upload-drop">
        <input
          type="file"
          accept={IMAGE_ACCEPT}
          className="visually-hidden"
          onChange={(e) => {
            const file = e.target.files?.[0];
            e.target.value = '';
            if (file) { setAlt(altFromFileName(file.name)); start(file); }
          }}
        />
        <span className="upload-drop-title">Chọn ảnh để tải lên</span>
        <span className="muted">JPEG, PNG hoặc WebP. Có thể dán (Ctrl+V) hay kéo thả ảnh thẳng vào trình soạn.</span>
      </label>
    );
  }

  return (
    <div className="upload-box stack-sm" aria-live="polite">
      <div className="upload-file">
        <strong className="upload-name">{state.file?.name}</strong>
        <span className="muted">{formatBytes(state.file?.size)}</span>
        {media && <MediaStatusBadge status={media.status} />}
      </div>
      {busy && <progress className="upload-progress" max={1} value={state.phase === 'uploading' || state.phase === 'hashing' ? state.progress : undefined} />}
      {state.phase === 'processing' && <p className="muted small">Ảnh đang được kiểm tra. Có thể đợi ở đây — tự cập nhật.</p>}
      {state.error && <div className={state.phase === 'rejected' ? 'banner error' : 'banner warn'} role="alert">{state.error}</div>}
      {ready && !insertable && (
        <div className="banner warn" role="status">
          Tệp này không chèn được vào bài ({media.mediaType !== 'IMAGE' ? 'không phải ảnh' : `quyền: ${rightsLabel(media.rightsStatus)}`}).
        </div>
      )}
      {(ready || state.phase === 'processing') && (
        <label className="field">
          <span>Mô tả ảnh (alt)</span>
          <input value={alt} onChange={(e) => setAlt(e.target.value)} maxLength={LIMITS.alt} placeholder="Ảnh thể hiện gì? (cho người đọc màn hình, SEO)" />
        </label>
      )}
      <div className="row-end">
        {busy && <button type="button" className="button small" onClick={cancel}>Hủy</button>}
        {state.phase === 'error' && <button type="button" className="button small primary" onClick={retry}>Thử lại</button>}
        {(state.phase === 'error' || state.phase === 'rejected' || ready) && <button type="button" className="button small" onClick={reset}>Chọn ảnh khác</button>}
        <button type="button" className="button small primary" disabled={!insertable} onClick={() => insertable && onInsert(media, alt.trim())}>
          Chèn vào bài
        </button>
      </div>
    </div>
  );
}

function LibraryTab({ onInsert }: { onInsert: (media: MediaStatus, alt: string) => void }) {
  const [q, setQ] = useState('');
  const [items, setItems] = useState<MediaStatus[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [picked, setPicked] = useState<MediaStatus | null>(null);
  const [alt, setAlt] = useState('');

  const load = async (query: string, after: string | null) => {
    setLoading(true);
    setError(null);
    try {
      const { data } = await apiFetch<Page<MediaStatus>>(mediaListQuery({ mediaType: 'IMAGE', rightsStatus: '', visibility: '', q: query }, after, 24));
      setItems((prev) => (after ? [...prev, ...(data?.items ?? [])] : data?.items ?? []));
      setCursor(data?.nextCursor ?? null);
    } catch (e) {
      setError(videoErrorText(e));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    const t = window.setTimeout(() => void load(q, null), q ? 300 : 0);
    return () => window.clearTimeout(t);
  }, [q]);

  return (
    <div className="stack-sm">
      <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Tìm theo tên tệp…" aria-label="Tìm ảnh trong thư viện" />
      {error && <div className="banner error" role="alert">{error}</div>}
      <div className="rte-library" role="listbox" aria-label="Ảnh trong thư viện">
        {items.map((m) => {
          const ok = canInsertImage(m);
          return (
            <button
              key={m.mediaId}
              type="button"
              role="option"
              aria-selected={picked?.mediaId === m.mediaId}
              className={`rte-thumb${picked?.mediaId === m.mediaId ? ' picked' : ''}`}
              disabled={!ok}
              title={ok ? m.fileName ?? m.mediaId : `Không chèn được: ${m.status !== 'READY' ? 'chưa sẵn sàng' : `quyền ${rightsLabel(m.rightsStatus)}`}`}
              onClick={() => { setPicked(m); setAlt(altFromFileName(m.fileName)); }}
            >
              {/* eslint-disable-next-line @next/next/no-img-element -- ảnh thư viện qua API cùng origin */}
              {m.status === 'READY' ? <img src={`${API_PREFIX}${mediaContentPath(m.mediaId)}`} alt="" loading="lazy" /> : <span className="rte-thumb-empty"><MediaStatusBadge status={m.status} /></span>}
              <span className="rte-thumb-name">{m.fileName ?? m.mediaId.slice(0, 8)}</span>
            </button>
          );
        })}
        {!loading && items.length === 0 && !error && <p className="muted">Chưa có ảnh nào{q ? ' khớp' : ''}.</p>}
      </div>
      <div className="row-end">
        {loading && <span className="muted">Đang tải…</span>}
        {!loading && cursor && <button type="button" className="button small" onClick={() => void load(q, cursor)}>Xem thêm</button>}
      </div>
      {picked && (
        <div className="stack-sm">
          <label className="field">
            <span>Mô tả ảnh (alt)</span>
            <input value={alt} onChange={(e) => setAlt(e.target.value)} maxLength={LIMITS.alt} autoFocus />
          </label>
          <div className="row-end">
            <button type="button" className="button small primary" onClick={() => onInsert(picked, alt.trim())}>Chèn vào bài</button>
          </div>
        </div>
      )}
    </div>
  );
}

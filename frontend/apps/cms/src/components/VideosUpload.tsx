'use client';

import { useCallback, useEffect, useRef, useState, type ChangeEvent } from 'react';
import { ApiError, CSRF_COOKIE, CSRF_HEADER, apiFetch, readCookie, toProblem } from '../lib/api';
import {
  formatBytes, guessContentType, isMediaFailed, isMediaInFlight, isSameOrigin, mediaErrorLabel, mediaStatusLabel, mediaTone,
  pollDelayMs, sha256Blob, videoErrorText, type MediaStatus, type UploadIntent,
} from '../lib/videos';

// Tải media lên theo hợp đồng v0.3 (WBS-046, ADR-0006):
//   băm SHA-256 → POST /admin/media/upload-intents → PUT tệp lên uploadUrl (có tiến độ) → POST …/finalize → GET …/{id} theo chu kỳ.
// Mỗi bước lỗi đều thử lại được từ đúng bước đó (intent còn hạn thì không tạo lại; tệp hỏng thì tải lại lên cùng URL).

export type UploadPhase = 'idle' | 'hashing' | 'intent' | 'uploading' | 'finalizing' | 'processing' | 'ready' | 'rejected' | 'error';
type Step = 'hash' | 'intent' | 'upload' | 'finalize' | 'poll';

export interface UploadState {
  phase: UploadPhase;
  file: File | null;
  progress: number;
  intent: UploadIntent | null;
  media: MediaStatus | null;
  error: string | null;
  failedStep: Step | null;
  /** Lỗi finalize do tệp: chưa có trên kho (MEDIA_NOT_READY) hoặc khác kích thước/checksum (API đã xóa) → thử lại = tải lên lại. */
  reupload: boolean;
  /** Intent hết hạn (STATE_CONFLICT khi finalize) → thử lại = tạo intent mới. */
  renewIntent: boolean;
}

const INITIAL: UploadState = {
  phase: 'idle', file: null, progress: 0, intent: null, media: null, error: null, failedStep: null, reupload: false, renewIntent: false,
};

const PHASE_LABEL: Record<UploadPhase, string> = {
  idle: '',
  hashing: 'Đang tính checksum…',
  intent: 'Đang tạo phiên tải lên…',
  uploading: 'Đang tải lên…',
  finalizing: 'Đang hoàn tất tải lên…',
  processing: 'Đang xử lý',
  ready: 'Sẵn sàng',
  rejected: 'Không dùng được tệp này',
  error: 'Tải lên bị gián đoạn',
};

/** PUT tệp lên uploadUrl bằng XHR (fetch chưa báo tiến độ upload). Cùng origin CMS → kèm X-CSRF-Token; URL ký sẵn của kho ngoài → không. */
function putFile(url: string, file: File, contentType: string, onProgress: (fraction: number) => void, register: (xhr: XMLHttpRequest | null) => void): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    register(xhr);
    xhr.open('PUT', url);
    xhr.setRequestHeader('content-type', contentType);
    if (isSameOrigin(url, window.location.origin)) {
      const csrf = readCookie(document.cookie, CSRF_COOKIE);
      if (csrf) xhr.setRequestHeader(CSRF_HEADER, csrf);
      xhr.setRequestHeader('accept', 'application/json');
    }
    xhr.upload.onprogress = (e) => e.lengthComputable && onProgress(e.loaded / e.total);
    xhr.onload = () => {
      register(null);
      if (xhr.status >= 200 && xhr.status < 300) return resolve();
      let body: unknown = null;
      try {
        body = xhr.responseText ? JSON.parse(xhr.responseText) : null;
      } catch {
        body = null;
      }
      reject(new ApiError(toProblem(xhr.status, body)));
    };
    xhr.onerror = () => {
      register(null);
      reject(new TypeError('Network error'));
    };
    xhr.onabort = () => {
      register(null);
      reject(new DOMException('Aborted', 'AbortError'));
    };
    xhr.send(file);
  });
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

/** Trạng thái tải lên một tệp. onMedia được gọi khi có media mới (intent) và mỗi lần trạng thái media đổi. */
export function useMediaUpload(onMedia?: (media: MediaStatus) => void) {
  const [state, setState] = useState<UploadState>(INITIAL);
  const stateRef = useRef(state);
  stateRef.current = state;
  const alive = useRef(true);
  const xhr = useRef<XMLHttpRequest | null>(null);
  const abort = useRef<AbortController | null>(null);
  const contentType = useRef('');
  const checksum = useRef('');
  const intentKey = useRef('');
  const onMediaRef = useRef(onMedia);
  onMediaRef.current = onMedia;

  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
      xhr.current?.abort();
      abort.current?.abort();
    };
  }, []);

  const patch = useCallback((p: Partial<UploadState>) => {
    if (alive.current) setState((s) => ({ ...s, ...p }));
  }, []);

  const fail = useCallback((step: Step, e: unknown) => {
    if (!alive.current) return;
    if (e instanceof DOMException && e.name === 'AbortError') {
      patch({ phase: 'error', failedStep: step, error: 'Đã hủy. Bấm "Thử lại" để tiếp tục.' });
      return;
    }
    const fileProblem = e instanceof ApiError && (e.code === 'MEDIA_NOT_READY'
      || (e.code === 'VALIDATION_FAILED' && !!e.problem.errors && ('sizeBytes' in e.problem.errors || 'checksumSha256' in e.problem.errors)));
    patch({
      phase: 'error', failedStep: step, error: videoErrorText(e),
      reupload: step === 'finalize' && fileProblem,
      renewIntent: step === 'finalize' && e instanceof ApiError && e.code === 'STATE_CONFLICT',
    });
  }, [patch]);

  const poll = useCallback(async (mediaId: string) => {
    patch({ phase: 'processing', error: null, failedStep: null, reupload: false, renewIntent: false });
    for (let attempt = 0; alive.current; attempt++) {
      let media: MediaStatus | null;
      try {
        media = (await apiFetch<MediaStatus>(`/admin/media/${encodeURIComponent(mediaId)}`)).data;
      } catch (e) {
        return fail('poll', e);
      }
      if (!media || !alive.current) return;
      onMediaRef.current?.(media);
      if (media.status === 'READY') return patch({ phase: 'ready', media });
      if (isMediaFailed(media.status)) return patch({ phase: 'rejected', media, error: mediaErrorLabel(media.errorCode) });
      patch({ media });
      if (!isMediaInFlight(media.status)) return;   // UPLOADING: chưa finalize — không hỏi mãi
      await sleep(pollDelayMs(attempt));
    }
  }, [fail, patch]);

  const finalize = useCallback(async (intent: UploadIntent) => {
    patch({ phase: 'finalizing', error: null, failedStep: null, reupload: false, renewIntent: false });
    try {
      // Mỗi lần finalize một khóa mới: finalize lặp lại là an toàn (API trả trạng thái hiện tại).
      await apiFetch(`/admin/media/${encodeURIComponent(intent.mediaId)}/finalize`, {
        method: 'POST',
        body: { uploadToken: intent.uploadToken },
        idempotencyKey: crypto.randomUUID(),
      });
    } catch (e) {
      return fail('finalize', e);
    }
    await poll(intent.mediaId);
  }, [fail, patch, poll]);

  const upload = useCallback(async (intent: UploadIntent, file: File) => {
    patch({ phase: 'uploading', progress: 0, error: null, failedStep: null, reupload: false, renewIntent: false });
    try {
      await putFile(intent.uploadUrl, file, contentType.current, (p) => patch({ progress: p }), (x) => (xhr.current = x));
    } catch (e) {
      return fail('upload', e);
    }
    await finalize(intent);
  }, [fail, finalize, patch]);

  const createIntent = useCallback(async (file: File) => {
    patch({ phase: 'intent', error: null, failedStep: null, reupload: false, renewIntent: false });
    let intent: UploadIntent | null;
    try {
      intent = (await apiFetch<UploadIntent>('/admin/media/upload-intents', {
        method: 'POST',
        body: { fileName: file.name, contentType: contentType.current, sizeBytes: file.size, checksumSha256: checksum.current },
        idempotencyKey: intentKey.current,
      })).data;
    } catch (e) {
      // 4xx không để lại gì phía API: lần sau là thao tác mới. Lỗi mạng/5xx: giữ khóa để không tạo hai media.
      if (e instanceof ApiError && e.status < 500) intentKey.current = crypto.randomUUID();
      return fail('intent', e);
    }
    if (!intent) return fail('intent', new Error('empty'));
    patch({ intent });
    onMediaRef.current?.({
      mediaId: intent.mediaId, status: 'UPLOADING', generation: 1, errorCode: null, visibility: 'PRIVATE', rightsStatus: 'UNVERIFIED',
      licenseId: null, rightsExpiresAt: null, createdAt: new Date().toISOString(), fileName: file.name, contentType: contentType.current, sizeBytes: file.size,
    });
    await upload(intent, file);
  }, [fail, patch, upload]);

  const hashAndStart = useCallback(async (file: File) => {
    patch({ phase: 'hashing', progress: 0, error: null, failedStep: null, reupload: false, renewIntent: false });
    abort.current = new AbortController();
    try {
      checksum.current = await sha256Blob(file, (p) => patch({ progress: p }), abort.current.signal);
    } catch (e) {
      return fail('hash', e);
    }
    intentKey.current = crypto.randomUUID();
    await createIntent(file);
  }, [createIntent, fail, patch]);

  const start = useCallback((file: File) => {
    const type = guessContentType(file.name, file.type);
    setState({ ...INITIAL, file });
    if (!type) {
      setState({ ...INITIAL, file, phase: 'rejected', error: 'Loại tệp không được hỗ trợ. Chọn MP4, MOV, WebM (video), JPEG/PNG/WebP (ảnh) hoặc WebVTT (phụ đề).' });
      return;
    }
    contentType.current = type;
    void hashAndStart(file);
  }, [hashAndStart]);

  /** Thử lại từ đúng bước đã lỗi. */
  const retry = useCallback(() => {
    const s = stateRef.current;
    if (!s.file) return;
    const expired = s.intent && new Date(s.intent.expiresAt).getTime() <= Date.now() + 5000;
    switch (s.failedStep) {
      case 'hash':
        return void hashAndStart(s.file);
      case 'intent':
        return void createIntent(s.file);
      case 'upload':
      case 'finalize':
        if (!s.intent || expired || s.renewIntent) {
          intentKey.current = crypto.randomUUID();
          return void createIntent(s.file);
        }
        // Lỗi tải, hoặc finalize báo thiếu tệp / sai checksum (API đã xóa tệp hỏng): tải lại lên cùng URL rồi finalize.
        return void (s.failedStep === 'upload' || s.reupload ? upload(s.intent, s.file) : finalize(s.intent));
      case 'poll':
        return s.intent ? void poll(s.intent.mediaId) : undefined;
      default:
        return undefined;
    }
  }, [createIntent, finalize, hashAndStart, poll, upload]);

  const cancel = useCallback(() => {
    xhr.current?.abort();
    abort.current?.abort();
  }, []);

  const reset = useCallback(() => {
    cancel();
    setState(INITIAL);
  }, [cancel]);

  return { state, start, retry, cancel, reset };
}

export function MediaStatusBadge({ status }: { status: string }) {
  const tone = mediaTone(status);
  return <span className={`badge ${tone === 'ok' ? 'ok' : tone === 'warn' ? 'warn' : tone === 'error' ? 'bad' : ''}`}>{mediaStatusLabel(status)}</span>;
}

/** Hộp tải lên: chọn tệp → tiến trình → trạng thái xử lý cập nhật theo chu kỳ → thử lại khi lỗi. */
export function UploadBox({ accept, hint, onMedia, disabled = false }: {
  accept: string;
  hint: string;
  onMedia?: (media: MediaStatus) => void;
  disabled?: boolean;
}) {
  const { state, start, retry, cancel, reset } = useMediaUpload(onMedia);
  const busy = state.phase === 'hashing' || state.phase === 'intent' || state.phase === 'uploading' || state.phase === 'finalizing';
  const onPick = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (file) start(file);
  };

  if (state.phase === 'idle') {
    return (
      <label className={`upload-drop${disabled ? ' disabled' : ''}`}>
        <input type="file" accept={accept} onChange={onPick} disabled={disabled} className="visually-hidden" />
        <span className="upload-drop-title">Chọn tệp để tải lên</span>
        <span className="muted">{hint}</span>
      </label>
    );
  }

  const percent = Math.round(state.progress * 100);
  return (
    <div className="upload-box" aria-live="polite">
      <div className="upload-file">
        <strong className="upload-name">{state.file?.name}</strong>
        <span className="muted">{formatBytes(state.file?.size)}</span>
      </div>

      {busy && (
        <div className="stack-xs">
          <div className="upload-phase">
            <span>{PHASE_LABEL[state.phase]}</span>
            {(state.phase === 'hashing' || state.phase === 'uploading') && <span className="muted">{percent}%</span>}
          </div>
          <progress className="upload-progress" max={1} value={state.phase === 'hashing' || state.phase === 'uploading' ? state.progress : undefined} />
        </div>
      )}

      {(state.phase === 'processing' || state.phase === 'ready') && state.media && (
        <div className="upload-phase">
          <MediaStatusBadge status={state.media.status} />
          {state.phase === 'processing' && <span className="muted">Tự cập nhật trạng thái…</span>}
        </div>
      )}

      {state.error && (
        <div className={state.phase === 'rejected' ? 'banner error' : 'banner warn'} role="alert">
          {state.phase === 'rejected' && state.media && <MediaStatusBadge status={state.media.status} />}
          <span>{state.error}</span>
        </div>
      )}

      <div className="row-end">
        {busy && (
          <button type="button" className="button small" onClick={cancel}>
            Hủy
          </button>
        )}
        {state.phase === 'error' && (
          <button type="button" className="button small primary" onClick={retry}>
            Thử lại
          </button>
        )}
        {(state.phase === 'error' || state.phase === 'rejected' || state.phase === 'ready') && !disabled && (
          <button type="button" className="button small" onClick={reset}>
            {state.phase === 'ready' ? 'Tải tệp khác' : 'Chọn tệp khác'}
          </button>
        )}
      </div>
    </div>
  );
}

/** Trạng thái một media đã có: tải một lần, tự hỏi lại khi còn trong pipeline. */
export function useMediaStatus(mediaId: string | null) {
  const [media, setMedia] = useState<MediaStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    if (!mediaId) {
      setMedia(null);
      return;
    }
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const load = async (attempt: number) => {
      try {
        const { data } = await apiFetch<MediaStatus>(`/admin/media/${encodeURIComponent(mediaId)}`);
        if (cancelled) return;
        setMedia(data);
        setError(null);
        if (data && isMediaInFlight(data.status)) timer = setTimeout(() => void load(attempt + 1), pollDelayMs(attempt));
      } catch (e) {
        if (!cancelled) setError(videoErrorText(e));
      }
    };
    void load(0);
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [mediaId, tick]);

  return { media, error, refresh: () => setTick((t) => t + 1) };
}

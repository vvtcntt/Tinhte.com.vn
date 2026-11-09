'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { ApiError, apiFetch } from '../lib/api';
import { access } from '../lib/rbac';
import {
  ANY_ACCEPT, CONTENT_STATUSES, VIDEO_ACCEPT, canAttachToVideo, contentStatusLabel, draftFromVideo, formatBytes, isMediaInFlight,
  mediaErrorLabel, mediaListQuery, mediaTypeLabel, pollDelayMs, rightsLabel, titleFromFileName, videoErrorText, videoListQuery, videoPatchBody,
  type AdminVideo, type MediaListFilter, type MediaStatus, type Page, type VideoDraft, type VideoListFilter,
} from '../lib/videos';
import { RequirePermission } from './CmsApp';
import { useStaff } from './session';
import { MediaStatusBadge, UploadBox, useMediaStatus } from './VideosUpload';

// Màn Video + thư viện media (WBS-046; UX-CMS-03/05, FR-CMS-009). API: /api/v1/admin/videos*, /api/v1/admin/media* (hợp đồng v0.3).
// Ẩn/hiện theo quyền chỉ là giao diện — API vẫn kiểm (401/403).

const VIDEO_HINT = 'MP4, MOV hoặc WebM. Tệp được kiểm checksum và giữ PRIVATE; tải lên không cấp quyền phát hành.';

function ContentStatusBadge({ status }: { status: string }) {
  const tone = status === 'PUBLISHED' ? 'ok' : status === 'REJECTED' || status === 'RETRACTED' ? 'bad' : status === 'DRAFT' ? '' : 'warn';
  return <span className={`badge ${tone}`}>{contentStatusLabel(status)}</span>;
}

function RightsBadge({ status }: { status: string }) {
  return <span className={`badge ${status === 'CLEARED' ? 'ok' : status === 'UNVERIFIED' ? '' : 'bad'}`}>{rightsLabel(status)}</span>;
}

const formatTime = (iso: string | null | undefined) => (iso ? new Date(iso).toLocaleString('vi-VN') : '—');

function VideoTabs({ active }: { active: 'videos' | 'media' }) {
  return (
    <nav className="tabs" aria-label="Video">
      <Link href="/videos" className={active === 'videos' ? 'tab active' : 'tab'} aria-current={active === 'videos' ? 'page' : undefined}>
        Video
      </Link>
      <Link href="/videos/media" className={active === 'media' ? 'tab active' : 'tab'} aria-current={active === 'media' ? 'page' : undefined}>
        Thư viện media
      </Link>
    </nav>
  );
}

function ErrorBanner({ text, children }: { text: string | null; children?: ReactNode }) {
  if (!text) return null;
  return (
    <div className="banner error" role="alert">
      <span>{text}</span>
      {children}
    </div>
  );
}

/** Tải một trang danh sách cursor; "Xem thêm" nối trang sau. */
function useCursorList<T>(buildPath: (cursor: string | null) => string, deps: unknown[]) {
  const [items, setItems] = useState<T[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (after: string | null) => {
    setLoading(true);
    setError(null);
    try {
      const { data } = await apiFetch<Page<T>>(buildPath(after));
      setItems((prev) => (after ? [...prev, ...(data?.items ?? [])] : data?.items ?? []));
      setCursor(data?.nextCursor ?? null);
    } catch (e) {
      setError(videoErrorText(e));
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return { items, setItems, cursor, loading, error, load };
}

// ---------------------------------------------------------------- danh sách video

export function VideosPage() {
  return (
    <RequirePermission permission="content.read">
      <VideoList />
    </RequirePermission>
  );
}

function VideoList() {
  const staff = useStaff();
  const canCreate = access(staff, 'content.write_own') === 'allowed';
  const [filter, setFilter] = useState<VideoListFilter>({ status: '', q: '', mine: false, sort: 'updatedAt_desc' });
  const [q, setQ] = useState('');
  const list = useCursorList<AdminVideo>((c) => videoListQuery(filter, staff.userId, c), [filter, staff.userId]);
  const { load } = list;

  useEffect(() => void load(null), [load]);

  const onSearch = (e: FormEvent) => {
    e.preventDefault();
    setFilter((f) => ({ ...f, q }));
  };

  return (
    <section className="stack">
      <div className="title-row spread">
        <div>
          <h1>Video</h1>
          <p className="muted">Video ngắn đang soạn và trạng thái xử lý media. Duyệt và xuất bản video là bước sau (WBS-040).</p>
        </div>
        {canCreate && (
          <Link href="/videos/new" className="button primary">
            Tải video lên
          </Link>
        )}
      </div>
      <VideoTabs active="videos" />

      <form className="toolbar" onSubmit={onSearch} role="search">
        <label className="field grow">
          <span>Tìm</span>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Tiêu đề hoặc slug" maxLength={200} />
        </label>
        <label className="field">
          <span>Trạng thái</span>
          <select value={filter.status} onChange={(e) => setFilter((f) => ({ ...f, status: e.target.value }))}>
            <option value="">Tất cả</option>
            {CONTENT_STATUSES.map((s) => (
              <option key={s} value={s}>
                {contentStatusLabel(s)}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Sắp xếp</span>
          <select value={filter.sort} onChange={(e) => setFilter((f) => ({ ...f, sort: e.target.value }))}>
            <option value="updatedAt_desc">Mới sửa trước</option>
            <option value="createdAt_desc">Mới tạo trước</option>
            <option value="title_asc">Tiêu đề A→Z</option>
          </select>
        </label>
        <label className="check inline">
          <input type="checkbox" checked={filter.mine} onChange={(e) => setFilter((f) => ({ ...f, mine: e.target.checked }))} />
          <span>Của tôi</span>
        </label>
        <button type="submit" className="button primary">
          Tìm
        </button>
      </form>

      <ErrorBanner text={list.error} />

      <div className="card flush">
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Tiêu đề</th>
              <th scope="col">Trạng thái</th>
              <th scope="col" className="hide-sm">Media</th>
              <th scope="col" className="hide-sm">Quyền</th>
              <th scope="col" className="hide-sm">Cập nhật</th>
            </tr>
          </thead>
          <tbody>
            {list.items.map((v) => (
              <tr key={v.id}>
                <td>
                  <Link href={`/videos/${v.id}`}>{v.title || '(chưa có tiêu đề)'}</Link>
                  {v.ownerId === staff.userId && <span className="muted small"> · của tôi</span>}
                  {v.contentKind === 'SPONSORED' && <span className="badge warn small-badge">Tài trợ</span>}
                </td>
                <td>
                  <ContentStatusBadge status={v.status} />
                </td>
                <td className="hide-sm">{v.mediaId ? <span className="muted">Đã gắn</span> : <span className="badge warn">Chưa có media</span>}</td>
                <td className="hide-sm">
                  <RightsBadge status={v.rightsStatus} />
                </td>
                <td className="hide-sm muted">{formatTime(v.updatedAt)}</td>
              </tr>
            ))}
            {!list.loading && list.items.length === 0 && (
              <tr>
                <td colSpan={5} className="muted center">
                  Chưa có video nào khớp bộ lọc.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="row-end">
        {list.loading && <span className="muted">Đang tải…</span>}
        {!list.loading && list.cursor && (
          <button type="button" className="button" onClick={() => void load(list.cursor)}>
            Xem thêm
          </button>
        )}
      </div>
    </section>
  );
}

// ---------------------------------------------------------------- tạo video

export function VideoNewPage() {
  return (
    <RequirePermission permission="content.write_own">
      <VideoNew />
    </RequirePermission>
  );
}

function VideoNew() {
  const staff = useStaff();
  const router = useRouter();
  const canUpload = access(staff, 'media.upload') === 'allowed';
  const [title, setTitle] = useState('');
  const [summary, setSummary] = useState('');
  const [media, setMedia] = useState<MediaStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID());

  const onMedia = useCallback((m: MediaStatus) => {
    setMedia(m);
    if (m.fileName) setTitle((t) => t || titleFromFileName(m.fileName!));
  }, []);

  // Chỉ gắn media đã finalize (đang xử lý/sẵn sàng): rời trang khi đang tải lên sẽ làm gián đoạn tải lên.
  const uploading = media !== null && media.status === 'UPLOADING';
  const attachable = media !== null && !uploading && canAttachToVideo(media);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const { data } = await apiFetch<AdminVideo>('/admin/videos', {
        method: 'POST',
        body: { title: title.trim(), summary: summary.trim() || null, ...(attachable ? { mediaId: media!.mediaId } : {}) },
        idempotencyKey,
      });
      if (data) router.push(`/videos/${data.id}`);
    } catch (err) {
      setError(videoErrorText(err));
      if (err instanceof ApiError && err.status < 500) setIdempotencyKey(crypto.randomUUID());
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="stack">
      <Link href="/videos" className="back">
        ← Video
      </Link>
      <h1>Tải video lên</h1>

      <div className="grid wide">
        <div className="card stack-sm">
          <h2>Tệp video</h2>
          {canUpload ? (
            <UploadBox accept={VIDEO_ACCEPT} hint={VIDEO_HINT} onMedia={onMedia} />
          ) : (
            <p className="muted">Vai trò của bạn không có quyền tải media lên. Bạn vẫn tạo được bản nháp rồi nhờ người có quyền gắn tệp.</p>
          )}
        </div>

        <form className="card stack-sm" onSubmit={submit}>
          <h2>Thông tin</h2>
          <label className="field">
            <span>Tiêu đề</span>
            <input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={300} required />
          </label>
          <label className="field">
            <span>Mô tả ngắn</span>
            <textarea value={summary} onChange={(e) => setSummary(e.target.value)} rows={3} />
          </label>
          {uploading && <p className="muted">Đợi tải lên xong để gắn tệp vào video.</p>}
          {media && !uploading && !attachable && <p className="muted">Tệp này không gắn được vào video; bản nháp sẽ được tạo không kèm media.</p>}
          <ErrorBanner text={error} />
          <div className="row-end">
            <button type="submit" className="button primary" disabled={busy || uploading || !title.trim()}>
              {busy ? 'Đang lưu…' : 'Lưu bản nháp'}
            </button>
          </div>
        </form>
      </div>
    </section>
  );
}

// ---------------------------------------------------------------- chi tiết + sửa video

export function VideoDetailPage({ videoId }: { videoId: string }) {
  return (
    <RequirePermission permission="content.read">
      <VideoDetail videoId={videoId} />
    </RequirePermission>
  );
}

function VideoDetail({ videoId }: { videoId: string }) {
  const staff = useStaff();
  const [video, setVideo] = useState<AdminVideo | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoadError(null);
    try {
      const response = await apiFetch<AdminVideo>(`/admin/videos/${encodeURIComponent(videoId)}`);
      setVideo(response.data);
      setEtag(response.etag);
      return response;
    } catch (e) {
      setLoadError(e instanceof ApiError && e.status === 404 ? 'Không tìm thấy video này.' : videoErrorText(e));
      return null;
    }
  }, [videoId]);

  useEffect(() => void reload(), [reload]);

  if (loadError) {
    return (
      <section className="stack">
        <Link href="/videos" className="back">
          ← Video
        </Link>
        <ErrorBanner text={loadError} />
      </section>
    );
  }
  if (!video) return <p className="muted">Đang tải…</p>;

  const isOwner = video.ownerId === staff.userId;
  const canEdit = access(staff, isOwner ? 'content.write_own' : 'content.write_any') === 'allowed';
  const applied = (v: AdminVideo, newEtag: string | null) => {
    setVideo(v);
    setEtag(newEtag);
  };

  return (
    <section className="stack">
      <Link href="/videos" className="back">
        ← Video
      </Link>
      <div className="title-row">
        <h1>{video.title || '(chưa có tiêu đề)'}</h1>
        <ContentStatusBadge status={video.status} />
        <RightsBadge status={video.rightsStatus} />
      </div>
      {!canEdit && <div className="banner warn">Bạn chỉ có quyền xem video này{isOwner ? '' : ' (video của người khác cần quyền sửa mọi nội dung)'}.</div>}

      <div className="grid wide">
        <VideoEditor video={video} etag={etag} canEdit={canEdit} onSaved={applied} reload={reload} />
        <div className="stack">
          <MediaPanel video={video} etag={etag} canEdit={canEdit} onSaved={applied} reload={reload} />
          <div className="card">
            <h2>Thông tin</h2>
            <dl className="facts">
              <dt>Mã video</dt>
              <dd>
                <code>{video.id}</code>
              </dd>
              <dt>Người tạo</dt>
              <dd>{isOwner ? 'Bạn' : <code>{video.ownerId ?? '—'}</code>}</dd>
              <dt>Bản đang soạn</dt>
              <dd>{video.workingRevisionId ? <code>{video.workingRevisionId}</code> : '—'}</dd>
              <dt>Bản công khai</dt>
              <dd>{video.publicRevisionId ? <code>{video.publicRevisionId}</code> : 'Chưa xuất bản'}</dd>
              <dt>Cập nhật</dt>
              <dd>
                {formatTime(video.updatedAt)} <span className="muted">(phiên bản {video.rowVersion})</span>
              </dd>
            </dl>
          </div>
        </div>
      </div>
    </section>
  );
}

type Reload = () => Promise<{ data: AdminVideo | null; etag: string | null } | null>;

function VideoEditor({ video, etag, canEdit, onSaved, reload }: {
  video: AdminVideo;
  etag: string | null;
  canEdit: boolean;
  onSaved: (v: AdminVideo, etag: string | null) => void;
  reload: Reload;
}) {
  const [draft, setDraft] = useState<VideoDraft>(() => draftFromVideo(video));
  // Bản video mà form được dựng từ đó: video đổi từ nơi khác (gắn media, tải lại sau 412) mà người dùng chưa sửa gì → dựng lại form;
  // đang sửa dở → giữ nguyên thay đổi, lần lưu sau so với bản mới nhất.
  const [baseVideo, setBaseVideo] = useState(video);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string; conflict?: boolean } | null>(null);

  useEffect(() => {
    if (video === baseVideo) return;
    setDraft((d) => (videoPatchBody(baseVideo, d) === null ? draftFromVideo(video) : d));
    setBaseVideo(video);
  }, [video, baseVideo]);

  const body = useMemo(() => videoPatchBody(video, draft), [video, draft]);
  const set = <K extends keyof VideoDraft>(key: K, value: VideoDraft[K]) => setDraft((d) => ({ ...d, [key]: value }));
  const setSponsor = (key: keyof VideoDraft['sponsorship'], value: string) => setDraft((d) => ({ ...d, sponsorship: { ...d.sponsorship, [key]: value } }));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!body) return;
    setBusy(true);
    setMessage(null);
    try {
      const response = await apiFetch<AdminVideo>(`/admin/videos/${encodeURIComponent(video.id)}`, { method: 'PATCH', body, ifMatch: etag ?? undefined });
      if (response.data) {
        setDraft(draftFromVideo(response.data));
        setBaseVideo(response.data);
        onSaved(response.data, response.etag);
      }
      setMessage({ kind: 'ok', text: 'Đã lưu bản nháp.' });
    } catch (err) {
      setMessage({ kind: 'error', text: videoErrorText(err), conflict: err instanceof ApiError && err.code === 'REVISION_CONFLICT' });
    } finally {
      setBusy(false);
    }
  };

  /** 412: lấy bản mới nhất; giữ thay đổi của người dùng để áp lại (lưu lần nữa) hoặc bỏ đi. */
  const resolveConflict = async (keepMine: boolean) => {
    const latest = await reload();
    if (!latest?.data) return;
    if (!keepMine) {
      setDraft(draftFromVideo(latest.data));
      setBaseVideo(latest.data);
    }
    setMessage(keepMine ? { kind: 'ok', text: 'Đã tải bản mới nhất. Thay đổi của bạn vẫn còn trong form — kiểm tra rồi lưu lại.' } : null);
  };

  const sponsored = draft.contentKind === 'SPONSORED';
  return (
    <form className="card stack-sm" onSubmit={submit}>
      <h2>Nội dung</h2>
      <fieldset className="plain" disabled={!canEdit || busy}>
        <label className="field">
          <span>Tiêu đề</span>
          <input value={draft.title} onChange={(e) => set('title', e.target.value)} maxLength={300} required />
        </label>
        <label className="field">
          <span>Mô tả ngắn</span>
          <textarea value={draft.summary} onChange={(e) => set('summary', e.target.value)} rows={4} />
        </label>
        <label className="field">
          <span>Loại nội dung</span>
          <select value={draft.contentKind} onChange={(e) => set('contentKind', e.target.value)}>
            <option value="EDITORIAL">Biên tập</option>
            <option value="SPONSORED">Tài trợ</option>
          </select>
        </label>
        {sponsored && (
          <div className="sponsor stack-sm">
            <p className="muted small">Video tài trợ cần đủ thông tin; chỉ hiển thị công khai khi cờ tài trợ được bật và đã duyệt tài trợ.</p>
            <label className="field">
              <span>Nhà tài trợ</span>
              <input value={draft.sponsorship.sponsorName} onChange={(e) => setSponsor('sponsorName', e.target.value)} maxLength={180} required />
            </label>
            <label className="field">
              <span>Mã hợp đồng</span>
              <input value={draft.sponsorship.sponsorReference} onChange={(e) => setSponsor('sponsorReference', e.target.value)} maxLength={120} required />
            </label>
            <label className="field">
              <span>Bằng chứng (đường dẫn hồ sơ)</span>
              <input value={draft.sponsorship.evidenceRef} onChange={(e) => setSponsor('evidenceRef', e.target.value)} maxLength={300} required />
            </label>
            <div className="toolbar">
              <label className="field grow">
                <span>Hiệu lực từ</span>
                <input type="datetime-local" value={draft.sponsorship.validFrom} onChange={(e) => setSponsor('validFrom', e.target.value)} required />
              </label>
              <label className="field grow">
                <span>Đến</span>
                <input type="datetime-local" value={draft.sponsorship.validTo} onChange={(e) => setSponsor('validTo', e.target.value)} required />
              </label>
            </div>
          </div>
        )}
        <label className="field">
          <span>Lý do sửa (tùy chọn, lưu cùng phiên bản)</span>
          <input value={draft.changeReason} onChange={(e) => set('changeReason', e.target.value)} maxLength={500} />
        </label>
      </fieldset>
      {video.publicRevisionId && <p className="muted small">Video đã xuất bản: lưu sẽ tạo bản nháp mới, bản công khai giữ nguyên cho tới lần xuất bản sau.</p>}
      {message && (
        <div className={message.kind === 'ok' ? 'banner ok' : 'banner error'} role={message.kind === 'ok' ? 'status' : 'alert'}>
          <span>{message.text}</span>
          {message.conflict && (
            <span className="row-end">
              <button type="button" className="button small" onClick={() => void resolveConflict(true)}>
                Tải bản mới, giữ thay đổi của tôi
              </button>
              <button type="button" className="button small" onClick={() => void resolveConflict(false)}>
                Tải bản mới, bỏ thay đổi
              </button>
            </span>
          )}
        </div>
      )}
      {canEdit && (
        <div className="row-end">
          <button type="button" className="button" disabled={busy || !body} onClick={() => setDraft(draftFromVideo(video))}>
            Hoàn tác
          </button>
          <button type="submit" className="button primary" disabled={busy || !body || !draft.title.trim()}>
            {busy ? 'Đang lưu…' : 'Lưu'}
          </button>
        </div>
      )}
    </form>
  );
}

function MediaFacts({ media }: { media: MediaStatus }) {
  return (
    <dl className="facts">
      <dt>Tệp</dt>
      <dd className="break">{media.fileName ?? <code>{media.mediaId}</code>}</dd>
      <dt>Loại</dt>
      <dd>
        {mediaTypeLabel(media.mediaType)} <span className="muted">{media.contentType ?? ''}</span>
      </dd>
      <dt>Dung lượng</dt>
      <dd>{formatBytes(media.sizeBytes)}</dd>
      <dt>Trạng thái</dt>
      <dd>
        <MediaStatusBadge status={media.status} />
        {media.errorCode && <span className="muted small"> {mediaErrorLabel(media.errorCode)}</span>}
      </dd>
      <dt>Quyền</dt>
      <dd>
        <RightsBadge status={media.rightsStatus} /> <span className="muted small">{media.visibility === 'PUBLIC' ? 'Công khai' : 'Riêng tư'}</span>
      </dd>
    </dl>
  );
}

function MediaPanel({ video, etag, canEdit, onSaved, reload }: {
  video: AdminVideo;
  etag: string | null;
  canEdit: boolean;
  onSaved: (v: AdminVideo, etag: string | null) => void;
  reload: Reload;
}) {
  const staff = useStaff();
  const { media, error, refresh } = useMediaStatus(video.mediaId);
  const [mode, setMode] = useState<'view' | 'upload' | 'pick'>('view');
  const [busy, setBusy] = useState(false);
  const [attachError, setAttachError] = useState<string | null>(null);
  const canUpload = access(staff, 'media.upload') === 'allowed';
  const locked = video.publicRevisionId !== null;

  const attach = useCallback(async (mediaId: string) => {
    if (mediaId === video.mediaId) return;
    setBusy(true);
    setAttachError(null);
    try {
      const response = await apiFetch<AdminVideo>(`/admin/videos/${encodeURIComponent(video.id)}`, { method: 'PATCH', body: { mediaId }, ifMatch: etag ?? undefined });
      if (response.data) onSaved(response.data, response.etag);
      setMode('view');
    } catch (e) {
      setAttachError(videoErrorText(e));
      if (e instanceof ApiError && e.code === 'REVISION_CONFLICT') await reload();
    } finally {
      setBusy(false);
    }
  }, [etag, onSaved, reload, video.id, video.mediaId]);

  // Tệp mới tải lên: gắn ngay khi đã finalize (đang xử lý hoặc sẵn sàng) — trạng thái xử lý tiếp tục cập nhật ở đây.
  const onUploaded = useCallback((m: MediaStatus) => {
    if (m.status !== 'UPLOADING' && canAttachToVideo(m)) void attach(m.mediaId);
  }, [attach]);

  return (
    <div className="card stack-sm">
      <div className="title-row spread">
        <h2>Media</h2>
        {media && isMediaInFlight(media.status) && <span className="muted small">Tự cập nhật…</span>}
      </div>
      {!video.mediaId && mode === 'view' && <p className="muted">Video chưa có tệp.</p>}
      {media && mode === 'view' && <MediaFacts media={media} />}
      {media?.status === 'READY' && (
        <p className="muted small">Bản dev: “Sẵn sàng” chỉ nghĩa là tệp đúng định dạng; chưa có transcode, ảnh bìa hay bản phát (WBS-036…039).</p>
      )}
      <ErrorBanner text={error}>
        <button type="button" className="button small" onClick={refresh}>
          Thử lại
        </button>
      </ErrorBanner>
      <ErrorBanner text={attachError} />

      {canEdit && locked && <p className="muted small">Đổi media của video đã xuất bản chưa hỗ trợ (WBS-040).</p>}
      {canEdit && !locked && mode === 'upload' && (
        <UploadBox accept={VIDEO_ACCEPT} hint={VIDEO_HINT} onMedia={onUploaded} disabled={busy} />
      )}
      {canEdit && !locked && mode === 'pick' && <MediaPicker currentId={video.mediaId} onPick={(id) => void attach(id)} busy={busy} />}
      {canEdit && !locked && (
        <div className="row-end">
          {mode !== 'view' && (
            <button type="button" className="button small" onClick={() => setMode('view')}>
              Đóng
            </button>
          )}
          {mode === 'view' && canUpload && (
            <button type="button" className="button small" onClick={() => setMode('upload')}>
              {video.mediaId ? 'Thay tệp' : 'Tải tệp lên'}
            </button>
          )}
          {mode === 'view' && (
            <button type="button" className="button small" onClick={() => setMode('pick')}>
              Chọn từ thư viện
            </button>
          )}
        </div>
      )}
    </div>
  );
}

/** Chọn media VIDEO trong thư viện để gắn (ẩn lựa chọn với media hỏng/hết quyền — API vẫn kiểm). */
function MediaPicker({ currentId, onPick, busy }: { currentId: string | null; onPick: (mediaId: string) => void; busy: boolean }) {
  const [q, setQ] = useState('');
  const [query, setQuery] = useState('');
  const list = useCursorList<MediaStatus>((c) => mediaListQuery({ mediaType: 'VIDEO', rightsStatus: '', visibility: '', q: query }, c, 10), [query]);
  const { load } = list;
  useEffect(() => void load(null), [load]);

  return (
    <div className="stack-sm">
      <form
        className="toolbar"
        role="search"
        onSubmit={(e) => {
          e.preventDefault();
          setQuery(q);
        }}
      >
        <label className="field grow">
          <span>Tìm theo tên tệp</span>
          <input value={q} onChange={(e) => setQ(e.target.value)} maxLength={200} />
        </label>
        <button type="submit" className="button small">
          Tìm
        </button>
      </form>
      <ErrorBanner text={list.error} />
      <ul className="picker">
        {list.items.map((m) => {
          const ok = canAttachToVideo(m) && m.mediaId !== currentId;
          return (
            <li key={m.mediaId}>
              <span className="picker-name break">{m.fileName ?? m.mediaId}</span>
              <span className="muted small">{formatBytes(m.sizeBytes)}</span>
              <MediaStatusBadge status={m.status} />
              <button type="button" className="button small" disabled={!ok || busy} onClick={() => onPick(m.mediaId)}>
                {m.mediaId === currentId ? 'Đang dùng' : 'Gắn'}
              </button>
            </li>
          );
        })}
        {!list.loading && list.items.length === 0 && <li className="muted">Không có video nào trong thư viện.</li>}
      </ul>
      {list.cursor && !list.loading && (
        <button type="button" className="button small" onClick={() => void load(list.cursor)}>
          Xem thêm
        </button>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- thư viện media

export function MediaLibraryPage() {
  return (
    <RequirePermission permission="content.read">
      <MediaLibrary />
    </RequirePermission>
  );
}

function MediaLibrary() {
  const staff = useStaff();
  const canUpload = access(staff, 'media.upload') === 'allowed';
  const [filter, setFilter] = useState<MediaListFilter>({ mediaType: '', rightsStatus: '', visibility: '', q: '' });
  const [q, setQ] = useState('');
  const [uploading, setUploading] = useState(false);
  const [uploadKey, setUploadKey] = useState(0);
  const list = useCursorList<MediaStatus>((c) => mediaListQuery(filter, c), [filter]);
  const { load, items, setItems } = list;

  useEffect(() => void load(null), [load]);

  // Media đang xử lý trong danh sách: hỏi lại từng mục theo chu kỳ.
  const inFlight = items.filter((m) => isMediaInFlight(m.status)).map((m) => m.mediaId).join(',');
  useEffect(() => {
    if (!inFlight) return;
    let attempt = 0;
    let timer: ReturnType<typeof setTimeout>;
    let cancelled = false;
    const tick = async () => {
      const ids = inFlight.split(',');
      const fresh = await Promise.all(ids.map((id) => apiFetch<MediaStatus>(`/admin/media/${encodeURIComponent(id)}`).then((r) => r.data).catch(() => null)));
      if (cancelled) return;
      setItems((prev) => prev.map((m) => fresh.find((f) => f?.mediaId === m.mediaId) ?? m));
      timer = setTimeout(() => void tick(), pollDelayMs(++attempt));
    };
    timer = setTimeout(() => void tick(), pollDelayMs(0));
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [inFlight, setItems]);

  const onUploaded = useCallback((m: MediaStatus) => {
    // Có intent mới → đưa lên đầu danh sách; các lần sau cập nhật trạng thái tại chỗ.
    setItems((prev) => (prev.some((x) => x.mediaId === m.mediaId) ? prev.map((x) => (x.mediaId === m.mediaId ? { ...x, ...m } : x)) : [m, ...prev]));
  }, [setItems]);

  return (
    <section className="stack">
      <div className="title-row spread">
        <div>
          <h1>Video</h1>
          <p className="muted">Thư viện media: tệp đã tải lên, trạng thái xử lý và quyền sử dụng. Media mới luôn riêng tư và chưa xác minh quyền.</p>
        </div>
        {canUpload && (
          <button type="button" className="button primary" onClick={() => (uploading ? setUploadKey((k) => k + 1) : setUploading(true))}>
            {uploading ? 'Tải tệp khác' : 'Tải lên'}
          </button>
        )}
      </div>
      <VideoTabs active="media" />

      {uploading && canUpload && (
        <div className="card">
          <UploadBox key={uploadKey} accept={ANY_ACCEPT} hint="Video (MP4, MOV, WebM), ảnh (JPEG, PNG, WebP) hoặc phụ đề WebVTT." onMedia={onUploaded} />
        </div>
      )}

      <form
        className="toolbar"
        role="search"
        onSubmit={(e) => {
          e.preventDefault();
          setFilter((f) => ({ ...f, q }));
        }}
      >
        <label className="field grow">
          <span>Tìm</span>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Tên tệp" maxLength={200} />
        </label>
        <label className="field">
          <span>Loại</span>
          <select value={filter.mediaType} onChange={(e) => setFilter((f) => ({ ...f, mediaType: e.target.value }))}>
            <option value="">Tất cả</option>
            <option value="VIDEO">Video</option>
            <option value="IMAGE">Ảnh</option>
            <option value="SUBTITLE">Phụ đề</option>
            <option value="OTHER">Khác</option>
          </select>
        </label>
        <label className="field">
          <span>Quyền</span>
          <select value={filter.rightsStatus} onChange={(e) => setFilter((f) => ({ ...f, rightsStatus: e.target.value }))}>
            <option value="">Tất cả</option>
            {['UNVERIFIED', 'CLEARED', 'RESTRICTED', 'EXPIRED', 'REVOKED'].map((r) => (
              <option key={r} value={r}>
                {rightsLabel(r)}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Hiển thị</span>
          <select value={filter.visibility} onChange={(e) => setFilter((f) => ({ ...f, visibility: e.target.value }))}>
            <option value="">Tất cả</option>
            <option value="PRIVATE">Riêng tư</option>
            <option value="PUBLIC">Công khai</option>
          </select>
        </label>
        <button type="submit" className="button primary">
          Tìm
        </button>
      </form>

      <ErrorBanner text={list.error} />

      <div className="card flush">
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Tệp</th>
              <th scope="col">Trạng thái</th>
              <th scope="col" className="hide-sm">Loại</th>
              <th scope="col" className="hide-sm">Dung lượng</th>
              <th scope="col" className="hide-sm">Quyền</th>
              <th scope="col" className="hide-sm">Tải lên</th>
            </tr>
          </thead>
          <tbody>
            {items.map((m) => (
              <tr key={m.mediaId}>
                <td className="break">
                  <strong>{m.fileName ?? <code>{m.mediaId}</code>}</strong>
                  {m.createdBy === staff.userId && <span className="muted small"> · của tôi</span>}
                  {m.errorCode && <div className="muted small">{mediaErrorLabel(m.errorCode)}</div>}
                </td>
                <td>
                  <MediaStatusBadge status={m.status} />
                </td>
                <td className="hide-sm">{mediaTypeLabel(m.mediaType)}</td>
                <td className="hide-sm">{formatBytes(m.sizeBytes)}</td>
                <td className="hide-sm">
                  <RightsBadge status={m.rightsStatus} /> <span className="muted small">{m.visibility === 'PUBLIC' ? 'Công khai' : 'Riêng tư'}</span>
                </td>
                <td className="hide-sm muted">{formatTime(m.createdAt)}</td>
              </tr>
            ))}
            {!list.loading && items.length === 0 && (
              <tr>
                <td colSpan={6} className="muted center">
                  Chưa có media nào khớp bộ lọc.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="row-end">
        {list.loading && <span className="muted">Đang tải…</span>}
        {!list.loading && list.cursor && (
          <button type="button" className="button" onClick={() => void load(list.cursor)}>
            Xem thêm
          </button>
        )}
      </div>
    </section>
  );
}

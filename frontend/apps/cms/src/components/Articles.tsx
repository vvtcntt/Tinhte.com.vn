'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { ApiError, apiFetch } from '../lib/api';
import {
  ARTICLE_STATUSES,
  blocksToText,
  canEditArticle,
  diffValue,
  EDITABLE_STATUSES,
  formatTime,
  statusLabel,
  statusTone,
  textToBlocks,
  type AdminArticle,
  type ArticleRevision,
  type Block,
  type Category,
} from '../lib/articles';
import { access } from '../lib/rbac';
import { RequirePermission } from './CmsApp';
import { useStaff } from './session';

// Màn Bài viết (WBS-030 soạn/sửa, phần danh sách của WBS-031) trên API WBS-022 (/admin/articles*, /admin/categories).

function errorText(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.code === 'REVISION_CONFLICT') return 'Bài vừa được người khác lưu. Tải bản mới nhất: bỏ thay đổi của bạn, hoặc giữ nội dung của bạn rồi lưu đè.';
    const fields = e.problem.errors ? Object.entries(e.problem.errors).map(([k, v]) => `${k}: ${v.join(' ')}`).join(' · ') : '';
    return [e.problem.detail ?? e.problem.title, fields].filter(Boolean).join(' — ');
  }
  return 'Không kết nối được API. Thử lại.';
}

// Sao lưu bản chưa lưu trong trình duyệt (mất mạng, đóng tab nhầm — WBS-030 "autosave/offline"). Chỉ là tiện ích theo
// người dùng: trình duyệt chặn lưu trữ thì bỏ qua.
const BACKUP_PREFIX = 'tt-cms:article-draft:';

function readBackup(articleId: string): { draft: Draft; savedAt: string } | null {
  try {
    const raw = window.localStorage.getItem(BACKUP_PREFIX + articleId);
    return raw ? (JSON.parse(raw) as { draft: Draft; savedAt: string }) : null;
  } catch {
    return null;
  }
}

function writeBackup(articleId: string, draft: Draft | null) {
  try {
    if (draft) window.localStorage.setItem(BACKUP_PREFIX + articleId, JSON.stringify({ draft, savedAt: new Date().toISOString() }));
    else window.localStorage.removeItem(BACKUP_PREFIX + articleId);
  } catch {
    // bỏ qua
  }
}

function sameDraft(a: Draft, b: Draft): boolean {
  return (Object.keys(a) as (keyof Draft)[]).every((k) => a[k] === b[k]);
}

function StatusBadge({ status }: { status: string }) {
  const tone = statusTone(status);
  return <span className={tone ? `badge ${tone}` : 'badge'}>{statusLabel(status)}</span>;
}

function useCategories() {
  const [categories, setCategories] = useState<Category[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    apiFetch<{ items: Category[] }>('/admin/categories')
      .then((r) => setCategories(r.data?.items ?? []))
      .catch((e) => setError(errorText(e)));
  }, []);
  return { categories, error };
}

// ---------------------------------------------------------------- danh sách

export function ArticlesPage() {
  return (
    <RequirePermission permission="content.read">
      <ArticlesList />
    </RequirePermission>
  );
}

function ArticlesList() {
  const staff = useStaff();
  const [q, setQ] = useState('');
  const [status, setStatus] = useState('');
  const [mine, setMine] = useState(false);
  const [items, setItems] = useState<AdminArticle[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(
    async (after: string | null) => {
      setLoading(true);
      setError(null);
      const params = new URLSearchParams({ limit: '20', sort: 'updatedAt_desc' });
      if (q.trim()) params.set('q', q.trim());
      if (status) params.set('status', status);
      if (mine) params.set('ownerId', staff.userId);
      if (after) params.set('cursor', after);
      try {
        const { data } = await apiFetch<{ items: AdminArticle[]; nextCursor: string | null }>(`/admin/articles?${params}`);
        setItems((prev) => (after ? [...prev, ...(data?.items ?? [])] : data?.items ?? []));
        setCursor(data?.nextCursor ?? null);
      } catch (e) {
        setError(errorText(e));
      } finally {
        setLoading(false);
      }
    },
    [q, status, mine, staff.userId],
  );

  useEffect(() => void load(null), [status, mine]); // ô tìm kiếm tải khi bấm "Tìm"

  return (
    <section className="stack">
      <div className="title-row spread">
        <div>
          <h1>Bài viết</h1>
          <p className="muted">Bài nháp, đang duyệt và đã xuất bản. Sửa bài đã xuất bản tạo phiên bản mới; bản công khai giữ nguyên tới lần xuất bản sau.</p>
        </div>
        {access(staff, 'content.write_own') === 'allowed' && (
          <Link href="/articles/new" className="button primary">
            Bài mới
          </Link>
        )}
      </div>

      <form className="toolbar" role="search" onSubmit={(e) => { e.preventDefault(); void load(null); }}>
        <label className="field grow">
          <span>Tìm</span>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Tiêu đề" maxLength={200} />
        </label>
        <label className="field">
          <span>Trạng thái</span>
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tất cả</option>
            {ARTICLE_STATUSES.map((s) => (
              <option key={s} value={s}>
                {statusLabel(s)}
              </option>
            ))}
          </select>
        </label>
        <label className="check inline">
          <input type="checkbox" checked={mine} onChange={(e) => setMine(e.target.checked)} />
          <span>Chỉ bài của tôi</span>
        </label>
        <button type="submit" className="button primary">
          Tìm
        </button>
      </form>

      {error && (
        <div className="banner error" role="alert">
          {error}
        </div>
      )}

      <div className="card flush">
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Tiêu đề</th>
              <th scope="col">Trạng thái</th>
              <th scope="col">Loại</th>
              <th scope="col">Cập nhật</th>
            </tr>
          </thead>
          <tbody>
            {items.map((a) => (
              <tr key={a.id}>
                <td>
                  <Link href={`/articles/${a.id}`}>{a.title}</Link>
                  <div className="muted small">/{a.slug}</div>
                </td>
                <td>
                  <StatusBadge status={a.status} />
                  {a.publicRevisionId && a.status !== 'PUBLISHED' && <div className="muted small">Có bản công khai</div>}
                </td>
                <td>{a.contentKind === 'SPONSORED' ? <span className="badge warn">Tài trợ</span> : <span className="muted">Biên tập</span>}</td>
                <td className="muted">{formatTime(a.updatedAt)}</td>
              </tr>
            ))}
            {!loading && items.length === 0 && (
              <tr>
                <td colSpan={4} className="muted center">
                  Chưa có bài viết nào khớp bộ lọc.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="row-end">
        {loading && <span className="muted">Đang tải…</span>}
        {!loading && cursor && (
          <button type="button" className="button" onClick={() => void load(cursor)}>
            Xem thêm
          </button>
        )}
      </div>
    </section>
  );
}

// ---------------------------------------------------------------- tạo bài

export function NewArticlePage() {
  return (
    <RequirePermission permission="content.write_own">
      <NewArticle />
    </RequirePermission>
  );
}

function NewArticle() {
  const router = useRouter();
  const { categories, error: categoryError } = useCategories();
  const [title, setTitle] = useState('');
  const [categoryId, setCategoryId] = useState('');
  const [summary, setSummary] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID());
  const enabled = (categories ?? []).filter((c) => c.isEnabled);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const { data } = await apiFetch<AdminArticle>('/admin/articles', {
        method: 'POST',
        body: { title, categoryId, summary: summary.trim() || undefined, initialBodyBlocks: [] },
        idempotencyKey,
      });
      if (data) router.push(`/articles/${data.id}`);
    } catch (err) {
      setError(errorText(err));
      if (err instanceof ApiError && err.status < 500) setIdempotencyKey(crypto.randomUUID());
      setBusy(false);
    }
  };

  return (
    <section className="stack">
      <Link href="/articles" className="back">
        ← Bài viết
      </Link>
      <h1>Bài mới</h1>
      <form className="card stack-sm narrow-form" onSubmit={submit}>
        <label className="field">
          <span>Tiêu đề</span>
          <input value={title} onChange={(e) => setTitle(e.target.value)} required maxLength={300} autoFocus />
        </label>
        <label className="field">
          <span>Chuyên mục</span>
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} required disabled={!categories}>
            <option value="">{categories ? 'Chọn chuyên mục' : 'Đang tải…'}</option>
            {enabled.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        {categories && enabled.length === 0 && (
          <div className="banner warn" role="status">
            Chưa có chuyên mục nào. Quản lý chuyên mục thuộc WBS-023/032 (chưa làm); trên máy dev hãy thêm chuyên mục bằng SQL (README).
          </div>
        )}
        {categoryError && (
          <div className="banner error" role="alert">
            {categoryError}
          </div>
        )}
        <label className="field">
          <span>Tóm tắt (tùy chọn)</span>
          <textarea value={summary} onChange={(e) => setSummary(e.target.value)} rows={3} />
        </label>
        <p className="muted small">Đường dẫn (slug) tự sinh từ tiêu đề, đổi được sau khi tạo.</p>
        {error && (
          <div className="banner error" role="alert">
            {error}
          </div>
        )}
        <div className="row-end">
          <button type="submit" className="button primary" disabled={busy || !title.trim() || !categoryId}>
            {busy ? 'Đang tạo…' : 'Tạo bản nháp'}
          </button>
        </div>
      </form>
    </section>
  );
}

// ---------------------------------------------------------------- trình soạn

export function ArticleEditorPage({ articleId }: { articleId: string }) {
  return (
    <RequirePermission permission="content.read">
      <ArticleEditor articleId={articleId} />
    </RequirePermission>
  );
}

interface Draft {
  title: string;
  slug: string;
  summary: string;
  categoryId: string;
  body: string;
}

function ArticleEditor({ articleId }: { articleId: string }) {
  const staff = useStaff();
  const { categories } = useCategories();
  const [article, setArticle] = useState<AdminArticle | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [revision, setRevision] = useState<ArticleRevision | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [changeReason, setChangeReason] = useState('');
  const [loadError, setLoadError] = useState<string | null>(null);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string; conflict?: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [preview, setPreview] = useState(false);
  const [historyKey, setHistoryKey] = useState(0);
  const [restorable, setRestorable] = useState<{ draft: Draft; savedAt: string } | null>(null);
  const formRef = useRef<HTMLFormElement>(null);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const a = await apiFetch<AdminArticle>(`/admin/articles/${encodeURIComponent(articleId)}`);
      const art = a.data!;
      const r = art.workingRevisionId
        ? (await apiFetch<ArticleRevision>(`/admin/articles/${encodeURIComponent(articleId)}/revisions/${art.workingRevisionId}`)).data
        : null;
      setArticle(art);
      setEtag(a.etag);
      setRevision(r);
      const body = blocksToText((r?.bodyBlocks ?? []) as Block[]);
      const loaded: Draft = { title: r?.title ?? art.title, slug: art.slug, summary: r?.summary ?? '', categoryId: r?.categoryId ?? '', body: body.text };
      setDraft(loaded);
      const backup = readBackup(articleId);
      setRestorable(backup && !sameDraft(backup.draft, loaded) ? backup : null);
      setChangeReason('');
      setHistoryKey((k) => k + 1);
    } catch (e) {
      setLoadError(e instanceof ApiError && e.status === 404 ? 'Không tìm thấy bài viết này.' : errorText(e));
    }
  }, [articleId]);

  useEffect(() => void load(), [load]);

  const bodySupported = useMemo(() => blocksToText((revision?.bodyBlocks ?? []) as Block[]).supported, [revision]);
  const original = useMemo<Draft | null>(() => {
    if (!article) return null;
    return {
      title: revision?.title ?? article.title,
      slug: article.slug,
      summary: revision?.summary ?? '',
      categoryId: revision?.categoryId ?? '',
      body: blocksToText((revision?.bodyBlocks ?? []) as Block[]).text,
    };
  }, [article, revision]);
  const dirty = !!draft && !!original && !sameDraft(draft, original);

  // Sao lưu cục bộ khi có thay đổi chưa lưu; hết thay đổi (đã lưu / hoàn tác) thì xóa.
  useEffect(() => {
    if (!draft || !original) return;
    const timer = window.setTimeout(() => writeBackup(articleId, dirty ? draft : null), 800);
    return () => window.clearTimeout(timer);
  }, [articleId, draft, original, dirty]);

  // Ctrl/Cmd+S = Lưu.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
        e.preventDefault();
        formRef.current?.requestSubmit();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Cảnh báo khi rời trang mà chưa lưu.
  useEffect(() => {
    if (!dirty) return;
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  if (loadError) {
    return (
      <section className="stack">
        <Link href="/articles" className="back">
          ← Bài viết
        </Link>
        <div className="banner error" role="alert">
          {loadError}
        </div>
      </section>
    );
  }

  if (!article || !draft || !original) return <p className="muted">Đang tải…</p>;

  const can = (p: 'content.write_own' | 'content.write_any') => access(staff, p) === 'allowed';
  const permitted = canEditArticle(can, staff.userId, article);
  const editableState = EDITABLE_STATUSES.has(article.status);
  const editable = permitted && editableState && bodySupported;
  const set = (patch: Partial<Draft>) => setDraft((d) => (d ? { ...d, ...patch } : d));

  const save = async (e: FormEvent) => {
    e.preventDefault();
    const body: Record<string, unknown> = {};
    if (draft.title !== original.title) body.title = draft.title;
    if (draft.slug !== original.slug) body.slug = draft.slug;
    if (draft.summary !== original.summary) body.summary = draft.summary.trim() ? draft.summary : null;
    if (draft.categoryId !== original.categoryId && draft.categoryId) body.categoryId = draft.categoryId;
    if (draft.body !== original.body) body.bodyBlocks = textToBlocks(draft.body);
    if (changeReason.trim()) body.changeReason = changeReason.trim();
    if (Object.keys(body).length === 0) return;
    setBusy(true);
    setMessage(null);
    try {
      await apiFetch<AdminArticle>(`/admin/articles/${encodeURIComponent(article.id)}`, { method: 'PATCH', body, ifMatch: etag ?? undefined });
      writeBackup(article.id, null);
      await load();
      setMessage({ kind: 'ok', text: article.publicRevisionId ? 'Đã lưu. Bản công khai giữ nguyên tới lần xuất bản sau.' : 'Đã lưu bản nháp.' });
    } catch (err) {
      setMessage({ kind: 'error', text: errorText(err), conflict: err instanceof ApiError && err.code === 'REVISION_CONFLICT' });
    } finally {
      setBusy(false);
    }
  };

  // Xung đột 412: tải bản mới nhất nhưng giữ nội dung đang soạn (lưu lần nữa sẽ ghi đè các trường bạn đã đổi).
  const keepMine = async () => {
    const mine = draft;
    const before = original;
    await load();
    setRestorable(null);
    setDraft((fresh) => (fresh ? { ...mine, slug: mine.slug === before.slug ? fresh.slug : mine.slug } : fresh));
    setMessage({ kind: 'ok', text: 'Đã tải bản mới nhất và giữ nội dung của bạn. Xem lại rồi bấm Lưu để ghi đè.' });
  };

  return (
    <section className="stack">
      <Link href="/articles" className="back">
        ← Bài viết
      </Link>
      {restorable && (
        <div className="banner warn" role="status">
          <span>Có nội dung chưa lưu từ {formatTime(restorable.savedAt)} trên máy này.</span>
          <span className="row-end">
            <button type="button" className="button small" disabled={!editable} onClick={() => { setDraft(restorable.draft); setRestorable(null); }}>
              Khôi phục
            </button>
            <button type="button" className="button small" onClick={() => { writeBackup(article.id, null); setRestorable(null); }}>
              Bỏ
            </button>
          </span>
        </div>
      )}
      <div className="title-row">
        <h1>{original.title}</h1>
        <StatusBadge status={article.status} />
        {article.contentKind === 'SPONSORED' && <span className="badge warn">Tài trợ</span>}
      </div>

      {!permitted && (
        <div className="banner warn" role="status">
          Bạn chỉ xem được bài này (sửa bài của người khác cần vai trò Biên tập viên).
        </div>
      )}
      {permitted && !editableState && (
        <div className="banner warn" role="status">
          Bài đang ở trạng thái “{statusLabel(article.status)}”: không sửa trực tiếp được. Cần bước duyệt (yêu cầu sửa / từ chối) — thuộc WBS-024, chưa làm.
        </div>
      )}
      {!bodySupported && (
        <div className="banner warn" role="status">
          Nội dung có loại khối mà trình soạn tạm chưa hỗ trợ (ảnh, nhúng…). Chỉ xem được; trình soạn đầy đủ làm theo thiết kế UX-CMS-02.
        </div>
      )}

      <div className="editor-layout">
        <form ref={formRef} className="card stack-sm" onSubmit={save}>
          <label className="field">
            <span>Tiêu đề</span>
            <input value={draft.title} onChange={(e) => set({ title: e.target.value })} maxLength={300} required disabled={!editable || busy} />
          </label>
          <div className="grid-2">
            <label className="field">
              <span>Đường dẫn (slug)</span>
              <input value={draft.slug} onChange={(e) => set({ slug: e.target.value })} disabled={!editable || busy} />
            </label>
            <label className="field">
              <span>Chuyên mục</span>
              <select value={draft.categoryId} onChange={(e) => set({ categoryId: e.target.value })} disabled={!editable || busy || !categories}>
                <option value="">—</option>
                {(categories ?? []).map((c) => (
                  <option key={c.id} value={c.id} disabled={!c.isEnabled}>
                    {c.name}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <label className="field">
            <span>Tóm tắt</span>
            <textarea value={draft.summary} onChange={(e) => set({ summary: e.target.value })} rows={2} disabled={!editable || busy} />
          </label>
          <div className="field">
            <span className="tabs">
              <button type="button" className={preview ? 'tab' : 'tab active'} onClick={() => setPreview(false)}>
                Soạn
              </button>
              <button type="button" className={preview ? 'tab active' : 'tab'} onClick={() => setPreview(true)}>
                Xem trước
              </button>
            </span>
            {preview ? (
              <BlocksPreview blocks={textToBlocks(draft.body)} />
            ) : (
              <textarea
                className="body-editor"
                value={draft.body}
                onChange={(e) => set({ body: e.target.value })}
                rows={16}
                disabled={!editable || busy}
                placeholder={'Viết nội dung. Cách đoạn bằng một dòng trống.\n## Tiêu đề mục\n> Trích dẫn'}
              />
            )}
          </div>
          <label className="field">
            <span>Ghi chú thay đổi (tùy chọn, lưu vào lịch sử)</span>
            <input value={changeReason} onChange={(e) => setChangeReason(e.target.value)} maxLength={500} disabled={!editable || busy} />
          </label>
          {message && (
            <div className={message.kind === 'ok' ? 'banner ok' : 'banner error'} role={message.kind === 'ok' ? 'status' : 'alert'}>
              <span>{message.text}</span>
              {message.conflict && (
                <span className="row-end">
                  <button type="button" className="button small" onClick={() => void load().then(() => { writeBackup(article.id, null); setRestorable(null); setMessage(null); })}>
                    Tải lại (bỏ thay đổi của tôi)
                  </button>
                  <button type="button" className="button small" onClick={() => void keepMine()}>
                    Giữ nội dung của tôi
                  </button>
                </span>
              )}
            </div>
          )}
          {editable && (
            <div className="row-end">
              {dirty && <span className="muted small">Có thay đổi chưa lưu</span>}
              <button type="submit" className="button primary" disabled={busy || (!dirty && !changeReason.trim())}>
                {busy ? 'Đang lưu…' : 'Lưu'}
              </button>
            </div>
          )}
        </form>

        <aside className="stack-sm">
          <div className="card stack-sm">
            <h2>Thông tin</h2>
            <dl className="facts">
              <dt>Phiên bản đang sửa</dt>
              <dd>{revision ? `#${revision.number} · ${statusLabel(revision.status)}` : '—'}</dd>
              <dt>Bản công khai</dt>
              <dd>{article.publicRevisionId ? 'Có' : 'Chưa'}</dd>
              <dt>Quyền nội dung</dt>
              <dd>{article.rightsStatus}</dd>
              <dt>Cập nhật</dt>
              <dd>{formatTime(article.updatedAt)}</dd>
            </dl>
          </div>
          <PublishPanel article={article} etag={etag} dirty={dirty} onPublished={load} />
          <RevisionHistory key={historyKey} articleId={article.id} />
        </aside>
      </div>
    </section>
  );
}

function BlocksPreview({ blocks }: { blocks: Block[] }) {
  if (blocks.length === 0) return <div className="preview muted">(Chưa có nội dung)</div>;
  return (
    <div className="preview">
      {blocks.map((b, i) =>
        b.type === 'heading' ? (
          b.level === 3 ? <h4 key={i}>{b.text}</h4> : <h3 key={i}>{b.text}</h3>
        ) : b.type === 'quote' ? (
          <blockquote key={i}>{b.text}</blockquote>
        ) : (
          <p key={i}>{b.text}</p>
        ),
      )}
    </div>
  );
}

function PublishPanel({ article, etag, dirty, onPublished }: { article: AdminArticle; etag: string | null; dirty: boolean; onPublished: () => Promise<void> }) {
  const staff = useStaff();
  const decision = access(staff, 'content.publish');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID());
  if (decision === 'denied') return null;

  const publish = async () => {
    if (!article.workingRevisionId) return;
    if (!window.confirm('Xuất bản phiên bản đang sửa? Bản này sẽ hiển thị công khai trên trang tin.')) return;
    setBusy(true);
    setMessage(null);
    try {
      await apiFetch<AdminArticle>(`/admin/articles/${encodeURIComponent(article.id)}/publish`, {
        method: 'POST',
        body: { revisionId: article.workingRevisionId },
        ifMatch: etag ?? undefined,
        idempotencyKey,
      });
      setIdempotencyKey(crypto.randomUUID());
      await onPublished();
      setMessage({ kind: 'ok', text: 'Đã xuất bản.' });
    } catch (err) {
      if (err instanceof ApiError && err.status < 500) setIdempotencyKey(crypto.randomUUID());
      setMessage({ kind: 'error', text: errorText(err) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card stack-sm">
      <h2>Xuất bản</h2>
      <p className="muted small">
        Chỉ xuất bản được phiên bản đã được duyệt và bài đã xác minh quyền nội dung. Bước duyệt (WBS-024) và quyền nguồn (WBS-023) chưa làm, nên hiện API sẽ
        từ chối và nêu lý do.
      </p>
      {decision === 'mfa-required' && <p className="muted small">Cần đăng nhập lại với MFA để xuất bản.</p>}
      {message && (
        <div className={message.kind === 'ok' ? 'banner ok' : 'banner error'} role={message.kind === 'ok' ? 'status' : 'alert'}>
          {message.text}
        </div>
      )}
      <button
        type="button"
        className="button primary"
        onClick={() => void publish()}
        disabled={busy || decision !== 'allowed' || dirty || !article.workingRevisionId}
        title={dirty ? 'Lưu thay đổi trước khi xuất bản' : undefined}
      >
        {busy ? 'Đang xuất bản…' : 'Xuất bản phiên bản đang sửa'}
      </button>
    </div>
  );
}

function RevisionHistory({ articleId }: { articleId: string }) {
  const [items, setItems] = useState<ArticleRevision[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<ArticleRevision | null>(null);

  useEffect(() => {
    apiFetch<{ items: ArticleRevision[] }>(`/admin/articles/${encodeURIComponent(articleId)}/revisions`)
      .then((r) => setItems(r.data?.items ?? []))
      .catch((e) => setError(errorText(e)));
  }, [articleId]);

  const open = async (rev: ArticleRevision, index: number) => {
    const previous = items?.[index + 1];
    try {
      const url = `/admin/articles/${encodeURIComponent(articleId)}/revisions/${rev.revisionId}${previous ? `?compareTo=${previous.revisionId}` : ''}`;
      setSelected((await apiFetch<ArticleRevision>(url)).data);
    } catch (e) {
      setError(errorText(e));
    }
  };

  return (
    <div className="card stack-sm">
      <h2>Lịch sử phiên bản</h2>
      {error && (
        <div className="banner error" role="alert">
          {error}
        </div>
      )}
      {!items && !error && <p className="muted small">Đang tải…</p>}
      <ol className="revisions">
        {(items ?? []).map((r, i) => (
          <li key={r.revisionId}>
            <button type="button" className={selected?.revisionId === r.revisionId ? 'rev active' : 'rev'} onClick={() => void open(r, i)}>
              <span>
                #{r.number} · {statusLabel(r.status)}
              </span>
              <span className="muted small">{formatTime(r.updatedAt)}</span>
              {r.changeReason && <span className="small">{r.changeReason}</span>}
            </button>
          </li>
        ))}
      </ol>
      {selected && (
        <div className="diff">
          <strong className="small">
            #{selected.number} {selected.diff ? 'so với phiên bản trước' : '(phiên bản đầu tiên)'}
          </strong>
          {selected.diff && selected.diff.changes.length === 0 && <p className="muted small">Không có thay đổi nội dung.</p>}
          {selected.diff?.changes.map((c) => (
            <div key={c.path} className="diff-row">
              <code>{c.path}</code>
              <del>{diffValue(c.before)}</del>
              <ins>{diffValue(c.after)}</ins>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

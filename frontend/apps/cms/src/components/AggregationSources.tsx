'use client';

import Link from 'next/link';
import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from '../lib/api';
import {
  FEED_FAILURE_LABEL,
  FEED_FORMATS,
  FEED_HEALTH_LABEL,
  SOURCE_STATUS_LABEL,
  USAGE_MODE_LABEL,
  ago,
  approvalError,
  dateTimeVi,
  feedUrlError,
  isUuid,
  keywordsText,
  label,
  parseKeywords,
  reasonError,
  type AggregationFeed,
  type CategoryMapping,
  type CategoryRule,
  type FeedFormat,
  type FeedTestResult,
  type Page,
  type SourceDetail,
  type SourceStatus,
  type SourceSummary,
  type UsageMode,
} from '../lib/aggregation';
import { access, permissionLabel } from '../lib/rbac';
import { ContentSourcePicker, LicensePicker } from './Catalog';
import { RequirePermission } from './CmsApp';
import { Banner, SiteSelect, aggErrorText, contentSites, isConflict, newIdempotencyKey, useSiteCategories } from './aggregationCommon';
import { useStaff } from './session';

// Quản lý nguồn thu thập + luật chuyên mục (WBS-144, FR-AGG-001/004, ADR-0008 §2, §7). Xem: aggregation.read; sửa nguồn/feed/luật,
// thử feed: aggregation.sources_manage; duyệt chế độ khai thác: aggregation.sources_approve (MFA). API kiểm lại mọi quyền.

function SourceStatusBadge({ source }: { source: Pick<SourceSummary, 'status' | 'statusChangedBySystem' | 'statusReason'> }) {
  const cls = source.status === 'ENABLED' ? 'badge ok' : source.status === 'PROPOSED' || source.status === 'RETIRED' ? 'badge' : 'badge warn';
  return (
    <span className={cls} title={source.statusReason ?? undefined}>
      {label(SOURCE_STATUS_LABEL, source.status)}
      {source.statusChangedBySystem && (source.status === 'PAUSED' || source.status === 'DEGRADED') ? ' (tự động)' : ''}
    </span>
  );
}

function HealthBadge({ health }: { health: string | null }) {
  if (!health) return <span className="muted">—</span>;
  const cls = health === 'OK' ? 'badge ok' : health === 'NEVER_FETCHED' || health === 'DISABLED' ? 'badge' : 'badge warn';
  return <span className={cls}>{label(FEED_HEALTH_LABEL, health)}</span>;
}

function Tabs({ active }: { active: 'sources' | 'rules' }) {
  return (
    <nav className="tabs-bar" aria-label="Thu thập tin">
      <Link href="/aggregation" className={active === 'sources' ? 'tab active' : 'tab'} aria-current={active === 'sources' ? 'page' : undefined}>Nguồn</Link>
      <Link href="/aggregation/rules" className={active === 'rules' ? 'tab active' : 'tab'} aria-current={active === 'rules' ? 'page' : undefined}>Luật chuyên mục</Link>
      <Link href="/radar" className="tab">Radar tin →</Link>
    </nav>
  );
}

// ---------------------------------------------------------------- danh sách nguồn

export function SourcesPage() {
  return (
    <RequirePermission permission="aggregation.read">
      <SourcesList />
    </RequirePermission>
  );
}

function SourcesList() {
  const staff = useStaff();
  const canManage = access(staff, 'aggregation.sources_manage') === 'allowed';
  const [q, setQ] = useState('');
  const [status, setStatus] = useState('');
  const [items, setItems] = useState<SourceSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (after: string | null) => {
    setLoading(true);
    setError(null);
    const params = new URLSearchParams({ limit: '50' });
    if (q.trim()) params.set('q', q.trim());
    if (status) params.set('status', status);
    if (after) params.set('cursor', after);
    try {
      const { data } = await apiFetch<Page<SourceSummary>>(`/admin/aggregation/sources?${params}`);
      setItems((prev) => (after ? [...prev, ...(data?.items ?? [])] : data?.items ?? []));
      setCursor(data?.nextCursor ?? null);
    } catch (e) {
      setError(aggErrorText(e));
    } finally {
      setLoading(false);
    }
  }, [q, status]);

  useEffect(() => void load(null), [status]);   // ô tìm: tải khi bấm "Tìm"

  const autoChanged = items.filter((s) => s.statusChangedBySystem && (s.status === 'PAUSED' || s.status === 'DEGRADED'));

  return (
    <section className="stack">
      <div>
        <h1>Nguồn thu thập</h1>
        <p className="muted">
          Hồ sơ thu thập gắn với danh mục nguồn (ADR-0007). Nguồn chỉ được thu thập khi đã duyệt chế độ khai thác; thêm feed hoặc đổi URL feed ⇒ phải duyệt lại.
        </p>
      </div>
      <Tabs active="sources" />

      {autoChanged.length > 0 && (
        <div className="banner warn stacked" role="status">
          <strong>{autoChanged.length} nguồn bị hệ thống tự đổi trạng thái (BR-06)</strong>
          {autoChanged.map((s) => (
            <span key={s.id}>
              <Link href={`/aggregation/sources/${s.id}`}>{s.name}</Link> — {label(SOURCE_STATUS_LABEL, s.status)} {ago(s.statusChangedAt)}: {s.statusReason}
            </span>
          ))}
        </div>
      )}

      <form className="toolbar" role="search" onSubmit={(e) => { e.preventDefault(); void load(null); }}>
        <label className="field grow">
          <span>Tìm theo tên</span>
          <input value={q} onChange={(e) => setQ(e.target.value)} maxLength={200} />
        </label>
        <label className="field">
          <span>Trạng thái</span>
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tất cả</option>
            {(Object.keys(SOURCE_STATUS_LABEL) as SourceStatus[]).map((s) => <option key={s} value={s}>{SOURCE_STATUS_LABEL[s]}</option>)}
          </select>
        </label>
        <button type="submit" className="button primary">Tìm</button>
      </form>

      {error && <Banner kind="error" onRetry={() => void load(null)}>{error}</Banner>}

      <div className="card flush">
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Nguồn</th>
              <th scope="col">Trạng thái</th>
              <th scope="col">Chế độ</th>
              <th scope="col">Feed</th>
              <th scope="col">Sức khỏe</th>
              <th scope="col">Mục 24 giờ</th>
            </tr>
          </thead>
          <tbody>
            {items.map((s) => (
              <tr key={s.id}>
                <td><Link href={`/aggregation/sources/${s.id}`}>{s.name}</Link> <span className="muted small">uy tín {s.trustWeight}/5 · {s.language}</span></td>
                <td><SourceStatusBadge source={s} /></td>
                <td>{s.usageMode}</td>
                <td>{s.feedCount}</td>
                <td><HealthBadge health={s.worstFeedHealth} /></td>
                <td>{s.itemsLast24h}</td>
              </tr>
            ))}
            {!loading && items.length === 0 && (
              <tr><td colSpan={6} className="muted center">Chưa có nguồn nào khớp bộ lọc.</td></tr>
            )}
          </tbody>
        </table>
      </div>
      <div className="row-end">
        {loading && <span className="muted">Đang tải…</span>}
        {!loading && cursor && <button type="button" className="button" onClick={() => void load(cursor)}>Xem thêm</button>}
      </div>

      {canManage && <CreateSource onCreated={() => void load(null)} />}
    </section>
  );
}

function CreateSource({ onCreated }: { onCreated: () => void }) {
  const sites = contentSites();
  const [contentSourceId, setContentSourceId] = useState('');
  const [language, setLanguage] = useState('vi');
  const [trust, setTrust] = useState(3);
  const [siteKeys, setSiteKeys] = useState<string[]>(sites.slice(0, 1).map((s) => s.key));
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [key, setKey] = useState(() => newIdempotencyKey('agg-source'));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!isUuid(contentSourceId)) return setMessage({ kind: 'error', text: 'Chọn nguồn trong danh mục.' });
    if (!/^[a-z]{2}$/.test(language)) return setMessage({ kind: 'error', text: 'Ngôn ngữ là mã ISO 639-1 hai chữ thường (vi, en…).' });
    if (siteKeys.length === 0) return setMessage({ kind: 'error', text: 'Chọn ít nhất một site.' });
    setBusy(true);
    setMessage(null);
    try {
      await apiFetch('/admin/aggregation/sources', {
        method: 'POST',
        body: { contentSourceId: contentSourceId.trim(), language, trustWeight: trust, siteKeys },
        idempotencyKey: key,
      });
      setMessage({ kind: 'ok', text: 'Đã tạo hồ sơ (chờ duyệt, chế độ M0). Thêm feed rồi gửi người duyệt.' });
      setContentSourceId('');
      setKey(newIdempotencyKey('agg-source'));
      onCreated();
    } catch (err) {
      setMessage({ kind: 'error', text: aggErrorText(err) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="card stack-sm" onSubmit={submit}>
      <h2>Thêm hồ sơ thu thập</h2>
      <p className="muted small">Chọn nguồn có sẵn trong danh mục "Nguồn & giấy phép" (thêm nguồn mới ở đó nếu chưa có).</p>
      <ContentSourcePicker value={contentSourceId} onChange={setContentSourceId} />
      <div className="grid-2">
        <label className="field"><span>Ngôn ngữ</span><input value={language} onChange={(e) => setLanguage(e.target.value)} maxLength={2} /></label>
        <label className="field"><span>Uy tín (1–5)</span><input type="number" min={1} max={5} value={trust} onChange={(e) => setTrust(Number(e.target.value))} /></label>
        <fieldset className="checks">
          <legend>Site áp dụng</legend>
          {sites.map((s) => (
            <label key={s.key} className="check inline">
              <input type="checkbox" checked={siteKeys.includes(s.key)} onChange={() => setSiteKeys((cur) => (cur.includes(s.key) ? cur.filter((k) => k !== s.key) : [...cur, s.key]))} />
              <span>{s.label}</span>
            </label>
          ))}
        </fieldset>
      </div>
      {message && <Banner kind={message.kind}>{message.text}</Banner>}
      <div className="row-end"><button type="submit" className="button primary" disabled={busy}>{busy ? 'Đang tạo…' : 'Tạo hồ sơ'}</button></div>
    </form>
  );
}

// ---------------------------------------------------------------- chi tiết nguồn

export function SourceDetailPage({ sourceId }: { sourceId: string }) {
  return (
    <RequirePermission permission="aggregation.read">
      <SourceView sourceId={sourceId} />
    </RequirePermission>
  );
}

function SourceView({ sourceId }: { sourceId: string }) {
  const staff = useStaff();
  const canManage = access(staff, 'aggregation.sources_manage');
  const canApprove = access(staff, 'aggregation.sources_approve');
  const [source, setSource] = useState<SourceDetail | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoadError(null);
    try {
      const r = await apiFetch<SourceDetail>(`/admin/aggregation/sources/${encodeURIComponent(sourceId)}`);
      setSource(r.data);
      setEtag(r.etag);
    } catch (e) {
      setLoadError(aggErrorText(e));
    }
  }, [sourceId]);

  useEffect(() => void reload(), [reload]);

  if (loadError) {
    return (
      <section className="stack">
        <Link href="/aggregation" className="back">← Nguồn thu thập</Link>
        <Banner kind="error">{loadError}</Banner>
      </section>
    );
  }

  if (!source) return <p className="muted">Đang tải…</p>;
  const applied = (s: SourceDetail | null, newEtag: string | null) => {
    if (s) setSource(s);
    setEtag(newEtag);
  };
  const retired = source.status === 'RETIRED';

  return (
    <section className="stack">
      <Link href="/aggregation" className="back">← Nguồn thu thập</Link>
      <div className="title-row">
        <h1>{source.name}</h1>
        <SourceStatusBadge source={source} />
      </div>

      {source.statusChangedBySystem && (source.status === 'PAUSED' || source.status === 'DEGRADED') && (
        <Banner kind="warn">
          Hệ thống tự chuyển nguồn sang {label(SOURCE_STATUS_LABEL, source.status)} {ago(source.statusChangedAt)}: {source.statusReason}
          {source.status === 'PAUSED' && ' Kiểm tra feed bên dưới rồi bấm "Tiếp tục".'}
        </Banner>
      )}
      {source.status === 'PROPOSED' && (
        <Banner kind="warn">Nguồn chưa được duyệt (hoặc vừa thêm/đổi feed): chưa thu thập cho tới khi người duyệt duyệt chế độ khai thác.</Banner>
      )}

      <div className="card">
        <dl className="facts">
          <dt>Chế độ khai thác</dt><dd>{label(USAGE_MODE_LABEL, source.usageMode)}</dd>
          <dt>Giấy phép</dt><dd>{source.licenseId ? <code>{source.licenseId}</code> : '—'}</dd>
          <dt>Bằng chứng điều khoản</dt><dd>{source.termsEvidenceRef ?? '—'}</dd>
          <dt>Duyệt</dt><dd>{source.approvedAt ? `${dateTimeVi(source.approvedAt)} bởi ${source.approvedBy?.slice(0, 8)}` : 'Chưa duyệt'}</dd>
          <dt>Site</dt><dd>{source.siteKeys.join(', ')}</dd>
          <dt>Mục tin 24 giờ</dt><dd>{source.itemsLast24h}</dd>
          <dt>Đổi trạng thái gần nhất</dt><dd>{source.statusChangedAt ? `${dateTimeVi(source.statusChangedAt)}${source.statusChangedBySystem ? ' (hệ thống)' : ''}${source.statusReason ? ` — ${source.statusReason}` : ''}` : '—'}</dd>
        </dl>
      </div>

      {!retired && canManage === 'allowed' && (
        <div className="grid">
          <ProfileEditor source={source} etag={etag} onSaved={applied} onConflict={reload} />
          <StatusActions source={source} etag={etag} onSaved={applied} onConflict={reload} />
        </div>
      )}
      {!retired && canApprove !== 'denied' && (
        canApprove === 'allowed'
          ? <ApprovalForm source={source} etag={etag} onSaved={applied} onConflict={reload} />
          : <Banner kind="warn">Duyệt chế độ khai thác cần quyền "{permissionLabel('aggregation.sources_approve')}" với phiên đã qua MFA.</Banner>
      )}

      <Feeds source={source} canManage={!retired && canManage === 'allowed'} onChanged={reload} />

      {source.approvals.length > 0 && (
        <div className="card stack-sm">
          <h2>Lịch sử duyệt</h2>
          <ul className="history">
            {source.approvals.map((a) => (
              <li key={`${a.approvedAt}-${a.usageMode}`}>
                <strong>{a.usageMode}</strong> · {dateTimeVi(a.approvedAt)} · <code>{a.approvedBy.slice(0, 8)}</code> · {a.termsEvidenceRef}
                {a.licenseId && <> · giấy phép <code>{a.licenseId.slice(0, 8)}</code></>}
                {a.note && <> — {a.note}</>}
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}

interface EditorProps {
  source: SourceDetail;
  etag: string | null;
  onSaved: (s: SourceDetail | null, etag: string | null) => void;
  onConflict: () => Promise<void>;
}

function useAction() {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string; conflict?: boolean } | null>(null);
  const run = async (fn: () => Promise<string>) => {
    setBusy(true);
    setMessage(null);
    try {
      setMessage({ kind: 'ok', text: await fn() });
    } catch (e) {
      setMessage({ kind: 'error', text: aggErrorText(e), conflict: isConflict(e) });
    } finally {
      setBusy(false);
    }
  };
  return { busy, message, setMessage, run };
}

function ActionMessage({ message, onConflict, clear }: { message: { kind: 'ok' | 'error'; text: string; conflict?: boolean } | null; onConflict: () => Promise<void>; clear: () => void }) {
  if (!message) return null;
  return (
    <div className={message.kind === 'ok' ? 'banner ok' : 'banner error'} role={message.kind === 'ok' ? 'status' : 'alert'}>
      <span>{message.text}</span>
      {message.conflict && <button type="button" className="button small" onClick={() => void onConflict().then(clear)}>Tải lại</button>}
    </div>
  );
}

function ProfileEditor({ source, etag, onSaved, onConflict }: EditorProps) {
  const sites = contentSites();
  const [trust, setTrust] = useState(source.trustWeight);
  const [language, setLanguage] = useState(source.language);
  const [siteKeys, setSiteKeys] = useState(source.siteKeys);
  const { busy, message, setMessage, run } = useAction();
  useEffect(() => {
    setTrust(source.trustWeight);
    setLanguage(source.language);
    setSiteKeys(source.siteKeys);
  }, [source]);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    void run(async () => {
      const r = await apiFetch<SourceDetail>(`/admin/aggregation/sources/${source.id}`, {
        method: 'PATCH', body: { trustWeight: trust, language, siteKeys }, ifMatch: etag ?? undefined,
      });
      onSaved(r.data, r.etag);
      return 'Đã lưu hồ sơ.';
    });
  };

  return (
    <form className="card stack-sm" onSubmit={submit}>
      <h2>Hồ sơ</h2>
      <label className="field"><span>Uy tín (1–5, Ban biên tập đặt)</span><input type="number" min={1} max={5} value={trust} onChange={(e) => setTrust(Number(e.target.value))} /></label>
      <label className="field"><span>Ngôn ngữ</span><input value={language} maxLength={2} onChange={(e) => setLanguage(e.target.value)} /></label>
      <fieldset className="checks">
        <legend>Site</legend>
        {sites.map((s) => (
          <label key={s.key} className="check inline">
            <input type="checkbox" checked={siteKeys.includes(s.key)} onChange={() => setSiteKeys((cur) => (cur.includes(s.key) ? cur.filter((k) => k !== s.key) : [...cur, s.key]))} />
            <span>{s.label}</span>
          </label>
        ))}
      </fieldset>
      <ActionMessage message={message} onConflict={onConflict} clear={() => setMessage(null)} />
      <div className="row-end"><button type="submit" className="button primary" disabled={busy || siteKeys.length === 0}>Lưu</button></div>
    </form>
  );
}

function StatusActions({ source, etag, onSaved, onConflict }: EditorProps) {
  const [reason, setReason] = useState('');
  const { busy, message, setMessage, run } = useAction();
  const post = (action: 'pause' | 'resume' | 'retire', ok: string, withReason: boolean) => {
    if (withReason) {
      const err = reasonError(reason);
      if (err) return setMessage({ kind: 'error', text: err });
    }
    if (action === 'retire' && !window.confirm('Ngừng nguồn vĩnh viễn? Nguồn RETIRED không bật lại được (mục tin đã thu thập được giữ).')) return;
    void run(async () => {
      const r = await apiFetch<SourceDetail>(`/admin/aggregation/sources/${source.id}/${action}`, {
        method: 'POST', body: withReason ? { reason: reason.trim() } : undefined, ifMatch: etag ?? undefined,
      });
      onSaved(r.data, r.etag);
      setReason('');
      return ok;
    });
  };
  const canPause = source.status === 'PROPOSED' || source.status === 'ENABLED' || source.status === 'DEGRADED';
  return (
    <div className="card stack-sm">
      <h2>Trạng thái</h2>
      <label className="field"><span>Lý do (bắt buộc khi tạm dừng/ngừng, ghi lịch sử)</span><textarea rows={2} maxLength={2000} value={reason} onChange={(e) => setReason(e.target.value)} /></label>
      <ActionMessage message={message} onConflict={onConflict} clear={() => setMessage(null)} />
      <div className="row-end">
        {canPause && <button type="button" className="button" disabled={busy} onClick={() => post('pause', 'Đã tạm dừng nguồn.', true)}>Tạm dừng</button>}
        {source.status === 'PAUSED' && (
          <button type="button" className="button primary" disabled={busy || !source.approvedAt} title={source.approvedAt ? undefined : 'Cần duyệt lại trước khi tiếp tục'}
            onClick={() => post('resume', 'Nguồn tiếp tục thu thập.', false)}>Tiếp tục</button>
        )}
        <button type="button" className="button danger" disabled={busy} onClick={() => post('retire', 'Đã ngừng nguồn.', true)}>Ngừng vĩnh viễn</button>
      </div>
    </div>
  );
}

function ApprovalForm({ source, etag, onSaved, onConflict }: EditorProps) {
  const [mode, setMode] = useState<UsageMode>(source.usageMode);
  const [licenseId, setLicenseId] = useState(source.licenseId ?? '');
  const [terms, setTerms] = useState(source.termsEvidenceRef ?? '');
  const [note, setNote] = useState('');
  const { busy, message, setMessage, run } = useAction();
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const err = approvalError(mode, licenseId, terms);
    if (err) return setMessage({ kind: 'error', text: err });
    void run(async () => {
      const r = await apiFetch<SourceDetail>(`/admin/aggregation/sources/${source.id}/approval`, {
        method: 'POST',
        body: { usageMode: mode, licenseId: licenseId.trim() || null, termsEvidenceRef: terms.trim(), note: note.trim() || null },
        ifMatch: etag ?? undefined,
        idempotencyKey: newIdempotencyKey('agg-approval'),
      });
      onSaved(r.data, r.etag);
      setNote('');
      return 'Đã duyệt — nguồn bắt đầu được thu thập.';
    });
  };
  return (
    <form className="card stack-sm" onSubmit={submit}>
      <h2>Duyệt chế độ khai thác</h2>
      <p className="muted small">Chỉ người có quyền duyệt (MFA). Người đề xuất nguồn không tự duyệt M2/M3. Ghi lịch sử chỉ thêm.</p>
      <div className="grid-2">
        <label className="field"><span>Chế độ</span>
          <select value={mode} onChange={(e) => setMode(e.target.value as UsageMode)}>
            {(Object.keys(USAGE_MODE_LABEL) as UsageMode[]).map((m) => <option key={m} value={m}>{USAGE_MODE_LABEL[m]}</option>)}
          </select>
        </label>
        <LicensePicker value={licenseId} onChange={setLicenseId} />
        <label className="field"><span>Bằng chứng điều khoản (đường dẫn DMS…)</span><input value={terms} maxLength={500} onChange={(e) => setTerms(e.target.value)} required /></label>
        <label className="field"><span>Ghi chú</span><input value={note} maxLength={2000} onChange={(e) => setNote(e.target.value)} /></label>
      </div>
      <ActionMessage message={message} onConflict={onConflict} clear={() => setMessage(null)} />
      <div className="row-end"><button type="submit" className="button primary" disabled={busy}>Duyệt</button></div>
    </form>
  );
}

// ---------------------------------------------------------------- feed

function Feeds({ source, canManage, onChanged }: { source: SourceDetail; canManage: boolean; onChanged: () => Promise<void> }) {
  const { categories } = useSiteCategories(source.siteKeys[0] ?? contentSites()[0]?.key ?? 'www');
  return (
    <div className="card stack-sm">
      <h2>Feed ({source.feeds.length})</h2>
      {source.feeds.length === 0 && <p className="muted">Chưa có feed nào.</p>}
      {source.feeds.map((f) => <FeedRow key={f.id} feed={f} canManage={canManage} categories={categories} onChanged={onChanged} />)}
      {canManage && <AddFeed sourceId={source.id} onAdded={onChanged} />}
    </div>
  );
}

function FeedHealthLine({ feed }: { feed: AggregationFeed }) {
  return (
    <p className="muted small">
      <HealthBadge health={feed.health} /> · đọc lần cuối {ago(feed.lastFetchedAt)} · thành công {ago(feed.lastSuccessAt)} · lần tới {dateTimeVi(feed.nextFetchAt)}
      {feed.consecutiveFailures > 0 && <> · <span className="warn-text">{feed.consecutiveFailures} lần lỗi liên tiếp ({label(FEED_FAILURE_LABEL, feed.lastFailure)})</span></>}
    </p>
  );
}

function FeedRow({ feed, canManage, categories, onChanged }: { feed: AggregationFeed; canManage: boolean; categories: CategoryRule[]; onChanged: () => Promise<void> }) {
  const [editing, setEditing] = useState(false);
  const [url, setUrl] = useState(feed.url);
  const [format, setFormat] = useState<FeedFormat>(feed.format);
  const [schedule, setSchedule] = useState(feed.scheduleMinutes);
  const [enabled, setEnabled] = useState(feed.isEnabled);
  const [mapping, setMapping] = useState<CategoryMapping[]>(feed.categoryMapping);
  const { busy, message, setMessage, run } = useAction();
  const categoryName = useMemo(() => new Map(categories.map((c) => [c.categoryId, c.categoryName])), [categories]);
  // Feed đổi (lưu xong, tải lại): đồng bộ ô sửa — giữ nguyên component để thông báo "Đã lưu" không mất.
  useEffect(() => {
    setUrl(feed.url);
    setFormat(feed.format);
    setSchedule(feed.scheduleMinutes);
    setEnabled(feed.isEnabled);
    setMapping(feed.categoryMapping);
  }, [feed]);

  const save = (e: FormEvent) => {
    e.preventDefault();
    const err = feedUrlError(url);
    if (err) return setMessage({ kind: 'error', text: err });
    const clean = mapping.filter((m) => m.feedCategory.trim() && m.categoryId);
    if (url.trim() !== feed.url && !window.confirm('Đổi URL feed ⇒ nguồn phải được duyệt lại trước khi thu thập tiếp. Tiếp tục?')) return;
    void run(async () => {
      await apiFetch(`/admin/aggregation/feeds/${feed.id}`, {
        method: 'PATCH',
        body: { url: url.trim(), format, scheduleMinutes: schedule, isEnabled: enabled, categoryMapping: clean },
        ifMatch: `W/"${feed.rowVersion}"`,
      });
      setEditing(false);
      await onChanged();
      return 'Đã lưu feed.';
    });
  };

  return (
    <div className="feed-row">
      <div className="title-row spread">
        <div>
          <code className="feed-url">{feed.url}</code> <span className="muted small">{feed.format} · {feed.scheduleMinutes} phút{feed.isEnabled ? '' : ' · đã tắt'}</span>
          <FeedHealthLine feed={feed} />
          {feed.categoryMapping.length > 0 && (
            <p className="muted small">Ánh xạ: {feed.categoryMapping.map((m) => `${m.feedCategory} → ${categoryName.get(m.categoryId) ?? m.categoryId.slice(0, 8)}`).join('; ')}</p>
          )}
        </div>
        <div className="row-start">
          <FeedTestButton url={feed.url} format={feed.format} disabled={!canManage} />
          {canManage && <button type="button" className="button small" onClick={() => setEditing((v) => !v)}>{editing ? 'Đóng' : 'Sửa'}</button>}
        </div>
      </div>
      {editing && (
        <form className="stack-sm" onSubmit={save}>
          <div className="grid-2">
            <label className="field"><span>URL (https)</span><input value={url} onChange={(e) => setUrl(e.target.value)} /></label>
            <label className="field"><span>Định dạng</span>
              <select value={format} onChange={(e) => setFormat(e.target.value as FeedFormat)}>{FEED_FORMATS.map((f) => <option key={f} value={f}>{f}</option>)}</select>
            </label>
            <label className="field"><span>Chu kỳ (5–1440 phút)</span><input type="number" min={5} max={1440} value={schedule} onChange={(e) => setSchedule(Number(e.target.value))} /></label>
            <label className="check inline"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /><span>Đang bật</span></label>
          </div>
          <fieldset className="stack-sm">
            <legend>Ánh xạ category của feed → chuyên mục</legend>
            {mapping.map((m, i) => (
              <div key={i} className="inline-form">
                <input aria-label="Category của feed" value={m.feedCategory} onChange={(e) => setMapping((cur) => cur.map((x, j) => (j === i ? { ...x, feedCategory: e.target.value } : x)))} />
                <select aria-label="Chuyên mục" value={m.categoryId} onChange={(e) => setMapping((cur) => cur.map((x, j) => (j === i ? { ...x, categoryId: e.target.value } : x)))}>
                  <option value="">— chọn —</option>
                  {categories.map((c) => <option key={c.categoryId} value={c.categoryId}>{c.categoryName}</option>)}
                </select>
                <button type="button" className="button small" onClick={() => setMapping((cur) => cur.filter((_, j) => j !== i))}>Bỏ</button>
              </div>
            ))}
            <div><button type="button" className="button small" onClick={() => setMapping((cur) => [...cur, { feedCategory: '', categoryId: '' }])}>+ Thêm ánh xạ</button></div>
          </fieldset>
          <div className="row-end"><button type="submit" className="button primary" disabled={busy}>Lưu feed</button></div>
        </form>
      )}
      {message && <Banner kind={message.kind === 'ok' ? 'ok' : 'error'} onRetry={message.conflict ? () => void onChanged() : undefined}>{message.text}</Banner>}
    </div>
  );
}

function AddFeed({ sourceId, onAdded }: { sourceId: string; onAdded: () => Promise<void> }) {
  const [url, setUrl] = useState('');
  const [format, setFormat] = useState<FeedFormat>('AUTO');
  const [schedule, setSchedule] = useState(15);
  const { busy, message, setMessage, run } = useAction();
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const err = feedUrlError(url);
    if (err) return setMessage({ kind: 'error', text: err });
    void run(async () => {
      await apiFetch(`/admin/aggregation/sources/${sourceId}/feeds`, {
        method: 'POST', body: { url: url.trim(), format, scheduleMinutes: schedule }, idempotencyKey: newIdempotencyKey('agg-feed'),
      });
      setUrl('');
      await onAdded();
      return 'Đã thêm feed. Nguồn cần được duyệt lại trước khi thu thập.';
    });
  };
  return (
    <form className="stack-sm add-feed" onSubmit={submit}>
      <h3>Thêm feed</h3>
      <div className="inline-form">
        <label className="field grow"><span>URL (https)</span><input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://…/rss" /></label>
        <label className="field"><span>Định dạng</span><select value={format} onChange={(e) => setFormat(e.target.value as FeedFormat)}>{FEED_FORMATS.map((f) => <option key={f} value={f}>{f}</option>)}</select></label>
        <label className="field"><span>Chu kỳ (phút)</span><input type="number" min={5} max={1440} value={schedule} onChange={(e) => setSchedule(Number(e.target.value))} className="narrow-input" /></label>
      </div>
      <div className="row-end">
        <FeedTestButton url={url} format={format} disabled={!url.trim()} />
        <button type="submit" className="button primary" disabled={busy}>Thêm feed</button>
      </div>
      {message && <Banner kind={message.kind === 'ok' ? 'ok' : 'error'}>{message.text}</Banner>}
    </form>
  );
}

/** Thử đọc feed bằng chính bộ thu thập (không lưu). Lỗi của nguồn là dữ liệu; URL nội bộ → 422 UNSAFE_URL; quá số lần/phút → 429. */
function FeedTestButton({ url, format, disabled }: { url: string; format: FeedFormat; disabled?: boolean }) {
  const [result, setResult] = useState<FeedTestResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const test = async () => {
    const err = feedUrlError(url);
    if (err) return setError(err);
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      setResult((await apiFetch<FeedTestResult>('/admin/aggregation/feed-tests', { method: 'POST', body: { url: url.trim(), format } })).data);
    } catch (e) {
      setError(aggErrorText(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <>
      <button type="button" className="button small" disabled={disabled || busy} onClick={() => void test()}>{busy ? 'Đang thử…' : 'Thử feed'}</button>
      {(result || error) && (
        <div className="feed-test" role="status">
          {error && <span className="field-error">{error}</span>}
          {result && (result.ok ? (
            <>
              <strong>Đọc được</strong> {result.detectedFormat} · {result.itemCount} mục · {result.durationMs} ms{result.feedTitle && ` · “${result.feedTitle}”`}
              <ul>{result.sampleItems.map((i) => <li key={i.url}>{i.title} <span className="muted small">{dateTimeVi(i.publishedAt)}{i.timeReliable ? '' : ' (giờ không chắc)'}</span></li>)}</ul>
            </>
          ) : (
            <span className="warn-text">Không đọc được: {label(FEED_FAILURE_LABEL, result.failure)}{result.httpStatus ? ` (HTTP ${result.httpStatus})` : ''}{!result.robotsAllowed && ' — robots.txt chặn bot'}</span>
          ))}
        </div>
      )}
    </>
  );
}

// ---------------------------------------------------------------- luật chuyên mục

export function RulesPage() {
  return (
    <RequirePermission permission="aggregation.read">
      <Rules />
    </RequirePermission>
  );
}

function Rules() {
  const staff = useStaff();
  const canManage = access(staff, 'aggregation.sources_manage') === 'allowed';
  const [siteKey, setSiteKey] = useState(() => contentSites()[0]?.key ?? 'www');
  const [rules, setRules] = useState<CategoryRule[]>([]);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    setError(null);
    try {
      setRules((await apiFetch<{ items: CategoryRule[] }>(`/admin/aggregation/category-rules?siteKey=${encodeURIComponent(siteKey)}`)).data?.items ?? []);
    } catch (e) {
      setError(aggErrorText(e));
    }
  }, [siteKey]);
  useEffect(() => void load(), [load]);

  return (
    <section className="stack">
      <div>
        <h1>Luật chuyên mục</h1>
        <p className="muted">
          Từ khóa phân loại tin vào chuyên mục (BR-30). Hai từ khóa khớp tiêu đề ⇒ tự gán; một từ khóa ⇒ gán kèm "cần xem". Từ khóa thực thể (tên sản phẩm,
          hãng) còn dùng để gom cụm. Khớp nguyên cụm từ, giữ dấu tiếng Việt. Chỉ áp cho tin thu thập sau khi lưu.
        </p>
      </div>
      <Tabs active="rules" />
      <div className="toolbar"><SiteSelect value={siteKey} onChange={setSiteKey} /></div>
      {error && <Banner kind="error" onRetry={() => void load()}>{error}</Banner>}
      {rules.map((r) => <RuleEditor key={r.categoryId} rule={r} canManage={canManage} onSaved={(n) => setRules((cur) => cur.map((x) => (x.categoryId === n.categoryId ? n : x)))} onConflict={load} />)}
      {!error && rules.length === 0 && <div className="card empty muted">Site này chưa có chuyên mục đang bật.</div>}
    </section>
  );
}

function RuleEditor({ rule, canManage, onSaved, onConflict }: { rule: CategoryRule; canManage: boolean; onSaved: (r: CategoryRule) => void; onConflict: () => Promise<void> }) {
  const [include, setInclude] = useState(keywordsText(rule.includeKeywords));
  const [exclude, setExclude] = useState(keywordsText(rule.excludeKeywords));
  const [entity, setEntity] = useState(keywordsText(rule.entityKeywords));
  const { busy, message, setMessage, run } = useAction();
  useEffect(() => {
    setInclude(keywordsText(rule.includeKeywords));
    setExclude(keywordsText(rule.excludeKeywords));
    setEntity(keywordsText(rule.entityKeywords));
  }, [rule]);
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const parsed = [parseKeywords(include), parseKeywords(exclude), parseKeywords(entity)];
    const err = parsed.find((p) => p.error)?.error;
    if (err) return setMessage({ kind: 'error', text: err });
    void run(async () => {
      const r = await apiFetch<CategoryRule>(`/admin/aggregation/category-rules/${rule.categoryId}`, {
        method: 'PATCH',
        body: { includeKeywords: parsed[0].keywords, excludeKeywords: parsed[1].keywords, entityKeywords: parsed[2].keywords },
        ifMatch: `W/"${rule.rowVersion}"`,
      });
      if (r.data) onSaved(r.data);
      return 'Đã lưu luật.';
    });
  };
  return (
    <form className="card stack-sm" onSubmit={submit}>
      <div className="title-row spread">
        <h2>{rule.categoryName}</h2>
        <span className="muted small">{rule.updatedAt ? `Sửa ${ago(rule.updatedAt)}` : 'Chưa có từ khóa'}</span>
      </div>
      <div className="grid-2">
        <label className="field"><span>Từ khóa (phẩy hoặc xuống dòng)</span><textarea rows={3} value={include} onChange={(e) => setInclude(e.target.value)} disabled={!canManage} /></label>
        <label className="field"><span>Thực thể: sản phẩm, hãng (dùng cả cho gom cụm)</span><textarea rows={3} value={entity} onChange={(e) => setEntity(e.target.value)} disabled={!canManage} /></label>
        <label className="field"><span>Từ khóa loại trừ</span><textarea rows={2} value={exclude} onChange={(e) => setExclude(e.target.value)} disabled={!canManage} /></label>
      </div>
      {message && <Banner kind={message.kind === 'ok' ? 'ok' : 'error'} onRetry={message.conflict ? () => void onConflict() : undefined}>{message.text}</Banner>}
      {canManage && <div className="row-end"><button type="submit" className="button primary" disabled={busy}>Lưu</button></div>}
    </form>
  );
}

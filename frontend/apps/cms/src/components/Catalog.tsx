'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { apiFetch } from '../lib/api';
import type { Page } from '../lib/aggregation';
import {
  SOURCE_KINDS,
  SOURCE_KIND_LABEL,
  dateInputToIso,
  isoToDateInput,
  licenseCodeError,
  licenseError,
  licenseStatus,
  sourceError,
  type ContentSource,
  type License,
  type SourceKind,
} from '../lib/catalog';
import { access, permissionLabel } from '../lib/rbac';
import { RequirePermission } from './CmsApp';
import { Banner, aggErrorText, isConflict, newIdempotencyKey } from './aggregationCommon';
import { useStaff } from './session';

// Danh mục nguồn nội dung + giấy phép (ADR-0007 §1, FR-NEWS-006). Xem: content.read; sửa nguồn: taxonomy.manage; sửa giấy phép: licenses.manage (MFA).
// Có bản ghi giấy phép KHÔNG có nghĩa là đã được phép khai thác — Legal nhập và chịu trách nhiệm.

function Tabs({ active }: { active: 'sources' | 'licenses' }) {
  return (
    <nav className="tabs-bar" aria-label="Danh mục">
      <Link href="/catalog" className={active === 'sources' ? 'tab active' : 'tab'} aria-current={active === 'sources' ? 'page' : undefined}>Nguồn nội dung</Link>
      <Link href="/catalog/licenses" className={active === 'licenses' ? 'tab active' : 'tab'} aria-current={active === 'licenses' ? 'page' : undefined}>Giấy phép</Link>
    </nav>
  );
}

function useList<T>(path: string) {
  const [q, setQ] = useState('');
  const [enabledOnly, setEnabledOnly] = useState(false);
  const [items, setItems] = useState<T[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async (after: string | null) => {
    setLoading(true);
    setError(null);
    const params = new URLSearchParams({ limit: '50' });
    if (q.trim()) params.set('q', q.trim());
    if (enabledOnly) params.set('isEnabled', 'true');
    if (after) params.set('cursor', after);
    try {
      const { data } = await apiFetch<Page<T>>(`${path}?${params}`);
      setItems((prev) => (after ? [...prev, ...(data?.items ?? [])] : data?.items ?? []));
      setCursor(data?.nextCursor ?? null);
    } catch (e) {
      setError(aggErrorText(e));
    } finally {
      setLoading(false);
    }
  }, [path, q, enabledOnly]);
  useEffect(() => void load(null), [enabledOnly]);
  return { q, setQ, enabledOnly, setEnabledOnly, items, setItems, cursor, loading, error, load };
}

function SearchBar({ q, setQ, enabledOnly, setEnabledOnly, onSearch }: { q: string; setQ: (v: string) => void; enabledOnly: boolean; setEnabledOnly: (v: boolean) => void; onSearch: () => void }) {
  return (
    <form className="toolbar" role="search" onSubmit={(e) => { e.preventDefault(); onSearch(); }}>
      <label className="field grow"><span>Tìm</span><input value={q} onChange={(e) => setQ(e.target.value)} maxLength={200} /></label>
      <label className="check inline"><input type="checkbox" checked={enabledOnly} onChange={(e) => setEnabledOnly(e.target.checked)} /><span>Chỉ mục đang bật</span></label>
      <button type="submit" className="button primary">Tìm</button>
    </form>
  );
}

// ---------------------------------------------------------------- nguồn

export function SourceCatalogPage() {
  return (
    <RequirePermission permission="content.read">
      <SourceCatalog />
    </RequirePermission>
  );
}

function SourceCatalog() {
  const staff = useStaff();
  const canManage = access(staff, 'taxonomy.manage') === 'allowed';
  const list = useList<ContentSource>('/admin/sources');
  const replace = (s: ContentSource) => list.setItems((cur) => cur.map((x) => (x.id === s.id ? s : x)));
  return (
    <section className="stack">
      <div>
        <h1>Nguồn & giấy phép</h1>
        <p className="muted">Danh mục nguồn nội dung dùng cho bài viết (metadata quyền) và Radar tin. Không xóa — chỉ tắt để giữ truy vết.</p>
      </div>
      <Tabs active="sources" />
      <SearchBar {...list} onSearch={() => void list.load(null)} />
      {list.error && <Banner kind="error" onRetry={() => void list.load(null)}>{list.error}</Banner>}
      <div className="card flush">
        <table className="table">
          <thead><tr><th scope="col">Tên</th><th scope="col">Loại</th><th scope="col">Trang web</th><th scope="col">Trạng thái</th>{canManage && <th scope="col"><span className="visually-hidden">Thao tác</span></th>}</tr></thead>
          <tbody>
            {list.items.map((s) => <SourceRow key={s.id} source={s} canManage={canManage} onSaved={replace} onConflict={() => list.load(null)} />)}
            {!list.loading && list.items.length === 0 && <tr><td colSpan={5} className="muted center">Chưa có nguồn nào.</td></tr>}
          </tbody>
        </table>
      </div>
      <div className="row-end">
        {list.loading && <span className="muted">Đang tải…</span>}
        {!list.loading && list.cursor && <button type="button" className="button" onClick={() => void list.load(list.cursor)}>Xem thêm</button>}
      </div>
      {canManage ? <SourceForm onSaved={() => void list.load(null)} /> : <Banner kind="warn">Thêm/sửa nguồn cần quyền "{permissionLabel('taxonomy.manage')}".</Banner>}
    </section>
  );
}

function SourceRow({ source, canManage, onSaved, onConflict }: { source: ContentSource; canManage: boolean; onSaved: (s: ContentSource) => void; onConflict: () => Promise<void> }) {
  const [editing, setEditing] = useState(false);
  if (editing) {
    return (
      <tr><td colSpan={5}><SourceForm source={source} onSaved={(s) => { if (s) onSaved(s); setEditing(false); }} onCancel={() => setEditing(false)} onConflict={onConflict} /></td></tr>
    );
  }
  return (
    <tr>
      <td>{source.name}</td>
      <td>{SOURCE_KIND_LABEL[source.kind] ?? source.kind}</td>
      <td className="break">{source.url ?? <span className="muted">—</span>}</td>
      <td><span className={source.isEnabled ? 'badge ok' : 'badge'}>{source.isEnabled ? 'Đang bật' : 'Đã tắt'}</span></td>
      {canManage && <td><button type="button" className="button small" onClick={() => setEditing(true)}>Sửa</button></td>}
    </tr>
  );
}

function SourceForm({ source, onSaved, onCancel, onConflict }: { source?: ContentSource; onSaved: (s: ContentSource | null) => void; onCancel?: () => void; onConflict?: () => Promise<void> }) {
  const [name, setName] = useState(source?.name ?? '');
  const [kind, setKind] = useState<SourceKind>(source?.kind ?? 'AGENCY');
  const [url, setUrl] = useState(source?.url ?? '');
  const [enabled, setEnabled] = useState(source?.isEnabled ?? true);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string; conflict?: boolean } | null>(null);
  const [key, setKey] = useState(() => newIdempotencyKey('source'));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const err = sourceError(name, url);
    if (err) return setMessage({ kind: 'error', text: err });
    setBusy(true);
    setMessage(null);
    try {
      const r = source
        ? await apiFetch<ContentSource>(`/admin/sources/${source.id}`, {
            method: 'PATCH', body: { name: name.trim(), kind, url: url.trim() || null, isEnabled: enabled }, ifMatch: `W/"${source.rowVersion}"`,
          })
        : await apiFetch<ContentSource>('/admin/sources', { method: 'POST', body: { name: name.trim(), kind, ...(url.trim() ? { url: url.trim() } : {}) }, idempotencyKey: key });
      if (!source) {
        setName('');
        setUrl('');
        setKey(newIdempotencyKey('source'));
        setMessage({ kind: 'ok', text: 'Đã thêm nguồn.' });
      }
      onSaved(r.data);
    } catch (err2) {
      setMessage({ kind: 'error', text: aggErrorText(err2), conflict: isConflict(err2) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className={source ? 'stack-sm' : 'card stack-sm'} onSubmit={submit}>
      {!source && <h2>Thêm nguồn</h2>}
      <div className="grid-2">
        <label className="field"><span>Tên</span><input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} required /></label>
        <label className="field"><span>Loại</span><select value={kind} onChange={(e) => setKind(e.target.value as SourceKind)}>{SOURCE_KINDS.map((k) => <option key={k} value={k}>{SOURCE_KIND_LABEL[k]}</option>)}</select></label>
        <label className="field"><span>Trang web (tùy chọn)</span><input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://…" /></label>
        {source && <label className="check inline"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /><span>Đang bật</span></label>}
      </div>
      {message && <Banner kind={message.kind} onRetry={message.conflict && onConflict ? () => void onConflict() : undefined}>{message.text}</Banner>}
      <div className="row-end">
        {onCancel && <button type="button" className="button" onClick={onCancel}>Hủy</button>}
        <button type="submit" className="button primary" disabled={busy}>{source ? 'Lưu' : 'Thêm nguồn'}</button>
      </div>
    </form>
  );
}

// ---------------------------------------------------------------- giấy phép

export function LicenseCatalogPage() {
  return (
    <RequirePermission permission="content.read">
      <LicenseCatalog />
    </RequirePermission>
  );
}

function LicenseCatalog() {
  const staff = useStaff();
  const decision = access(staff, 'licenses.manage');
  const list = useList<License>('/admin/licenses');
  const replace = (l: License) => list.setItems((cur) => cur.map((x) => (x.id === l.id ? l : x)));
  return (
    <section className="stack">
      <div>
        <h1>Nguồn & giấy phép</h1>
        <p className="muted">
          Giấy phép / thỏa thuận sử dụng nội dung. Có bản ghi ở đây <strong>không</strong> có nghĩa là đã được phép khai thác: Legal nhập và chịu trách nhiệm.
          Chế độ M2/M3 của nguồn thu thập chỉ dùng được giấy phép đang hiệu lực.
        </p>
      </div>
      <Tabs active="licenses" />
      <SearchBar {...list} onSearch={() => void list.load(null)} />
      {list.error && <Banner kind="error" onRetry={() => void list.load(null)}>{list.error}</Banner>}
      <div className="stack-sm">
        {list.items.map((l) => <LicenseCard key={l.id} license={l} canManage={decision === 'allowed'} onSaved={replace} onConflict={() => list.load(null)} />)}
        {!list.loading && list.items.length === 0 && <div className="card empty muted">Chưa có giấy phép nào.</div>}
      </div>
      <div className="row-end">
        {list.loading && <span className="muted">Đang tải…</span>}
        {!list.loading && list.cursor && <button type="button" className="button" onClick={() => void list.load(list.cursor)}>Xem thêm</button>}
      </div>
      {decision === 'allowed' && <LicenseForm onSaved={() => void list.load(null)} />}
      {decision === 'mfa-required' && <Banner kind="warn">Thêm/sửa giấy phép cần quyền "{permissionLabel('licenses.manage')}" với phiên đã qua MFA.</Banner>}
    </section>
  );
}

function LicenseCard({ license, canManage, onSaved, onConflict }: { license: License; canManage: boolean; onSaved: (l: License) => void; onConflict: () => Promise<void> }) {
  const [editing, setEditing] = useState(false);
  const status = licenseStatus(license);
  return (
    <div className="card stack-sm">
      <div className="title-row spread">
        <div>
          <h2>{license.name} <code className="small">{license.code}</code></h2>
          <p className="muted small">
            {license.validFrom ? new Date(license.validFrom).toLocaleDateString('vi-VN') : 'không giới hạn'} → {license.validTo ? new Date(license.validTo).toLocaleDateString('vi-VN') : 'không giới hạn'}
            {license.requiresAttribution && ' · bắt buộc ghi nguồn'} · {license.evidenceRef ?? 'chưa có bằng chứng'}
          </p>
        </div>
        <div className="row-start">
          <span className={status === 'Đang hiệu lực' ? 'badge ok' : 'badge warn'}>{status}</span>
          {canManage && <button type="button" className="button small" onClick={() => setEditing((v) => !v)}>{editing ? 'Đóng' : 'Sửa'}</button>}
        </div>
      </div>
      <p className="small">{license.usageScope}</p>
      {editing && <LicenseForm license={license} onSaved={(l) => { if (l) onSaved(l); setEditing(false); }} onConflict={onConflict} />}
    </div>
  );
}

function LicenseForm({ license, onSaved, onConflict }: { license?: License; onSaved: (l: License | null) => void; onConflict?: () => Promise<void> }) {
  const [code, setCode] = useState(license?.code ?? '');
  const [name, setName] = useState(license?.name ?? '');
  const [usageScope, setUsageScope] = useState(license?.usageScope ?? '');
  const [evidenceRef, setEvidenceRef] = useState(license?.evidenceRef ?? '');
  const [validFrom, setValidFrom] = useState(isoToDateInput(license?.validFrom ?? null));
  const [validTo, setValidTo] = useState(isoToDateInput(license?.validTo ?? null));
  const [attribution, setAttribution] = useState(license?.requiresAttribution ?? true);
  const [enabled, setEnabled] = useState(license?.isEnabled ?? true);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string; conflict?: boolean } | null>(null);
  const [key, setKey] = useState(() => newIdempotencyKey('license'));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const err = (license ? null : licenseCodeError(code)) ?? licenseError({ name, usageScope, evidenceRef, validFrom, validTo });
    if (err) return setMessage({ kind: 'error', text: err });
    setBusy(true);
    setMessage(null);
    const common = {
      name: name.trim(), usageScope: usageScope.trim(), evidenceRef: evidenceRef.trim(), requiresAttribution: attribution,
      validFrom: dateInputToIso(validFrom), validTo: dateInputToIso(validTo),
    };
    try {
      const r = license
        ? await apiFetch<License>(`/admin/licenses/${license.id}`, { method: 'PATCH', body: { ...common, isEnabled: enabled }, ifMatch: `W/"${license.rowVersion}"` })
        : await apiFetch<License>('/admin/licenses', {
            method: 'POST',
            body: { code, name: common.name, usageScope: common.usageScope, evidenceRef: common.evidenceRef, requiresAttribution: attribution,
              ...(common.validFrom ? { validFrom: common.validFrom } : {}), ...(common.validTo ? { validTo: common.validTo } : {}) },
            idempotencyKey: key,
          });
      if (!license) {
        setCode('');
        setName('');
        setUsageScope('');
        setEvidenceRef('');
        setKey(newIdempotencyKey('license'));
        setMessage({ kind: 'ok', text: 'Đã thêm giấy phép.' });
      }
      onSaved(r.data);
    } catch (err2) {
      setMessage({ kind: 'error', text: aggErrorText(err2), conflict: isConflict(err2) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className={license ? 'stack-sm' : 'card stack-sm'} onSubmit={submit}>
      {!license && <h2>Thêm giấy phép</h2>}
      <div className="grid-2">
        {!license && <label className="field"><span>Mã (không đổi được)</span><input value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} maxLength={60} placeholder="VD: AFP-2026" required /></label>}
        <label className="field"><span>Tên</span><input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} required /></label>
        <label className="field"><span>Bằng chứng (hợp đồng trong kho tài liệu)</span><input value={evidenceRef} maxLength={500} onChange={(e) => setEvidenceRef(e.target.value)} required /></label>
        <label className="field"><span>Hiệu lực từ</span><input type="date" value={validFrom} onChange={(e) => setValidFrom(e.target.value)} /></label>
        <label className="field"><span>Hết hạn</span><input type="date" value={validTo} onChange={(e) => setValidTo(e.target.value)} /></label>
        <label className="check inline"><input type="checkbox" checked={attribution} onChange={(e) => setAttribution(e.target.checked)} /><span>Bắt buộc ghi nguồn</span></label>
        {license && <label className="check inline"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /><span>Đang bật</span></label>}
      </div>
      <label className="field"><span>Phạm vi được sử dụng</span><textarea rows={3} maxLength={4000} value={usageScope} onChange={(e) => setUsageScope(e.target.value)} required /></label>
      {message && <Banner kind={message.kind} onRetry={message.conflict && onConflict ? () => void onConflict() : undefined}>{message.text}</Banner>}
      <div className="row-end"><button type="submit" className="button primary" disabled={busy}>{license ? 'Lưu giấy phép' : 'Thêm giấy phép'}</button></div>
    </form>
  );
}

// ---------------------------------------------------------------- ô chọn dùng ở màn khác (Radar tin)

/** Chọn nguồn đang bật trong danh mục (tìm theo tên). */
export function ContentSourcePicker({ value, onChange }: { value: string; onChange: (id: string) => void }) {
  const [q, setQ] = useState('');
  const [items, setItems] = useState<ContentSource[]>([]);
  useEffect(() => {
    const t = setTimeout(() => {
      const params = new URLSearchParams({ limit: '50', isEnabled: 'true' });
      if (q.trim()) params.set('q', q.trim());
      apiFetch<Page<ContentSource>>(`/admin/sources?${params}`).then((r) => setItems(r.data?.items ?? [])).catch(() => setItems([]));
    }, 250);
    return () => clearTimeout(t);
  }, [q]);
  return (
    <div className="inline-form">
      <label className="field"><span>Tìm nguồn</span><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Tên nguồn" /></label>
      <label className="field grow"><span>Nguồn trong danh mục</span>
        <select value={value} onChange={(e) => onChange(e.target.value)} required>
          <option value="">— chọn nguồn —</option>
          {items.map((s) => <option key={s.id} value={s.id}>{s.name} ({SOURCE_KIND_LABEL[s.kind]})</option>)}
        </select>
      </label>
      <Link href="/catalog" className="button small">Thêm vào danh mục</Link>
    </div>
  );
}

/** Chọn giấy phép (hiện trạng thái hiệu lực; M2/M3 chỉ nhận giấy phép đang hiệu lực — API kiểm). */
export function LicensePicker({ value, onChange }: { value: string; onChange: (id: string) => void }) {
  const [items, setItems] = useState<License[]>([]);
  useEffect(() => {
    apiFetch<Page<License>>('/admin/licenses?limit=100&isEnabled=true').then((r) => setItems(r.data?.items ?? [])).catch(() => setItems([]));
  }, []);
  return (
    <label className="field"><span>Giấy phép (bắt buộc M2/M3)</span>
      <select value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">— không —</option>
        {items.map((l) => <option key={l.id} value={l.id}>{l.code} — {l.name} ({licenseStatus(l)})</option>)}
      </select>
    </label>
  );
}

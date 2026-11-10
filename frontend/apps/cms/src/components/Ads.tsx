'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { registry } from '@tinhte/sites';
import { ApiError, apiFetch } from '../lib/api';
import { access } from '../lib/rbac';
import {
  AD_BREAKPOINTS, adErrorText, adStatusLabel, adStatusTone, bookingBody, bookingDraftErrors, bookingStatusBody, breakpointLabel, breakpointRange,
  draftFromBooking, emptyBookingDraft, formatSize, isContentLocked, isLiveNow, parseSize, placementDraftErrors, placementUpdateBody, placementsQuery,
  scheduleText, sizeKey, sizesInUse, sizesSummary, sortSizes, statusActions, transitionPermission,
  type AdBooking, type AdBreakpoint, type AdCreative, type AdPlacement, type AdSize, type AdStatus, type BookingDraft, type CreativeDraft,
  type ItemList, type PlacementDraft,
} from '../lib/ads';
import { formatBytes, isMediaFailed, mediaListQuery, rightsLabel, titleFromFileName, type MediaStatus, type Page } from '../lib/videos';
import { RequirePermission } from './CmsApp';
import { useSession, useStaff } from './session';
import { MediaStatusBadge, UploadBox, useMediaStatus } from './VideosUpload';

// Màn Quảng cáo (P0-ADS, FR-ADS-002; WBS-136). API: /api/v1/admin/ad-placements*, /api/v1/admin/ad-bookings/* (hợp đồng v0.3 mục 16/19).
// Quyền ads.manage / ads.approve và vai trò AD_OPS là ĐỀ XUẤT (config/rbac/rbac.json) chờ BA/PO — ẩn/hiện ở đây chỉ là giao diện.

/** Site public-web của Site Registry (ADR-0001): mỗi vị trí thuộc một site, không đổi được sau khi tạo. */
const PUBLIC_SITES = registry.sites.filter((s) => s.kind === 'public-web');
const BOOKABLE_SITES = PUBLIC_SITES.filter((s) => s.status === 'active' || s.status === 'planned');
const siteName = (key: string) => PUBLIC_SITES.find((s) => s.key === key)?.label ?? key;
const formatTime = (iso: string | null | undefined) => (iso ? new Date(iso).toLocaleString('vi-VN') : '—');
const IMAGE_ACCEPT = 'image/jpeg,image/png,image/webp,.jpg,.jpeg,.png,.webp';

function StatusBadge({ status }: { status: string }) {
  return <span className={`badge ${adStatusTone(status)}`}>{adStatusLabel(status)}</span>;
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

function DraftNotice() {
  return (
    <p className="muted small">
      Bản nháp P0-ADS: quyền “Quản lý quảng cáo”, “Duyệt quảng cáo” và vai trò Vận hành quảng cáo đang chờ BA/PO duyệt. Banner chỉ hiện trên site khi
      cờ <code>ads.display_slots</code> bật (production mặc định tắt).
    </p>
  );
}

/** Danh sách cursor của thư viện media: "Xem thêm" nối trang sau. */
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
      setError(adErrorText(e));
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  return { items, cursor, loading, error, load };
}

// ---------------------------------------------------------------- kích thước theo breakpoint

function SizesEditor({ sizes, onChange, inUse, disabled = false }: {
  sizes: AdSize[];
  onChange: (sizes: AdSize[]) => void;
  inUse?: Set<string>;
  disabled?: boolean;
}) {
  const [input, setInput] = useState<Record<AdBreakpoint, string>>({ MOBILE: '', TABLET: '', DESKTOP: '' });
  const [error, setError] = useState<string | null>(null);

  const add = (bp: AdBreakpoint) => {
    const parsed = parseSize(input[bp]);
    if (!parsed) {
      setError(`${breakpointLabel(bp)}: nhập kích thước dạng 970x250 (1–4000 px).`);
      return;
    }
    const size: AdSize = { breakpoint: bp, ...parsed };
    if (sizes.some((s) => sizeKey(s) === sizeKey(size))) {
      setError(`${breakpointLabel(bp)} đã có ${formatSize(size)}.`);
      return;
    }
    setError(null);
    onChange(sortSizes([...sizes, size]));
    setInput((i) => ({ ...i, [bp]: '' }));
  };

  return (
    <fieldset className="plain">
      <legend className="legend">Kích thước banner cho phép</legend>
      {AD_BREAKPOINTS.map((bp) => {
        const own = sortSizes(sizes.filter((s) => s.breakpoint === bp));
        return (
          <div key={bp} className="size-row">
            <div className="size-head">
              <strong>{breakpointLabel(bp)}</strong> <span className="muted small">{breakpointRange(bp)}</span>
            </div>
            <div className="chips">
              {own.map((s) => {
                const used = inUse?.has(sizeKey(s)) ?? false;
                return (
                  <span key={sizeKey(s)} className="chip">
                    {formatSize(s)}
                    {!disabled && used && <span className="chip-note" title="Đặt chỗ đang dùng kích thước này — không gỡ được">đang dùng</span>}
                    {!disabled && !used && (
                      <button type="button" className="chip-x" aria-label={`Gỡ ${breakpointLabel(bp)} ${formatSize(s)}`}
                        onClick={() => onChange(sizes.filter((x) => sizeKey(x) !== sizeKey(s)))}>
                        ×
                      </button>
                    )}
                  </span>
                );
              })}
              {own.length === 0 && <span className="muted small">Không có — vị trí không hiện quảng cáo trên {breakpointLabel(bp).toLowerCase()}.</span>}
            </div>
            {!disabled && (
              <div className="size-add">
                <input
                  aria-label={`Thêm kích thước ${breakpointLabel(bp)}`}
                  placeholder="rộng×cao, ví dụ 970x250"
                  value={input[bp]}
                  onChange={(e) => setInput((i) => ({ ...i, [bp]: e.target.value }))}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault();
                      add(bp);
                    }
                  }}
                />
                <button type="button" className="button small" onClick={() => add(bp)}>
                  Thêm
                </button>
              </div>
            )}
          </div>
        );
      })}
      {error && <span className="field-error">{error}</span>}
    </fieldset>
  );
}

// ---------------------------------------------------------------- đổi trạng thái (vị trí + đặt chỗ)

function StatusPanel({ status, onChange, busy, blockedReason, approveNote }: {
  status: AdStatus;
  onChange: (to: AdStatus) => void;
  busy: boolean;
  /** Ví dụ: còn thay đổi chưa lưu. */
  blockedReason?: string | null;
  approveNote: string;
}) {
  const staff = useStaff();
  const { signIn } = useSession();
  const [confirmEnd, setConfirmEnd] = useState(false);
  const actions = statusActions(status).map((a) => ({ ...a, decision: access(staff, transitionPermission(status, a.to)) }));
  const hidden = actions.filter((a) => a.decision === 'denied');
  const visible = actions.filter((a) => a.decision !== 'denied');
  const needsMfa = visible.some((a) => a.decision === 'mfa-required');

  if (actions.length === 0) return <p className="muted small">Đã kết thúc — không đổi trạng thái được nữa.</p>;
  return (
    <div className="stack-sm">
      {blockedReason && <p className="muted small">{blockedReason}</p>}
      <div className="actions">
        {visible.filter((a) => !a.danger).map((a) => (
          <button key={a.to} type="button" className={a.to === 'APPROVED' || a.to === 'ACTIVE' ? 'button small primary' : 'button small'}
            disabled={busy || !!blockedReason || a.decision !== 'allowed'} onClick={() => onChange(a.to)}>
            {a.label}
            {a.decision === 'mfa-required' && <span className="lock" aria-label="cần MFA">MFA</span>}
          </button>
        ))}
        {needsMfa && (
          <button type="button" className="button small" onClick={signIn}>
            Đăng nhập lại với MFA
          </button>
        )}
        {visible.some((a) => a.danger) && !confirmEnd && (
          <button type="button" className="button small" disabled={busy || !!blockedReason} onClick={() => setConfirmEnd(true)}>
            Kết thúc…
          </button>
        )}
      </div>
      {confirmEnd && (
        <div className="banner warn stacked" role="alertdialog" aria-label="Xác nhận kết thúc">
          <span>Kết thúc là bước cuối: không mở lại hay sửa được nữa.</span>
          <div className="actions">
            <button type="button" className="button small danger" disabled={busy} onClick={() => { setConfirmEnd(false); onChange('EXPIRED'); }}>
              Kết thúc vĩnh viễn
            </button>
            <button type="button" className="button small" onClick={() => setConfirmEnd(false)}>
              Hủy
            </button>
          </div>
        </div>
      )}
      {hidden.length > 0 && <p className="muted small">{approveNote}</p>}
      {needsMfa && <p className="muted small">Duyệt và mở quảng cáo cần phiên đã qua MFA.</p>}
    </div>
  );
}

// ---------------------------------------------------------------- danh sách vị trí

export function AdPlacementsPage() {
  return (
    <RequirePermission permission="ads.manage">
      <PlacementList />
    </RequirePermission>
  );
}

function PlacementList() {
  const [siteKey, setSiteKey] = useState(PUBLIC_SITES.length === 1 ? PUBLIC_SITES[0].key : '');
  const [items, setItems] = useState<AdPlacement[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const { data } = await apiFetch<ItemList<AdPlacement>>(placementsQuery(siteKey));
      setItems(data?.items ?? []);
    } catch (e) {
      setError(adErrorText(e));
    } finally {
      setLoading(false);
    }
  }, [siteKey]);

  useEffect(() => void load(), [load]);

  return (
    <section className="stack ads">
      <div className="title-row spread">
        <div>
          <h1>Quảng cáo</h1>
          <p className="muted">Vị trí quảng cáo trên trang của từng site và kích thước banner cho phép theo màn hình. Banner, link, lịch chạy nằm ở đặt chỗ của vị trí.</p>
        </div>
        {!creating && BOOKABLE_SITES.length > 0 && (
          <button type="button" className="button primary" onClick={() => setCreating(true)}>
            Thêm vị trí
          </button>
        )}
      </div>
      <DraftNotice />

      {creating && <NewPlacementForm defaultSite={siteKey || BOOKABLE_SITES[0]?.key || ''} onCancel={() => setCreating(false)} />}

      <div className="toolbar">
        <label className="field">
          <span>Site</span>
          <select value={siteKey} onChange={(e) => setSiteKey(e.target.value)}>
            {PUBLIC_SITES.length > 1 && <option value="">Tất cả</option>}
            {PUBLIC_SITES.map((s) => (
              <option key={s.key} value={s.key}>
                {s.label} ({s.key}){s.status === 'active' ? '' : ` — ${s.status}`}
              </option>
            ))}
          </select>
        </label>
      </div>

      <ErrorBanner text={error}>
        <button type="button" className="button small" onClick={() => void load()}>
          Thử lại
        </button>
      </ErrorBanner>

      <div className="card flush">
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Mã vị trí</th>
              <th scope="col">Trạng thái</th>
              <th scope="col" className="hide-sm">Kích thước</th>
              {PUBLIC_SITES.length > 1 && <th scope="col" className="hide-sm">Site</th>}
              <th scope="col" className="hide-sm">Duyệt lúc</th>
            </tr>
          </thead>
          <tbody>
            {items.map((p) => (
              <tr key={p.id}>
                <td>
                  <Link href={`/ads/${p.id}`}>
                    <code>{p.code}</code>
                  </Link>
                  <div className="muted small">{p.label}</div>
                </td>
                <td>
                  <StatusBadge status={p.status} />
                </td>
                <td className="hide-sm small">{sizesSummary(p.sizes)}</td>
                {PUBLIC_SITES.length > 1 && <td className="hide-sm">{siteName(p.siteKey)}</td>}
                <td className="hide-sm muted">{formatTime(p.approvedAt)}</td>
              </tr>
            ))}
            {!loading && items.length === 0 && !error && (
              <tr>
                <td colSpan={5} className="muted center">
                  Chưa có vị trí nào{siteKey ? ` trên ${siteName(siteKey)}` : ''}.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      {loading && <p className="muted">Đang tải…</p>}
    </section>
  );
}

function NewPlacementForm({ defaultSite, onCancel }: { defaultSite: string; onCancel: () => void }) {
  const router = useRouter();
  const [siteKey, setSiteKey] = useState(defaultSite);
  const [draft, setDraft] = useState<PlacementDraft>({ code: '', label: 'Quảng cáo', sizes: [] });
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID());
  const errors = placementDraftErrors(draft, true);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setTouched(true);
    if (Object.keys(errors).length > 0 || !siteKey) return;
    setBusy(true);
    setError(null);
    try {
      const { data } = await apiFetch<AdPlacement>('/admin/ad-placements', {
        method: 'POST',
        body: { siteKey, code: draft.code, label: draft.label.trim(), sizes: sortSizes(draft.sizes) },
        idempotencyKey,
      });
      if (data) router.push(`/ads/${data.id}`);
    } catch (err) {
      setError(adErrorText(err));
      // Lỗi 4xx: request đã bị từ chối, không có tác dụng phụ → khóa mới cho lần gửi đã sửa.
      if (err instanceof ApiError && err.status < 500) setIdempotencyKey(crypto.randomUUID());
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="card stack-sm narrow-form" onSubmit={submit} noValidate>
      <h2>Vị trí mới</h2>
      <p className="muted small">Tạo ở trạng thái nháp. Mã phải trùng mã mà trang của site gọi (ví dụ <code>home_top</code>); cùng mã được dùng ở site khác.</p>
      <div className="grid-2">
        <label className="field">
          <span>Site</span>
          <select value={siteKey} onChange={(e) => setSiteKey(e.target.value)}>
            {BOOKABLE_SITES.map((s) => (
              <option key={s.key} value={s.key}>
                {s.label} ({s.key})
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Mã vị trí</span>
          <input value={draft.code} onChange={(e) => setDraft((d) => ({ ...d, code: e.target.value.trim() }))} maxLength={80} placeholder="home_top"
            aria-invalid={touched && !!errors.code} />
          {touched && errors.code && <span className="field-error">{errors.code}</span>}
        </label>
      </div>
      <label className="field">
        <span>Nhãn hiển thị cạnh banner</span>
        <input value={draft.label} onChange={(e) => setDraft((d) => ({ ...d, label: e.target.value }))} maxLength={150} aria-invalid={touched && !!errors.label} />
        {touched && errors.label && <span className="field-error">{errors.label}</span>}
      </label>
      <SizesEditor sizes={draft.sizes} onChange={(sizes) => setDraft((d) => ({ ...d, sizes }))} />
      {touched && errors.sizes && <span className="field-error">{errors.sizes}</span>}
      <ErrorBanner text={error} />
      <div className="row-end">
        <button type="button" className="button" onClick={onCancel} disabled={busy}>
          Hủy
        </button>
        <button type="submit" className="button primary" disabled={busy}>
          {busy ? 'Đang lưu…' : 'Tạo vị trí'}
        </button>
      </div>
    </form>
  );
}

// ---------------------------------------------------------------- chi tiết vị trí + đặt chỗ

export function AdPlacementDetailPage({ placementId }: { placementId: string }) {
  return (
    <RequirePermission permission="ads.manage">
      <PlacementDetail placementId={placementId} />
    </RequirePermission>
  );
}

function PlacementDetail({ placementId }: { placementId: string }) {
  const [placement, setPlacement] = useState<AdPlacement | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [bookings, setBookings] = useState<AdBooking[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  // Mỗi lần tải lại từ API (kể cả sau 412) dựng lại form từ bản mới nhất; lưu thành công thì form tự cập nhật.
  const [loads, setLoads] = useState(0);

  const reload = useCallback(async () => {
    setLoadError(null);
    try {
      const p = await apiFetch<AdPlacement>(`/admin/ad-placements/${encodeURIComponent(placementId)}`);
      const b = await apiFetch<ItemList<AdBooking>>(`/admin/ad-placements/${encodeURIComponent(placementId)}/bookings`);
      setPlacement(p.data);
      setEtag(p.etag);
      setBookings(b.data?.items ?? []);
      setLoads((n) => n + 1);
    } catch (e) {
      setLoadError(e instanceof ApiError && e.status === 404 ? 'Không tìm thấy vị trí này.' : adErrorText(e));
    }
  }, [placementId]);

  useEffect(() => void reload(), [reload]);

  if (loadError) {
    return (
      <section className="stack ads">
        <Link href="/ads" className="back">
          ← Quảng cáo
        </Link>
        <ErrorBanner text={loadError} />
      </section>
    );
  }
  if (!placement) return <p className="muted">Đang tải…</p>;

  return (
    <section className="stack ads">
      <Link href="/ads" className="back">
        ← Quảng cáo
      </Link>
      <div className="title-row">
        <h1>
          <code>{placement.code}</code>
        </h1>
        <StatusBadge status={placement.status} />
        <span className="muted">{siteName(placement.siteKey)}</span>
      </div>
      <DraftNotice />
      <PlacementEditor
        key={`${placement.id}:${loads}`}
        placement={placement}
        etag={etag}
        bookings={bookings}
        onSaved={(p, newEtag) => {
          setPlacement(p);
          setEtag(newEtag);
        }}
        reload={reload}
      />
      <BookingsCard placement={placement} bookings={bookings} />
    </section>
  );
}

function PlacementEditor({ placement, etag, bookings, onSaved, reload }: {
  placement: AdPlacement;
  etag: string | null;
  bookings: AdBooking[];
  onSaved: (p: AdPlacement, etag: string | null) => void;
  reload: () => Promise<void>;
}) {
  const saved: PlacementDraft = { code: placement.code, label: placement.label, sizes: placement.sizes };
  const [draft, setDraft] = useState<PlacementDraft>(saved);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ text: string; conflict: boolean } | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const errors = placementDraftErrors(draft, false);
  const dirty = JSON.stringify(placementUpdateBody(draft, placement.status)) !== JSON.stringify(placementUpdateBody(saved, placement.status));
  const expired = placement.status === 'EXPIRED';

  const put = async (body: Record<string, unknown>, done: string) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const response = await apiFetch<AdPlacement>(`/admin/ad-placements/${encodeURIComponent(placement.id)}`, { method: 'PUT', body, ifMatch: etag ?? undefined });
      if (response.data) {
        onSaved(response.data, response.etag);
        setDraft({ code: response.data.code, label: response.data.label, sizes: response.data.sizes });
      }
      setNotice(done);
    } catch (e) {
      setError({ text: adErrorText(e), conflict: e instanceof ApiError && e.code === 'REVISION_CONFLICT' });
    } finally {
      setBusy(false);
    }
  };

  const save = (e: FormEvent) => {
    e.preventDefault();
    if (Object.keys(errors).length > 0) return;
    void put(placementUpdateBody(draft, placement.status), 'Đã lưu vị trí.');
  };

  return (
    <div className="stack">
      {error && (
        <ErrorBanner text={error.text}>
          {error.conflict && (
            <button type="button" className="button small" onClick={() => void reload()}>
              Tải lại
            </button>
          )}
        </ErrorBanner>
      )}
      {notice && <div className="banner ok" role="status">{notice}</div>}
      <div className="grid wide">
        <form className="card stack-sm" onSubmit={save} noValidate>
          <h2>Vị trí</h2>
          <label className="field">
            <span>Mã vị trí</span>
            <input value={draft.code} onChange={(e) => setDraft((d) => ({ ...d, code: e.target.value.trim() }))} maxLength={80} disabled={expired}
              aria-invalid={!!errors.code} />
            {errors.code && <span className="field-error">{errors.code}</span>}
          </label>
          <label className="field">
            <span>Nhãn hiển thị cạnh banner</span>
            <input value={draft.label} onChange={(e) => setDraft((d) => ({ ...d, label: e.target.value }))} maxLength={150} disabled={expired}
              aria-invalid={!!errors.label} />
            {errors.label && <span className="field-error">{errors.label}</span>}
          </label>
          <SizesEditor sizes={draft.sizes} onChange={(sizes) => setDraft((d) => ({ ...d, sizes }))} inUse={sizesInUse(bookings)} disabled={expired} />
          {errors.sizes && <span className="field-error">{errors.sizes}</span>}
          {placement.status === 'ACTIVE' && draft.code !== placement.code && (
            <p className="warn-text small">Đổi mã của vị trí đang chạy: trang của site gọi theo mã cũ sẽ không còn thấy quảng cáo này.</p>
          )}
          {!expired && (
            <div className="row-end">
              {dirty && (
                <button type="button" className="button" onClick={() => setDraft(saved)} disabled={busy}>
                  Bỏ thay đổi
                </button>
              )}
              <button type="submit" className="button primary" disabled={busy || !dirty || Object.keys(errors).length > 0}>
                {busy ? 'Đang lưu…' : 'Lưu vị trí'}
              </button>
            </div>
          )}
        </form>

        <div className="stack">
          <div className="card stack-sm">
            <h2>Trạng thái</h2>
            <StatusPanel
              status={placement.status}
              busy={busy}
              blockedReason={dirty ? 'Lưu hoặc bỏ thay đổi trước khi đổi trạng thái.' : null}
              approveNote="Duyệt và mở vị trí cần người có quyền duyệt quảng cáo."
              onChange={(to) => void put(placementUpdateBody(saved, to), `Đã chuyển sang “${adStatusLabel(to)}”.`)}
            />
          </div>
          <div className="card">
            <h2>Thông tin</h2>
            <dl className="facts">
              <dt>Site</dt>
              <dd>
                {siteName(placement.siteKey)} <code>{placement.siteKey}</code>
              </dd>
              <dt>Người duyệt</dt>
              <dd>{placement.approvedBy ? <code>{placement.approvedBy}</code> : '—'}</dd>
              <dt>Duyệt lúc</dt>
              <dd>{formatTime(placement.approvedAt)}</dd>
              <dt>Phiên bản</dt>
              <dd>{placement.rowVersion}</dd>
            </dl>
          </div>
        </div>
      </div>
    </div>
  );
}

function BookingsCard({ placement, bookings }: { placement: AdPlacement; bookings: AdBooking[] }) {
  const canBook = placement.status !== 'EXPIRED';
  return (
    <div className="card stack-sm">
      <div className="title-row spread">
        <div>
          <h2>Đặt chỗ</h2>
          <p className="muted small">
            Mỗi breakpoint hiện một banner: đặt chỗ đang chạy có ưu tiên cao nhất, rồi bắt đầu muộn nhất. Không xoay vòng.
          </p>
        </div>
        {canBook && (
          <Link href={`/ads/${placement.id}/bookings/new`} className="button primary small">
            Thêm đặt chỗ
          </Link>
        )}
      </div>
      <div className="flush-table">
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Nhà quảng cáo</th>
              <th scope="col">Trạng thái</th>
              <th scope="col">Ưu tiên</th>
              <th scope="col" className="hide-sm">Lịch chạy</th>
              <th scope="col" className="hide-sm">Banner</th>
            </tr>
          </thead>
          <tbody>
            {bookings.map((b) => (
              <tr key={b.id}>
                <td>
                  <Link href={`/ads/${placement.id}/bookings/${b.id}`}>{b.advertiserName || '(chưa đặt tên)'}</Link>
                  {isLiveNow(placement, b) && <span className="badge ok small-badge">Đang hiện*</span>}
                  {b.targetUrl && <div className="muted small break">{b.targetUrl}</div>}
                </td>
                <td>
                  <StatusBadge status={b.status} />
                </td>
                <td>{b.priority}</td>
                <td className="hide-sm small">{scheduleText(b)}</td>
                <td className="hide-sm">
                  <span className="chips">
                    {b.creatives.map((c) => (
                      <span key={c.breakpoint} className={c.creativeUrl ? 'chip' : 'chip warn'} title={c.creativeUrl ? 'Ảnh phục vụ được' : 'Ảnh chưa phục vụ được'}>
                        {breakpointLabel(c.breakpoint)} {formatSize(c)}
                      </span>
                    ))}
                  </span>
                </td>
              </tr>
            ))}
            {bookings.length === 0 && (
              <tr>
                <td colSpan={5} className="muted center">
                  Chưa có đặt chỗ nào.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      {bookings.some((b) => isLiveNow(placement, b)) && (
        <p className="muted small">* Vị trí và đặt chỗ đang chạy, trong lịch, có ảnh phục vụ được — vẫn cần cờ ads.display_slots bật và không bị đặt chỗ ưu tiên cao hơn che.</p>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- đặt chỗ: tạo + sửa

export function AdBookingPage({ placementId, bookingId }: { placementId: string; bookingId: string }) {
  return (
    <RequirePermission permission="ads.manage">
      <BookingScreen placementId={placementId} bookingId={bookingId} />
    </RequirePermission>
  );
}

function BookingScreen({ placementId, bookingId }: { placementId: string; bookingId: string }) {
  const isNew = bookingId === 'new';
  const router = useRouter();
  const staff = useStaff();
  const canUpload = access(staff, 'media.upload') === 'allowed';
  const [placement, setPlacement] = useState<AdPlacement | null>(null);
  const [booking, setBooking] = useState<AdBooking | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [draft, setDraft] = useState<BookingDraft | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ text: string; conflict: boolean } | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [touched, setTouched] = useState(false);
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID());

  const reload = useCallback(async () => {
    setLoadError(null);
    try {
      const p = await apiFetch<AdPlacement>(`/admin/ad-placements/${encodeURIComponent(placementId)}`);
      if (!p.data) return;
      setPlacement(p.data);
      if (isNew) {
        setDraft(emptyBookingDraft(p.data));
        return;
      }
      const b = await apiFetch<AdBooking>(`/admin/ad-bookings/${encodeURIComponent(bookingId)}`);
      if (!b.data) return;
      if (b.data.placementId !== p.data.id) {
        setLoadError('Đặt chỗ này thuộc vị trí khác.');
        return;
      }
      setBooking(b.data);
      setEtag(b.etag);
      setDraft(draftFromBooking(b.data, p.data));
      setTouched(false);
    } catch (e) {
      setLoadError(e instanceof ApiError && e.status === 404 ? 'Không tìm thấy vị trí hoặc đặt chỗ này.' : adErrorText(e));
    }
  }, [bookingId, isNew, placementId]);

  useEffect(() => void reload(), [reload]);

  const back = (
    <Link href={`/ads/${placementId}`} className="back">
      ← {placement ? <code>{placement.code}</code> : 'Vị trí'}
    </Link>
  );
  if (loadError) {
    return (
      <section className="stack ads">
        {back}
        <ErrorBanner text={loadError} />
      </section>
    );
  }
  if (!placement || !draft) return <p className="muted">Đang tải…</p>;

  const status: AdStatus = booking?.status ?? 'DRAFT';
  const locked = isContentLocked(status);
  const errors = bookingDraftErrors(draft, placement, status);
  const savedDraft = booking ? draftFromBooking(booking, placement) : null;
  const dirty = isNew || JSON.stringify(draft) !== JSON.stringify(savedDraft);
  const setCreative = (bp: AdBreakpoint, patch: Partial<CreativeDraft>) =>
    setDraft((d) => (d ? { ...d, creatives: { ...d.creatives, [bp]: { ...d.creatives[bp], ...patch } } } : d));

  const applied = (b: AdBooking, newEtag: string | null, done: string) => {
    setBooking(b);
    setEtag(newEtag);
    setDraft(draftFromBooking(b, placement));
    setTouched(false);
    setNotice(done);
  };

  const fail = (e: unknown) => setError({ text: adErrorText(e), conflict: e instanceof ApiError && e.code === 'REVISION_CONFLICT' });

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setTouched(true);
    setNotice(null);
    // Đã duyệt/đang chạy: chỉ ưu tiên đổi được, nên chỉ kiểm ô ưu tiên.
    if (locked ? !!errors.priority : Object.keys(errors).length > 0) return;
    setBusy(true);
    setError(null);
    try {
      if (isNew) {
        const { data } = await apiFetch<AdBooking>(`/admin/ad-placements/${encodeURIComponent(placementId)}/bookings`, {
          method: 'POST',
          body: bookingBody(draft),
          idempotencyKey,
        });
        if (data) router.replace(`/ads/${placementId}/bookings/${data.id}`);
        return;
      }
      const body = locked ? bookingStatusBody(booking!, status, Number(draft.priority)) : { status, ...bookingBody(draft) };
      const response = await apiFetch<AdBooking>(`/admin/ad-bookings/${encodeURIComponent(bookingId)}`, { method: 'PUT', body, ifMatch: etag ?? undefined });
      if (response.data) applied(response.data, response.etag, 'Đã lưu đặt chỗ.');
    } catch (err) {
      fail(err);
      if (isNew && err instanceof ApiError && err.status < 500) setIdempotencyKey(crypto.randomUUID());
    } finally {
      setBusy(false);
    }
  };

  const changeStatus = async (to: AdStatus) => {
    if (!booking) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const response = await apiFetch<AdBooking>(`/admin/ad-bookings/${encodeURIComponent(booking.id)}`, {
        method: 'PUT',
        body: bookingStatusBody(booking, to),
        ifMatch: etag ?? undefined,
      });
      if (response.data) applied(response.data, response.etag, `Đã chuyển sang “${adStatusLabel(to)}”.`);
    } catch (err) {
      fail(err);
    } finally {
      setBusy(false);
    }
  };

  const show = (key: string) => (touched ? errors[key] : undefined);

  return (
    <section className="stack ads">
      {back}
      <div className="title-row">
        <h1>{isNew ? 'Đặt chỗ mới' : booking?.advertiserName || 'Đặt chỗ'}</h1>
        {booking && <StatusBadge status={booking.status} />}
      </div>
      <DraftNotice />
      {booking && locked && status !== 'EXPIRED' && (
        <div className="banner warn" role="status">
          Đặt chỗ {adStatusLabel(status).toLowerCase()}: chỉ đổi được ưu tiên. Muốn sửa banner, link hoặc lịch,{' '}
          {status === 'ACTIVE' ? 'tạm dừng rồi chuyển về nháp' : 'chuyển về nháp'} — sau đó phải duyệt lại.
        </div>
      )}

      <form className="grid wide" onSubmit={save} noValidate>
        <div className="stack">
          <div className="card stack-sm">
            <h2>Banner theo màn hình</h2>
            <p className="muted small">Mỗi màn hình tối đa một banner; kích thước lấy từ kích thước cho phép của vị trí ({sizesSummary(placement.sizes)}).</p>
            {show('creatives') && <span className="field-error">{show('creatives')}</span>}
            {AD_BREAKPOINTS.map((bp) => (
              <CreativeEditor
                key={bp}
                breakpoint={bp}
                placement={placement}
                value={draft.creatives[bp]}
                saved={booking?.creatives.find((c) => c.breakpoint === bp) ?? null}
                disabled={locked || busy}
                canUpload={canUpload}
                errors={{ size: show(`creatives.${bp}.size`), media: show(`creatives.${bp}.media`), altText: show(`creatives.${bp}.altText`) }}
                onChange={(patch) => setCreative(bp, patch)}
              />
            ))}
          </div>
        </div>

        <div className="stack">
          <div className="card stack-sm">
            <h2>Liên kết & lịch</h2>
            <label className="field">
              <span>Nhà quảng cáo</span>
              <input value={draft.advertiserName} onChange={(e) => setDraft({ ...draft, advertiserName: e.target.value })} maxLength={180} disabled={locked || busy} />
            </label>
            <label className="field">
              <span>Link đích (https://)</span>
              <input type="url" inputMode="url" value={draft.targetUrl} onChange={(e) => setDraft({ ...draft, targetUrl: e.target.value })} maxLength={2000}
                placeholder="https://" disabled={locked || busy} aria-invalid={!!show('targetUrl')} />
              {show('targetUrl') && <span className="field-error">{show('targetUrl')}</span>}
            </label>
            <div className="grid-2">
              <label className="field">
                <span>Bắt đầu</span>
                <input type="datetime-local" value={draft.startsAt} onChange={(e) => setDraft({ ...draft, startsAt: e.target.value })} disabled={locked || busy} />
              </label>
              <label className="field">
                <span>Kết thúc</span>
                <input type="datetime-local" value={draft.endsAt} onChange={(e) => setDraft({ ...draft, endsAt: e.target.value })} disabled={locked || busy}
                  aria-invalid={!!show('endsAt')} />
                {show('endsAt') && <span className="field-error">{show('endsAt')}</span>}
              </label>
            </div>
            <p className="muted small">Để trống: chạy ngay khi đặt chỗ được mở, không có ngày kết thúc. Giờ theo máy của bạn.</p>
            <label className="field">
              <span>Ưu tiên (0–100, cao thắng)</span>
              <input type="number" min={0} max={100} step={1} value={draft.priority} onChange={(e) => setDraft({ ...draft, priority: e.target.value })}
                disabled={busy || status === 'EXPIRED'} aria-invalid={!!show('priority')} />
              {show('priority') && <span className="field-error">{show('priority')}</span>}
            </label>
            {error && (
              <ErrorBanner text={error.text}>
                {error.conflict && (
                  <button type="button" className="button small" onClick={() => void reload()}>
                    Tải lại
                  </button>
                )}
              </ErrorBanner>
            )}
            {notice && <div className="banner ok" role="status">{notice}</div>}
            {status !== 'EXPIRED' && (
              <div className="row-end">
                {!isNew && dirty && (
                  <button type="button" className="button" disabled={busy} onClick={() => savedDraft && setDraft(savedDraft)}>
                    Bỏ thay đổi
                  </button>
                )}
                <button type="submit" className="button primary" disabled={busy || !dirty}>
                  {busy ? 'Đang lưu…' : isNew ? 'Tạo đặt chỗ (nháp)' : 'Lưu'}
                </button>
              </div>
            )}
          </div>

          {booking && (
            <div className="card stack-sm">
              <h2>Trạng thái</h2>
              <StatusPanel
                status={booking.status}
                busy={busy}
                blockedReason={dirty ? 'Lưu hoặc bỏ thay đổi trước khi đổi trạng thái.' : null}
                approveNote="Duyệt và mở đặt chỗ cần người có quyền duyệt quảng cáo (MFA)."
                onChange={(to) => void changeStatus(to)}
              />
              <dl className="facts">
                <dt>Người duyệt</dt>
                <dd>{booking.approvedBy ? <code>{booking.approvedBy}</code> : '—'}</dd>
                <dt>Duyệt lúc</dt>
                <dd>{formatTime(booking.approvedAt)}</dd>
                <dt>Phiên bản</dt>
                <dd>{booking.rowVersion}</dd>
              </dl>
            </div>
          )}
        </div>
      </form>
    </section>
  );
}

function CreativeEditor({ breakpoint, placement, value, saved, disabled, canUpload, errors, onChange }: {
  breakpoint: AdBreakpoint;
  placement: AdPlacement;
  value: CreativeDraft;
  saved: AdCreative | null;
  disabled: boolean;
  canUpload: boolean;
  errors: { size?: string; media?: string; altText?: string };
  onChange: (patch: Partial<CreativeDraft>) => void;
}) {
  const sizes = sortSizes(placement.sizes.filter((s) => s.breakpoint === breakpoint));
  const [mode, setMode] = useState<'view' | 'pick' | 'upload'>('view');
  const { media } = useMediaStatus(value.enabled && value.mediaAssetId ? value.mediaAssetId : null);
  const preview = saved && saved.mediaAssetId === value.mediaAssetId ? saved.creativeUrl : null;
  const servable = media ? media.status === 'READY' && media.mediaType === 'IMAGE' && media.visibility === 'PUBLIC' && media.rightsStatus === 'CLEARED'
    && (!media.rightsExpiresAt || new Date(media.rightsExpiresAt) > new Date()) : null;
  const id = `creative-${breakpoint}`;

  const pick = (m: MediaStatus) => {
    onChange({ mediaAssetId: m.mediaId, altText: value.altText || (m.fileName ? titleFromFileName(m.fileName) : '') });
    setMode('view');
  };

  if (sizes.length === 0) {
    return (
      <div className="creative">
        <div className="creative-head">
          <strong>{breakpointLabel(breakpoint)}</strong> <span className="muted small">{breakpointRange(breakpoint)}</span>
        </div>
        <p className="muted small">Vị trí chưa có kích thước cho màn hình này — thêm kích thước ở vị trí nếu cần banner.</p>
      </div>
    );
  }

  return (
    <div className="creative">
      <label className="check creative-head">
        <input type="checkbox" checked={value.enabled} disabled={disabled} onChange={(e) => onChange({ enabled: e.target.checked })} aria-controls={id} />
        <span>
          <strong>{breakpointLabel(breakpoint)}</strong> <span className="muted small">{breakpointRange(breakpoint)}</span>
        </span>
      </label>
      {value.enabled && (
        <div className="stack-sm" id={id}>
          <div className="grid-2">
            <label className="field">
              <span>Kích thước</span>
              <select value={value.size} onChange={(e) => onChange({ size: e.target.value })} disabled={disabled} aria-invalid={!!errors.size}>
                {sizes.map((s) => (
                  <option key={sizeKey(s)} value={sizeKey(s)}>
                    {formatSize(s)}
                  </option>
                ))}
              </select>
              {errors.size && <span className="field-error">{errors.size}</span>}
            </label>
            <label className="field">
              <span>Mô tả ảnh (alt)</span>
              <input value={value.altText} onChange={(e) => onChange({ altText: e.target.value })} maxLength={250} disabled={disabled} aria-invalid={!!errors.altText} />
              {errors.altText && <span className="field-error">{errors.altText}</span>}
            </label>
          </div>

          <div className="creative-media">
            {preview && (
              // eslint-disable-next-line @next/next/no-img-element -- ảnh từ site media của Registry, kích thước thật để xem trước
              <img src={preview} alt={value.altText} className="creative-preview" onError={(e) => { e.currentTarget.hidden = true; }} />
            )}
            {value.mediaAssetId ? (
              media ? (
                <div className="stack-xs">
                  <span className="break">{media.fileName ?? <code>{media.mediaId}</code>}</span>
                  <span className="chips">
                    <MediaStatusBadge status={media.status} />
                    <span className={`badge ${media.rightsStatus === 'CLEARED' ? 'ok' : media.rightsStatus === 'UNVERIFIED' ? '' : 'bad'}`}>{rightsLabel(media.rightsStatus)}</span>
                    <span className="badge">{media.visibility === 'PUBLIC' ? 'Công khai' : 'Riêng tư'}</span>
                  </span>
                  {servable === false && (
                    <span className="warn-text small">
                      Ảnh chưa phục vụ được trên site: cần Sẵn sàng + Công khai + quyền đã xác nhận còn hạn. Đặt chỗ vẫn lưu được.
                    </span>
                  )}
                </div>
              ) : (
                <code className="small">{value.mediaAssetId}</code>
              )
            ) : (
              <span className="muted small">Chưa chọn ảnh.</span>
            )}
          </div>
          {errors.media && <span className="field-error">{errors.media}</span>}

          {!disabled && mode === 'pick' && <ImagePicker currentId={value.mediaAssetId} onPick={pick} />}
          {!disabled && mode === 'upload' && (
            <UploadBox accept={IMAGE_ACCEPT} hint="Ảnh JPEG, PNG hoặc WebP đúng kích thước banner." onMedia={(m) => m.status !== 'UPLOADING' && pick(m)} />
          )}
          {!disabled && (
            <div className="row-end">
              {mode !== 'view' && (
                <button type="button" className="button small" onClick={() => setMode('view')}>
                  Đóng
                </button>
              )}
              {mode === 'view' && canUpload && (
                <button type="button" className="button small" onClick={() => setMode('upload')}>
                  Tải ảnh lên
                </button>
              )}
              {mode === 'view' && (
                <button type="button" className="button small" onClick={() => setMode('pick')}>
                  {value.mediaAssetId ? 'Đổi ảnh' : 'Chọn từ thư viện'}
                </button>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** Chọn ảnh (IMAGE) trong thư viện media (WBS-046). Ảnh hỏng không chọn được; quyền/hiển thị hiện kèm để biết banner có phục vụ được không. */
function ImagePicker({ currentId, onPick }: { currentId: string; onPick: (m: MediaStatus) => void }) {
  const [q, setQ] = useState('');
  const [query, setQuery] = useState('');
  const list = useCursorList<MediaStatus>((c) => mediaListQuery({ mediaType: 'IMAGE', rightsStatus: '', visibility: '', q: query }, c, 10), [query]);
  const { load } = list;
  useEffect(() => void load(null), [load]);

  return (
    <div className="stack-sm">
      <div className="toolbar" role="search">
        <label className="field grow">
          <span>Tìm ảnh theo tên tệp</span>
          <input
            value={q}
            onChange={(e) => setQ(e.target.value)}
            maxLength={200}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                setQuery(q);
              }
            }}
          />
        </label>
        <button type="button" className="button small" onClick={() => setQuery(q)}>
          Tìm
        </button>
      </div>
      <ErrorBanner text={list.error} />
      <ul className="picker">
        {list.items.map((m) => (
          <li key={m.mediaId}>
            <span className="picker-name break">{m.fileName ?? m.mediaId}</span>
            <span className="muted small">
              {formatBytes(m.sizeBytes)} · {rightsLabel(m.rightsStatus)} · {m.visibility === 'PUBLIC' ? 'Công khai' : 'Riêng tư'}
            </span>
            <MediaStatusBadge status={m.status} />
            <button type="button" className="button small" disabled={isMediaFailed(m.status) || m.mediaId === currentId} onClick={() => onPick(m)}>
              {m.mediaId === currentId ? 'Đang dùng' : 'Chọn'}
            </button>
          </li>
        ))}
        {!list.loading && list.items.length === 0 && <li className="muted">Không có ảnh nào trong thư viện.</li>}
      </ul>
      {list.cursor && !list.loading && (
        <button type="button" className="button small" onClick={() => void load(list.cursor)}>
          Xem thêm
        </button>
      )}
    </div>
  );
}

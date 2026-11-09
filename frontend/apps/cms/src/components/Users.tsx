'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { ApiError, apiFetch } from '../lib/api';
import { access, permissionLabel, roleLabel, ROLES } from '../lib/rbac';
import { RequirePermission } from './CmsApp';
import { useStaff } from './session';

// Màn Tài khoản (WBS-115) — gọi /api/v1/admin/users* của hợp đồng v0.3. Phân quyền thật do API kiểm.

export interface AdminUser {
  id: string;
  displayName: string;
  status: 'ACTIVE' | 'SUSPENDED' | string;
  roles: string[];
  rowVersion: number;
}

interface AdminUserList {
  items: AdminUser[];
  nextCursor: string | null;
}

const STATUS_LABEL: Record<string, string> = { ACTIVE: 'Đang hoạt động', SUSPENDED: 'Đình chỉ' };

function errorText(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.code === 'REVISION_CONFLICT') return 'Tài khoản vừa được người khác thay đổi. Tải lại để xem bản mới nhất rồi thử lại.';
    if (e.code === 'FORBIDDEN') return e.problem.detail ?? 'Bạn không có quyền thực hiện thao tác này.';
    const fields = e.problem.errors ? Object.values(e.problem.errors).flat().join(' ') : '';
    return [e.problem.detail ?? e.problem.title, fields].filter(Boolean).join(' ');
  }
  return 'Không kết nối được API. Thử lại.';
}

function StatusBadge({ status }: { status: string }) {
  return <span className={status === 'ACTIVE' ? 'badge ok' : 'badge warn'}>{STATUS_LABEL[status] ?? status}</span>;
}

// ---------------------------------------------------------------- danh sách

export function UsersPage() {
  return (
    <RequirePermission permission="users.read">
      <UsersList />
    </RequirePermission>
  );
}

function UsersList() {
  const [q, setQ] = useState('');
  const [status, setStatus] = useState('');
  const [role, setRole] = useState('');
  const [items, setItems] = useState<AdminUser[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(
    async (after: string | null) => {
      setLoading(true);
      setError(null);
      const params = new URLSearchParams({ limit: '20' });
      if (q.trim()) params.set('q', q.trim());
      if (status) params.set('status', status);
      if (role) params.set('role', role);
      if (after) params.set('cursor', after);
      try {
        const { data } = await apiFetch<AdminUserList>(`/admin/users?${params}`);
        setItems((prev) => (after ? [...prev, ...(data?.items ?? [])] : data?.items ?? []));
        setCursor(data?.nextCursor ?? null);
      } catch (e) {
        setError(errorText(e));
      } finally {
        setLoading(false);
      }
    },
    [q, status, role],
  );

  // Đổi bộ lọc trạng thái/vai trò → tải lại ngay; ô tìm kiếm tải khi bấm "Tìm".
  useEffect(() => void load(null), [status, role]);

  const onSearch = (e: FormEvent) => {
    e.preventDefault();
    void load(null);
  };

  return (
    <section className="stack">
      <div>
        <h1>Tài khoản</h1>
        <p className="muted">Tài khoản staff và thành viên. Đình chỉ, kích hoạt lại và cấp vai trò đều được ghi nhật ký kèm lý do.</p>
      </div>

      <form className="toolbar" onSubmit={onSearch} role="search">
        <label className="field grow">
          <span>Tìm</span>
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Tên hiển thị hoặc mã người dùng" maxLength={200} />
        </label>
        <label className="field">
          <span>Trạng thái</span>
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tất cả</option>
            <option value="ACTIVE">{STATUS_LABEL.ACTIVE}</option>
            <option value="SUSPENDED">{STATUS_LABEL.SUSPENDED}</option>
          </select>
        </label>
        <label className="field">
          <span>Vai trò</span>
          <select value={role} onChange={(e) => setRole(e.target.value)}>
            <option value="">Tất cả</option>
            {ROLES.map((r) => (
              <option key={r} value={r}>
                {roleLabel(r)}
              </option>
            ))}
          </select>
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
              <th scope="col">Tên hiển thị</th>
              <th scope="col">Vai trò</th>
              <th scope="col">Trạng thái</th>
            </tr>
          </thead>
          <tbody>
            {items.map((u) => (
              <tr key={u.id}>
                <td>
                  <Link href={`/users/${u.id}`}>{u.displayName}</Link>
                </td>
                <td>
                  <span className="roles">
                    {u.roles.length === 0 ? <span className="muted">Chưa có</span> : u.roles.map((r) => <span key={r} className="badge">{roleLabel(r)}</span>)}
                  </span>
                </td>
                <td>
                  <StatusBadge status={u.status} />
                </td>
              </tr>
            ))}
            {!loading && items.length === 0 && (
              <tr>
                <td colSpan={3} className="muted center">
                  Không có tài khoản nào khớp bộ lọc.
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

// ---------------------------------------------------------------- chi tiết

export function UserDetailPage({ userId }: { userId: string }) {
  return (
    <RequirePermission permission="users.read">
      <UserDetail userId={userId} />
    </RequirePermission>
  );
}

function UserDetail({ userId }: { userId: string }) {
  const staff = useStaff();
  const canManage = access(staff, 'users.manage');
  const isSelf = staff.userId === userId;
  const [user, setUser] = useState<AdminUser | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoadError(null);
    try {
      const response = await apiFetch<AdminUser>(`/admin/users/${encodeURIComponent(userId)}`);
      setUser(response.data);
      setEtag(response.etag);
    } catch (e) {
      setLoadError(e instanceof ApiError && e.status === 404 ? 'Không tìm thấy tài khoản này.' : errorText(e));
    }
  }, [userId]);

  useEffect(() => void reload(), [reload]);

  if (loadError) {
    return (
      <section className="stack">
        <Link href="/users" className="back">
          ← Tài khoản
        </Link>
        <div className="banner error" role="alert">
          {loadError}
        </div>
      </section>
    );
  }

  if (!user) return <p className="muted">Đang tải…</p>;

  const applied = (u: AdminUser, newEtag: string | null) => {
    setUser(u);
    setEtag(newEtag);
  };

  return (
    <section className="stack">
      <Link href="/users" className="back">
        ← Tài khoản
      </Link>
      <div className="title-row">
        <h1>{user.displayName}</h1>
        <StatusBadge status={user.status} />
      </div>

      <div className="card">
        <dl className="facts">
          <dt>Mã người dùng</dt>
          <dd>
            <code>{user.id}</code>
          </dd>
          <dt>Vai trò</dt>
          <dd>{user.roles.length ? user.roles.map(roleLabel).join(', ') : 'Chưa có'}</dd>
        </dl>
      </div>

      {canManage !== 'allowed' ? (
        <div className="banner warn" role="status">
          {canManage === 'mfa-required'
            ? `Thay đổi tài khoản cần quyền "${permissionLabel('users.manage')}" với phiên đã qua MFA. Đăng nhập lại với MFA để chỉnh sửa.`
            : 'Bạn chỉ có quyền xem tài khoản.'}
        </div>
      ) : (
        <div className="grid">
          <RolesEditor user={user} etag={etag} isSelf={isSelf} onSaved={applied} onConflict={reload} />
          <StatusAction user={user} isSelf={isSelf} onDone={applied} />
        </div>
      )}
    </section>
  );
}

function RolesEditor({ user, etag, isSelf, onSaved, onConflict }: {
  user: AdminUser;
  etag: string | null;
  isSelf: boolean;
  onSaved: (u: AdminUser, etag: string | null) => void;
  onConflict: () => Promise<void>;
}) {
  const [selected, setSelected] = useState<string[]>(user.roles);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string; conflict?: boolean } | null>(null);
  useEffect(() => setSelected(user.roles), [user.roles]);

  const changed = selected.slice().sort().join() !== user.roles.slice().sort().join();
  const toggle = (role: string) => setSelected((s) => (s.includes(role) ? s.filter((r) => r !== role) : [...s, role]));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      const response = await apiFetch<AdminUser>(`/admin/users/${encodeURIComponent(user.id)}/roles`, {
        method: 'PUT',
        body: { roles: selected, reason },
        ifMatch: etag ?? undefined,
      });
      if (response.data) onSaved(response.data, response.etag);
      setReason('');
      setMessage({ kind: 'ok', text: 'Đã cập nhật vai trò.' });
    } catch (err) {
      setMessage({ kind: 'error', text: errorText(err), conflict: err instanceof ApiError && err.code === 'REVISION_CONFLICT' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="card stack-sm" onSubmit={submit}>
      <h2>Vai trò</h2>
      <fieldset className="checks">
        <legend className="visually-hidden">Vai trò</legend>
        {ROLES.map((role) => {
          const lockedSelf = isSelf && role === 'ADMIN';
          return (
            <label key={role} className="check">
              <input type="checkbox" checked={selected.includes(role)} onChange={() => toggle(role)} disabled={busy || lockedSelf} />
              <span>
                {roleLabel(role)} <code>{role}</code>
                {lockedSelf && <small className="muted"> — không tự bỏ vai trò quản trị của mình</small>}
              </span>
            </label>
          );
        })}
      </fieldset>
      <label className="field">
        <span>Lý do (bắt buộc, ghi vào nhật ký)</span>
        <textarea value={reason} onChange={(e) => setReason(e.target.value)} minLength={5} maxLength={2000} rows={2} required />
      </label>
      {message && (
        <div className={message.kind === 'ok' ? 'banner ok' : 'banner error'} role={message.kind === 'ok' ? 'status' : 'alert'}>
          <span>{message.text}</span>
          {message.conflict && (
            <button type="button" className="button small" onClick={() => void onConflict().then(() => setMessage(null))}>
              Tải lại
            </button>
          )}
        </div>
      )}
      <div className="row-end">
        <button type="submit" className="button primary" disabled={busy || !changed || reason.trim().length < 5}>
          {busy ? 'Đang lưu…' : 'Lưu vai trò'}
        </button>
      </div>
    </form>
  );
}

function StatusAction({ user, isSelf, onDone }: { user: AdminUser; isSelf: boolean; onDone: (u: AdminUser, etag: string | null) => void }) {
  const suspending = user.status === 'ACTIVE';
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Một khóa idempotency cho mỗi lần thao tác: bấm lại sau lỗi mạng vẫn dùng khóa cũ → không thực hiện hai lần.
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID());

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (suspending && !window.confirm(`Đình chỉ tài khoản "${user.displayName}"? Mọi phiên đăng nhập của tài khoản sẽ bị thu hồi ngay.`)) return;
    setBusy(true);
    setError(null);
    try {
      const response = await apiFetch<AdminUser>(`/admin/users/${encodeURIComponent(user.id)}/${suspending ? 'suspend' : 'reactivate'}`, {
        method: 'POST',
        body: { reason },
        idempotencyKey,
      });
      if (response.data) onDone(response.data, response.etag);
      setReason('');
      setIdempotencyKey(crypto.randomUUID());
    } catch (err) {
      setError(errorText(err));
      // Lỗi nghiệp vụ (4xx) không có tác dụng phụ: lần sửa tiếp theo là thao tác mới.
      if (err instanceof ApiError && err.status < 500) setIdempotencyKey(crypto.randomUUID());
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="card stack-sm" onSubmit={submit}>
      <h2>{suspending ? 'Đình chỉ tài khoản' : 'Kích hoạt lại tài khoản'}</h2>
      <p className="muted">
        {suspending
          ? 'Tài khoản không đăng nhập được nữa và mọi phiên đang mở bị thu hồi ngay.'
          : 'Tài khoản đăng nhập lại được với các vai trò hiện có.'}
      </p>
      {isSelf ? (
        <p className="muted">Bạn không thể đình chỉ hoặc kích hoạt lại tài khoản của chính mình.</p>
      ) : (
        <>
          <label className="field">
            <span>Lý do (bắt buộc, ghi vào nhật ký)</span>
            <textarea value={reason} onChange={(e) => setReason(e.target.value)} minLength={5} maxLength={2000} rows={2} required />
          </label>
          {error && (
            <div className="banner error" role="alert">
              {error}
            </div>
          )}
          <div className="row-end">
            <button type="submit" className={suspending ? 'button danger' : 'button primary'} disabled={busy || reason.trim().length < 5}>
              {busy ? 'Đang xử lý…' : suspending ? 'Đình chỉ' : 'Kích hoạt lại'}
            </button>
          </div>
        </>
      )}
    </form>
  );
}

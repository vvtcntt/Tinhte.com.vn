'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import type { ReactNode } from 'react';
import { SECTIONS, sectionFor } from '../lib/nav';
import { access, hasLockedPermissions, roleLabel, type PermissionKey } from '../lib/rbac';
import { SessionProvider, useSession, useStaff } from './session';

/** Gốc giao diện CMS: kiểm phiên → màn đăng nhập hoặc khung làm việc. */
export function CmsApp({ siteLabel, children }: { siteLabel: string; children: ReactNode }) {
  return (
    <SessionProvider>
      <Gate siteLabel={siteLabel}>{children}</Gate>
    </SessionProvider>
  );
}

function Gate({ siteLabel, children }: { siteLabel: string; children: ReactNode }) {
  const { state, refresh } = useSession();
  if (state.status === 'loading') {
    return (
      <div className="center-screen" aria-busy="true">
        <p className="muted">Đang kiểm tra phiên đăng nhập…</p>
      </div>
    );
  }

  if (state.status === 'anonymous') return <LoginScreen siteLabel={siteLabel} />;

  if (state.status === 'error') {
    return (
      <div className="center-screen">
        <div className="card narrow" role="alert">
          <h1>Không kết nối được API</h1>
          <p className="muted">{state.error.message || 'API không phản hồi.'} Kiểm tra API đang chạy rồi thử lại.</p>
          <button type="button" className="button primary" onClick={() => void refresh()}>
            Thử lại
          </button>
        </div>
      </div>
    );
  }

  return <AppShell siteLabel={siteLabel}>{children}</AppShell>;
}

function LoginScreen({ siteLabel }: { siteLabel: string }) {
  const { signIn } = useSession();
  return (
    <div className="center-screen">
      <div className="card narrow">
        <p className="eyebrow">{siteLabel}</p>
        <h1>Đăng nhập</h1>
        <p className="muted">
          Dùng tài khoản công ty (đăng nhập một lần). Vai trò biên tập, duyệt và xuất bản được quản trị viên cấp trong hệ thống.
        </p>
        <button type="button" className="button primary block" onClick={signIn}>
          Đăng nhập
        </button>
        <p className="fine-print">
          Quyền cao (duyệt, xuất bản, gỡ bài, quản lý tài khoản) yêu cầu xác thực nhiều lớp (MFA).
        </p>
      </div>
    </div>
  );
}

function AppShell({ siteLabel, children }: { siteLabel: string; children: ReactNode }) {
  const staff = useStaff();
  const { logout, logoutError, signIn } = useSession();
  const pathname = usePathname() ?? '/';
  const visible = SECTIONS.filter((s) => s.permission === null || access(staff, s.permission) !== 'denied');

  return (
    <div className="shell">
      <header className="topbar">
        <Link href="/" className="brand">
          {siteLabel}
        </Link>
        <div className="topbar-right">
          <span className="roles" title="Vai trò của bạn">
            {staff.roles.map((r) => (
              <span key={r} className="badge">
                {roleLabel(r)}
              </span>
            ))}
          </span>
          <span className={staff.mfaSatisfied ? 'badge ok' : 'badge warn'}>{staff.mfaSatisfied ? 'MFA' : 'Chưa MFA'}</span>
          <button type="button" className="button" onClick={() => void logout()}>
            Đăng xuất
          </button>
        </div>
      </header>

      <nav className="sidebar" aria-label="Điều hướng CMS">
        <ul>
          {visible.map((s) => {
            const active = sectionFor(pathname)?.href === s.href;
            const locked = s.permission !== null && access(staff, s.permission) === 'mfa-required';
            return (
              <li key={s.href}>
                <Link href={s.href} aria-current={active ? 'page' : undefined} className={active ? 'active' : undefined}>
                  <span>{s.label}</span>
                  {locked && (
                    <span className="lock" title="Cần đăng nhập với MFA">
                      MFA
                    </span>
                  )}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>

      <main className="content">
        {logoutError && (
          <div className="banner error" role="alert">
            {logoutError}
          </div>
        )}
        {!staff.mfaSatisfied && hasLockedPermissions(staff) && (
          <div className="banner warn" role="status">
            <span>Phiên này chưa qua MFA nên các quyền cao đang bị khóa.</span>
            <button type="button" className="button small" onClick={signIn}>
              Đăng nhập lại với MFA
            </button>
          </div>
        )}
        {children}
      </main>
    </div>
  );
}

/** Ẩn nội dung khi phiên không có quyền (chỉ là giao diện — API vẫn kiểm và trả 403). */
export function RequirePermission({ permission, children }: { permission: PermissionKey; children: ReactNode }) {
  const staff = useStaff();
  const { signIn } = useSession();
  const decision = access(staff, permission);
  if (decision === 'allowed') return <>{children}</>;
  return (
    <div className="card" role="alert">
      <h1>{decision === 'mfa-required' ? 'Cần xác thực MFA' : 'Không có quyền truy cập'}</h1>
      <p className="muted">
        {decision === 'mfa-required'
          ? 'Vai trò của bạn có quyền này, nhưng phiên hiện tại chưa qua MFA.'
          : 'Vai trò của bạn không bao gồm quyền cho màn hình này. Liên hệ quản trị viên nếu bạn cần truy cập.'}
      </p>
      {decision === 'mfa-required' && (
        <button type="button" className="button primary" onClick={signIn}>
          Đăng nhập lại với MFA
        </button>
      )}
    </div>
  );
}

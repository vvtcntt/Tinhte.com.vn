'use client';

import { SECTIONS } from '../lib/nav';
import { grantedPermissions, permissionLabel, roleLabel } from '../lib/rbac';
import { RequirePermission } from './CmsApp';
import { useStaff } from './session';

export function Dashboard() {
  const staff = useStaff();
  const granted = grantedPermissions(staff);
  return (
    <section className="stack">
      <div>
        <h1>Tổng quan</h1>
        <p className="muted">Phiên đăng nhập và quyền hiện có của bạn trên CMS.</p>
      </div>

      <div className="grid">
        <div className="card">
          <h2>Phiên làm việc</h2>
          <dl className="facts">
            <dt>Mã người dùng</dt>
            <dd>
              <code>{staff.userId}</code>
            </dd>
            <dt>Vai trò</dt>
            <dd>{staff.roles.map(roleLabel).join(', ')}</dd>
            <dt>Xác thực nhiều lớp</dt>
            <dd>{staff.mfaSatisfied ? 'Đã xác thực' : 'Chưa xác thực'}</dd>
          </dl>
        </div>

        <div className="card">
          <h2>Quyền của bạn</h2>
          <ul className="perm-list">
            {granted.map(({ permission, access }) => (
              <li key={permission}>
                <span>{permissionLabel(permission)}</span>
                <span className={access === 'allowed' ? 'badge ok' : 'badge warn'}>{access === 'allowed' ? 'Có' : 'Cần MFA'}</span>
              </li>
            ))}
          </ul>
        </div>
      </div>
    </section>
  );
}

/** Màn hình giữ chỗ cho các mục theo WBS: kiểm phân quyền và điều hướng trước khi có nội dung thật. */
export function SectionPlaceholder({ href }: { href: string }) {
  const section = SECTIONS.find((s) => s.href === href);
  if (!section) return null;
  const body = (
    <section className="stack">
      <div>
        <h1>{section.label}</h1>
        <p className="muted">{section.summary}</p>
      </div>
      <div className="card empty">
        <p>Chưa có màn hình này. Sẽ làm ở {section.wbs}.</p>
      </div>
    </section>
  );
  return section.permission ? <RequirePermission permission={section.permission}>{body}</RequirePermission> : body;
}

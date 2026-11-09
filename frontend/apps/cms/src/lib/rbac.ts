// Ma trận vai trò → quyền dùng chung với backend (ADR-0004): nguồn duy nhất config/rbac/rbac.json.
// CMS chỉ dùng để ẨN/HIỆN giao diện — quyết định cho phép luôn do API kiểm (401/403 vẫn có thể xảy ra).
import rbacJson from '../../../../../config/rbac/rbac.json' with { type: 'json' };

export type PermissionKey = keyof typeof rbacJson.permissions;
export type RoleKey = keyof typeof rbacJson.roles;
export type Access = 'allowed' | 'mfa-required' | 'denied';

export interface StaffSession {
  userId: string;
  roles: string[];
  mfaSatisfied: boolean;
}

const permissions = rbacJson.permissions as Record<string, { label: string; mfa: boolean }>;
const roles = rbacJson.roles as Record<string, { label: string; permissions: string[] }>;

export const ALL_PERMISSIONS = Object.keys(permissions) as PermissionKey[];

export function roleLabel(role: string): string {
  return roles[role]?.label ?? role;
}

export function permissionLabel(permission: string): string {
  return permissions[permission]?.label ?? permission;
}

export function requiresMfa(permission: string): boolean {
  return permissions[permission]?.mfa === true;
}

/** Cùng logic với PermissionMatrix.Evaluate (C#): thiếu quyền → denied; quyền cao mà phiên chưa MFA → mfa-required. */
export function access(session: Pick<StaffSession, 'roles' | 'mfaSatisfied'>, permission: PermissionKey): Access {
  if (!(permission in permissions)) throw new Error(`Unknown permission '${permission}'`);
  const granted = session.roles.some((r) => roles[r]?.permissions.includes(permission));
  if (!granted) return 'denied';
  return requiresMfa(permission) && !session.mfaSatisfied ? 'mfa-required' : 'allowed';
}

/** Quyền mà vai trò cấp, kèm trạng thái theo MFA — dùng cho trang tổng quan. */
export function grantedPermissions(session: Pick<StaffSession, 'roles' | 'mfaSatisfied'>): { permission: PermissionKey; access: Access }[] {
  return ALL_PERMISSIONS.map((permission) => ({ permission, access: access(session, permission) })).filter((p) => p.access !== 'denied');
}

export function hasLockedPermissions(session: Pick<StaffSession, 'roles' | 'mfaSatisfied'>): boolean {
  return grantedPermissions(session).some((p) => p.access === 'mfa-required');
}

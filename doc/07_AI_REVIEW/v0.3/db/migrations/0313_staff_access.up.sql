-- 0313 — Đăng nhập staff + RBAC: danh tính OIDC, vai trò, lịch sử cấp/thu hồi, phiên phía server
-- WBS-017 (FR-ID-006), WBS-018 (FR-ID-002), FR-ID-004/007 · ADR-0004 · DRAFT chờ DBA + TL duyệt
-- Khớp API v0.3: StaffSession (userId, roles, mfaSatisfied), RoleAssignment (roles, reason), cookie __Host-tt_cms_session.
--
-- Giả định thêm về schema 04 (A-10): tt.users(id, display_name, status) — dùng để tạo user khi staff đăng nhập lần đầu.
-- Danh mục vai trò (CHECK bên dưới) là ĐỀ XUẤT theo FR-ID-002 "author/editor/approver/moderator/admin":
-- BA + TL + PO phải duyệt ma trận role → permission trước sign-off (07_API_IMPLEMENTATION_GUIDE §28).

SELECT tt_migration.require_column('users', 'id');
SELECT tt_migration.require_column('users', 'display_name');
SELECT tt_migration.require_column('users', 'status');

-- 1) Danh tính OIDC: một user có thể liên kết nhiều (issuer, subject). Không lưu mật khẩu — IdP xác thực.
SELECT tt_migration.track_new_table('0313', 'user_identities');
CREATE TABLE IF NOT EXISTS tt.user_identities (
  issuer        varchar(300) NOT NULL,
  subject       varchar(255) NOT NULL,
  user_id       uuid NOT NULL REFERENCES tt.users(id),
  created_at    timestamptz NOT NULL DEFAULT now(),
  last_login_at timestamptz,
  PRIMARY KEY (issuer, subject)
);
SELECT tt_migration.add_index('0313', 'user_identities', 'ix_user_identities_user', 'ON tt.user_identities (user_id)');

-- 2) Vai trò hiện hành. Đổi vai trò = INSERT/DELETE (trigger ghi lịch sử, bắt buộc lý do).
SELECT tt_migration.track_new_table('0313', 'user_roles');
CREATE TABLE IF NOT EXISTS tt.user_roles (
  user_id    uuid NOT NULL REFERENCES tt.users(id),
  role       varchar(32) NOT NULL,
  granted_by uuid REFERENCES tt.users(id),     -- NULL = khởi tạo hệ thống (bootstrap admin)
  granted_at timestamptz NOT NULL DEFAULT now(),
  reason     varchar(500) NOT NULL,
  PRIMARY KEY (user_id, role),
  CONSTRAINT ck_user_roles_role CHECK (role IN ('AUTHOR', 'EDITOR', 'APPROVER', 'MODERATOR', 'ADMIN')),
  CONSTRAINT ck_user_roles_reason CHECK (length(btrim(reason)) > 0)
);

-- 3) Lịch sử cấp/thu hồi vai trò — chỉ thêm (FR-ID-007).
SELECT tt_migration.track_new_table('0313', 'user_role_history');
CREATE TABLE IF NOT EXISTS tt.user_role_history (
  id         bigserial PRIMARY KEY,
  user_id    uuid NOT NULL REFERENCES tt.users(id),
  role       varchar(32) NOT NULL,
  action     varchar(8) NOT NULL CHECK (action IN ('GRANT', 'REVOKE')),
  actor_id   uuid REFERENCES tt.users(id),       -- NULL = hệ thống
  reason     varchar(500) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
SELECT tt_migration.add_index('0313', 'user_role_history', 'ix_user_role_history_user', 'ON tt.user_role_history (user_id, created_at DESC)');
SELECT tt_migration.add_trigger('0313', 'user_role_history', 'trg_user_role_history_append_only',
  'BEFORE UPDATE OR DELETE ON tt.user_role_history FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');

-- Ghi lịch sử tự động. Người thao tác: SET LOCAL tt.actor_id; lý do thu hồi: SET LOCAL tt.reason (bắt buộc).
CREATE OR REPLACE FUNCTION tt.tg_user_roles_history()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_actor uuid := NULLIF(current_setting('tt.actor_id', true), '')::uuid;
  v_reason text;
BEGIN
  IF TG_OP = 'INSERT' THEN
    INSERT INTO tt.user_role_history (user_id, role, action, actor_id, reason)
    VALUES (NEW.user_id, NEW.role, 'GRANT', COALESCE(v_actor, NEW.granted_by), NEW.reason);
    RETURN NEW;
  END IF;
  v_reason := NULLIF(btrim(current_setting('tt.reason', true)), '');
  IF v_reason IS NULL THEN
    PERFORM tt.raise_domain_error('REASON_REQUIRED', format('Revoking role %s requires a reason (SET LOCAL tt.reason)', OLD.role));
  END IF;
  INSERT INTO tt.user_role_history (user_id, role, action, actor_id, reason)
  VALUES (OLD.user_id, OLD.role, 'REVOKE', v_actor, v_reason);
  RETURN OLD;
END $$;
SELECT tt_migration.track_function('0313', 'tg_user_roles_history()');
SELECT tt_migration.add_trigger('0313', 'user_roles', 'trg_user_roles_history',
  'AFTER INSERT OR DELETE ON tt.user_roles FOR EACH ROW EXECUTE FUNCTION tt.tg_user_roles_history()');

-- 4) Phiên staff phía server: cookie chỉ chứa token ngẫu nhiên; DB lưu sha256 → thu hồi tức thì, lộ DB không lộ cookie.
SELECT tt_migration.track_new_table('0313', 'staff_sessions');
CREATE TABLE IF NOT EXISTS tt.staff_sessions (
  id_hash        char(64) PRIMARY KEY,           -- sha256 hex của token trong cookie
  user_id        uuid NOT NULL REFERENCES tt.users(id),
  issuer         varchar(300) NOT NULL,
  csrf_hash      char(64) NOT NULL,              -- sha256 hex của token X-CSRF-Token (double-submit, ADR-0001 D4)
  mfa_satisfied  boolean NOT NULL,
  amr            text[] NOT NULL DEFAULT '{}',
  auth_time      timestamptz,
  created_at     timestamptz NOT NULL DEFAULT now(),
  last_seen_at   timestamptz NOT NULL DEFAULT now(),
  expires_at     timestamptz NOT NULL,           -- hạn tuyệt đối; hạn idle do ứng dụng so last_seen_at
  revoked_at     timestamptz,
  revoked_reason varchar(40),
  user_agent     varchar(300),
  CONSTRAINT ck_staff_sessions_expiry CHECK (expires_at > created_at),
  CONSTRAINT ck_staff_sessions_revocation CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL))
);
SELECT tt_migration.add_index('0313', 'staff_sessions', 'ix_staff_sessions_user_active',
  'ON tt.staff_sessions (user_id) WHERE revoked_at IS NULL');
SELECT tt_migration.add_index('0313', 'staff_sessions', 'ix_staff_sessions_expiry', 'ON tt.staff_sessions (expires_at)');

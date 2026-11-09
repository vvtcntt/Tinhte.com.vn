-- 0314 — Lịch sử đình chỉ / kích hoạt lại tài khoản (FR-ID-004, FR-ID-007) · WBS-115 · ADR-0004
-- Đổi tt.users.status bắt buộc có lý do (SET LOCAL tt.reason) → ghi tt.user_status_history (chỉ thêm).
-- Người thao tác: SET LOCAL tt.actor_id. Giá trị status giữ như schema 04 (ACTIVE/SUSPENDED theo UserStatusFilter của API v0.3).

SELECT tt_migration.require_column('users', 'id');
SELECT tt_migration.require_column('users', 'status');

SELECT tt_migration.track_new_table('0314', 'user_status_history');
CREATE TABLE IF NOT EXISTS tt.user_status_history (
  id          bigserial PRIMARY KEY,
  user_id     uuid NOT NULL REFERENCES tt.users(id),
  from_status varchar(16),
  to_status   varchar(16) NOT NULL,
  actor_id    uuid REFERENCES tt.users(id),     -- NULL = hệ thống
  reason      varchar(2000) NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now()
);
SELECT tt_migration.add_index('0314', 'user_status_history', 'ix_user_status_history_user',
  'ON tt.user_status_history (user_id, created_at DESC)');
SELECT tt_migration.add_trigger('0314', 'user_status_history', 'trg_user_status_history_append_only',
  'BEFORE UPDATE OR DELETE ON tt.user_status_history FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');

CREATE OR REPLACE FUNCTION tt.tg_users_status_history()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_reason text := NULLIF(btrim(current_setting('tt.reason', true)), '');
BEGIN
  IF v_reason IS NULL THEN
    PERFORM tt.raise_domain_error('REASON_REQUIRED',
      format('Changing account status %s → %s requires a reason (SET LOCAL tt.reason)', OLD.status, NEW.status));
  END IF;
  INSERT INTO tt.user_status_history (user_id, from_status, to_status, actor_id, reason)
  VALUES (NEW.id, OLD.status, NEW.status, NULLIF(current_setting('tt.actor_id', true), '')::uuid, v_reason);
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0314', 'tg_users_status_history()');
SELECT tt_migration.add_trigger('0314', 'users', 'trg_users_status_history',
  'AFTER UPDATE OF status ON tt.users FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status) '
  || 'EXECUTE FUNCTION tt.tg_users_status_history()');

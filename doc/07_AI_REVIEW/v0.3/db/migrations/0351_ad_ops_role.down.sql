-- 0351 down — trả CHECK ck_user_roles_role về 5 vai trò của 0313.
-- TỪ CHỐI nếu còn người đang giữ vai trò AD_OPS (CHECK cũ không tạo lại được). Thu hồi vai trò qua CMS/API trước
-- (lịch sử GRANT/REVOKE trong tt.user_role_history được giữ — bảng đó không có CHECK vai trò).

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM tt.user_roles WHERE role = 'AD_OPS') THEN
    RAISE EXCEPTION 'Refusing to roll back 0351: tt.user_roles still grants AD_OPS. Revoke the role (with a reason) before rolling back.';
  END IF;
END $$;

SELECT tt_migration.rollback('0351');

-- CHECK gốc của 0313 (đã VALIDATED, nằm trong CREATE TABLE nên không thuộc ledger — down 0313 gỡ cùng bảng).
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'tt.user_roles'::regclass AND conname = 'ck_user_roles_role') THEN
    ALTER TABLE tt.user_roles ADD CONSTRAINT ck_user_roles_role CHECK (role IN ('AUTHOR', 'EDITOR', 'APPROVER', 'MODERATOR', 'ADMIN'));
  END IF;
END $$;

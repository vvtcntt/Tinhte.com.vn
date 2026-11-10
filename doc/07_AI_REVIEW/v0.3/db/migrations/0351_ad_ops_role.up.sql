-- 0351 — Vai trò AD_OPS (vận hành quảng cáo, P0-ADS) trong CHECK ck_user_roles_role (ADR-0004; FR-ADS-002; UX-PUB-01 đợt 3)
-- DRAFT chờ BA/PO duyệt ma trận quyền (config/rbac/rbac.json: ads.manage, ads.approve, vai trò AD_OPS) — không coi là đã duyệt.
--
-- Danh mục vai trò phải khớp ba nơi: config/rbac/rbac.json, StaffRoles (C#) và CHECK này (test AuthRbacTests đọc migration
-- mới nhất định nghĩa ck_user_roles_role). 0313 tạo CHECK cùng CREATE TABLE nên không có bản ghi ledger; ở đây thay bằng
-- CHECK cùng tên, thêm dạng NOT VALID (bảng cũ) và VALIDATE ở 0352. Nới danh sách không thể làm dòng cũ vi phạm.
-- Chạy lại an toàn: chỉ thay khi CHECK hiện tại chưa có AD_OPS.

SELECT tt_migration.require_table('user_roles');

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
     WHERE conrelid = 'tt.user_roles'::regclass AND conname = 'ck_user_roles_role'
       AND pg_get_constraintdef(oid) LIKE '%''AD_OPS''%'
  ) THEN
    ALTER TABLE tt.user_roles DROP CONSTRAINT IF EXISTS ck_user_roles_role;
  END IF;
END $$;

SELECT tt_migration.add_constraint('0351', 'user_roles', 'ck_user_roles_role',
  'CHECK (role IN (''AUTHOR'', ''EDITOR'', ''APPROVER'', ''MODERATOR'', ''ADMIN'', ''AD_OPS'')) NOT VALID');

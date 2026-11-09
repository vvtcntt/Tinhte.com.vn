-- 0313 down — TỪ CHỐI nếu đã có lịch sử cấp/thu hồi vai trò (audit FR-ID-007). Xuất dữ liệu + duyệt trước.
-- Phiên staff bị xóa cùng bảng (mọi người dùng CMS phải đăng nhập lại).
SELECT tt_migration.refuse_if_rows('0313', 'user_role_history', 'role grant/revoke audit trail');
SELECT tt_migration.rollback('0313');

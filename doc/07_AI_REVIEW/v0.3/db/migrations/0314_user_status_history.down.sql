-- 0314 down — TỪ CHỐI nếu đã có lịch sử đình chỉ/kích hoạt (audit FR-ID-007). Xuất dữ liệu + duyệt trước.
SELECT tt_migration.refuse_if_rows('0314', 'user_status_history', 'account status audit trail');
SELECT tt_migration.rollback('0314');

-- 0304 down — TỪ CHỐI nếu đã có lịch sử kiểm duyệt (audit). Xuất dữ liệu + duyệt trước.
SELECT tt_migration.refuse_if_rows('0304', 'moderation_case_history', 'moderation audit trail');
SELECT tt_migration.rollback('0304');

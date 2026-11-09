-- 0303 down — TỪ CHỐI nếu đã có yêu cầu takedown (bằng chứng pháp lý). Xuất dữ liệu + Legal duyệt trước.
SELECT tt_migration.refuse_if_rows('0303', 'takedown_requests', 'legal evidence');
SELECT tt_migration.rollback('0303');

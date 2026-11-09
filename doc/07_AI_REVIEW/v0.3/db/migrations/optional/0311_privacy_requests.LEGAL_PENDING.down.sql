-- 0311 down — TỪ CHỐI nếu đã có yêu cầu dữ liệu cá nhân (nghĩa vụ lưu vết theo chính sách Legal).
SELECT tt_migration.refuse_if_rows('0311', 'privacy_requests', 'data subject requests');
SELECT tt_migration.rollback('0311');

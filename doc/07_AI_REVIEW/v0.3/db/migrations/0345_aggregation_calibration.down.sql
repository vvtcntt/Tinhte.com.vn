-- 0345 down — gỡ cột dữ liệu hiệu chỉnh (mất danh sách mục của các lần tách/gộp đã ghi; lịch sử thao tác vẫn còn).
SELECT tt_migration.rollback('0345');

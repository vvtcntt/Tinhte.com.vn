-- 0306 down — gỡ cột/ràng buộc/index/trigger do 0306 tạo (cột có sẵn từ schema 04 được giữ).
SELECT tt_migration.rollback('0306');

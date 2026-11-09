-- 0305 down — gỡ cột/ràng buộc/index do 0305 tạo. Trạng thái quyền media ghi sau 0305 sẽ mất: xuất trước nếu đã dùng thật.
SELECT tt_migration.rollback('0305');

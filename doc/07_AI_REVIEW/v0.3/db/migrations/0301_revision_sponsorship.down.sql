-- 0301 down — chỉ gỡ cột/ràng buộc/index do 0301 tạo (cột có từ v0.2 addendum được giữ).
-- Cảnh báo: dữ liệu tài trợ ghi vào các cột mới sẽ mất — xuất dữ liệu trước nếu đã có nội dung tài trợ thật.
SELECT tt_migration.rollback('0301');

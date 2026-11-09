-- 0312 down — gỡ tt.sites và cột site của nội dung. Cảnh báo: mất gán site của nội dung (xuất trước nếu đã có site nội dung thứ hai).
SELECT tt_migration.rollback('0312');

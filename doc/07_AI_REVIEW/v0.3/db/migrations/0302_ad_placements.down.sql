-- 0302 down — nếu 0302 tạo bảng thì xóa bảng; nếu bảng có từ v0.2 addendum thì chỉ gỡ phần 0302 đã thêm.
SELECT tt_migration.rollback('0302');

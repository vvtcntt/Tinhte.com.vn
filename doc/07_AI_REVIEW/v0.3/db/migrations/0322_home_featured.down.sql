-- 0322 down — gỡ đúng các bảng/trigger/hàm 0322 đã tạo (ledger).
-- TỪ CHỐI nếu đã có lịch sử thay đổi tin nổi bật: đó là dấu vết thao tác biên tập cần lưu (audit).
SELECT tt_migration.refuse_if_rows('0322', 'home_featured_history',
  'home page curation history is audit evidence; export it and obtain approval before rolling back');
SELECT tt_migration.rollback('0322');

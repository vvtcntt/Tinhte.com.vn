-- 0320 down — gỡ đúng các cột/ràng buộc/index/trigger/hàm 0320 đã tạo (ledger).
-- TỪ CHỐI nếu đã có revision được xuất bản qua API mới (published_at): đó là lịch sử sửa bài cần lưu (FR-NEWS-005).
DO $$
BEGIN
  IF tt_migration.has_column('article_revisions', 'published_at')
     AND EXISTS (SELECT 1 FROM tt.article_revisions WHERE published_at IS NOT NULL) THEN
    RAISE EXCEPTION 'Refusing to roll back 0320: tt.article_revisions has published revisions (publish history). Export the data and obtain approval before rolling back.';
  END IF;
END $$;
SELECT tt_migration.rollback('0320');

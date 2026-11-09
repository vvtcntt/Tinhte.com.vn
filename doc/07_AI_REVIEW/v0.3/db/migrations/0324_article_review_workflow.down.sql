-- 0324 down — gỡ đúng các cột/ràng buộc/index/trigger/hàm 0324 đã tạo (ledger).
-- TỪ CHỐI nếu đã có lịch sử duyệt (submitted_at/reviewed_at): đó là bằng chứng quy trình duyệt (FR-CMS-010).
DO $$
BEGIN
  IF tt_migration.has_column('article_revisions', 'submitted_at')
     AND EXISTS (SELECT 1 FROM tt.article_revisions WHERE submitted_at IS NOT NULL OR reviewed_at IS NOT NULL) THEN
    RAISE EXCEPTION 'Refusing to roll back 0324: tt.article_revisions has review history. Export the data and obtain approval before rolling back.';
  END IF;
END $$;
SELECT tt_migration.rollback('0324');

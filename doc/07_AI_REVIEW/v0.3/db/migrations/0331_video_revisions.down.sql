-- 0331 down — gỡ đúng các cột/ràng buộc/index/trigger/hàm 0331 đã tạo (ledger).
-- TỪ CHỐI nếu đã có revision rời DRAFT (đã gửi duyệt/duyệt/public): đó là lịch sử sửa video cần lưu.
DO $$
BEGIN
  IF tt_migration.has_column('video_revisions', 'status')
     AND EXISTS (SELECT 1 FROM tt_migration.ledger WHERE migration = '0331' AND object_type = 'column' AND object_name = 'status')
     AND EXISTS (SELECT 1 FROM tt.video_revisions WHERE status NOT IN ('DRAFT', 'PUBLISHED')) THEN
    RAISE EXCEPTION 'Refusing to roll back 0331: tt.video_revisions has workflow history (status beyond DRAFT). Export the data and obtain approval before rolling back.';
  END IF;
END $$;
SELECT tt_migration.rollback('0331');

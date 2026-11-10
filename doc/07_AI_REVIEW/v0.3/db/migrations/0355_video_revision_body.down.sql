-- 0355 down — gỡ cột body + CHECK + trigger + hàm của 0355 (ledger).
-- TỪ CHỐI nếu đã có revision mang thân khác rỗng: đó là nội dung biên tập (và lịch sử revision bất biến), xuất dữ liệu và xin duyệt trước.
DO $$
BEGIN
  IF tt_migration.has_column('video_revisions', 'body')
     AND EXISTS (SELECT 1 FROM tt_migration.ledger WHERE migration = '0355' AND object_type = 'column' AND object_name = 'body')
     AND EXISTS (SELECT 1 FROM tt.video_revisions WHERE body <> '[]'::jsonb) THEN
    RAISE EXCEPTION 'Refusing to roll back 0355: tt.video_revisions has non-empty video bodies. Export the data and obtain approval before rolling back.';
  END IF;
END $$;
SELECT tt_migration.rollback('0355');

-- 0342 down — gỡ failing_since, trigger row_version theo cột CMS và index mục tin theo site; tạo lại trigger tăng row_version ở mọi
-- UPDATE của 0341 (vẫn thuộc ledger 0341 nên down 0341 gỡ được). Không mất dữ liệu nghiệp vụ (failing_since tính lại được từ lần lỗi sau).
SELECT tt_migration.rollback('0342');

DO $$
BEGIN
  IF to_regclass('tt.agg_feeds') IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM pg_trigger WHERE tgrelid = 'tt.agg_feeds'::regclass AND tgname = 'trg_agg_feeds_touch' AND NOT tgisinternal) THEN
    CREATE TRIGGER trg_agg_feeds_touch BEFORE UPDATE ON tt.agg_feeds FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row();
  END IF;
END $$;

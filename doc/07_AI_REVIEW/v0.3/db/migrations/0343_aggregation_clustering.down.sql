-- 0343 down — gỡ chữ ký MinHash/dải LSH, bộ đếm và covered_at của cụm, trigger row_version theo thao tác CMS; tạo lại trigger tăng row_version
-- ở mọi UPDATE của 0341 (vẫn thuộc ledger 0341). Mất chữ ký (worker tính lại được) và số liệu đếm (tính lại từ mục tin).
SELECT tt_migration.rollback('0343');

DO $$
BEGIN
  IF to_regclass('tt.agg_clusters') IS NOT NULL AND NOT EXISTS (
       SELECT 1 FROM pg_trigger WHERE tgrelid = 'tt.agg_clusters'::regclass AND tgname = 'trg_agg_clusters_touch' AND NOT tgisinternal) THEN
    CREATE TRIGGER trg_agg_clusters_touch BEFORE UPDATE ON tt.agg_clusters FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row();
  END IF;
END $$;

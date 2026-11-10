-- 0346 down — trả cờ aggregation.radar về P2, gỡ cờ aggregation.ai_dedup (TỪ CHỐI nếu đang bật), trả mặc định chu kỳ 15 phút,
-- gỡ category_ids (mất chuyên mục phụ của mục tin) và index xóa mục lẻ. Mục tin đã bị xóa theo L-03 không khôi phục được.
DO $$
BEGIN
  IF to_regclass('tt_migration.flag_changed_0346') IS NULL THEN
    RETURN;
  END IF;
  IF EXISTS (SELECT 1 FROM tt.feature_gates g JOIN tt_migration.flag_changed_0346 a ON a.key = g.key WHERE a.added AND g.enabled) THEN
    RAISE EXCEPTION 'Refusing to roll back 0346: aggregation.ai_dedup is enabled. Turn it off (with approval) first.';
  END IF;
  DELETE FROM tt.feature_gates g USING tt_migration.flag_changed_0346 a WHERE a.key = g.key AND a.added;
  UPDATE tt.feature_gates g SET scope = a.old_scope FROM tt_migration.flag_changed_0346 a WHERE a.key = g.key AND NOT a.added;
END $$;
DROP TABLE IF EXISTS tt_migration.flag_changed_0346;

DO $$
BEGIN
  IF to_regclass('tt.agg_feeds') IS NOT NULL THEN
    ALTER TABLE tt.agg_feeds ALTER COLUMN schedule_minutes SET DEFAULT 15;
  END IF;
END $$;

SELECT tt_migration.rollback('0346');

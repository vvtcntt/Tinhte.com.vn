-- 0300 down — gỡ cột/trigger/bảng audit rồi khôi phục khóa cờ cũ đúng như trước khi chạy 0300.
-- Lưu ý: trạng thái enabled được khôi phục về giá trị lúc chạy 0300 (thay đổi sau đó bị bỏ).
SELECT tt_migration.rollback('0300');

DO $$
BEGIN
  IF to_regclass('tt_migration.flag_key_map_0300') IS NULL THEN
    RETURN;
  END IF;
  DELETE FROM tt.feature_gates g
   WHERE g.key IN (SELECT DISTINCT new_key FROM tt_migration.flag_key_map_0300);
  INSERT INTO tt.feature_gates (key, enabled)
  SELECT old_key, old_enabled FROM tt_migration.flag_key_map_0300 WHERE old_key IS NOT NULL
  ON CONFLICT (key) DO NOTHING;
END $$;

DROP TABLE IF EXISTS tt_migration.flag_key_map_0300;

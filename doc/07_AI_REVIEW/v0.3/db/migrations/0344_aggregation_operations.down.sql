-- 0344 down — gỡ hàng đợi cảnh báo và nhịp tim worker. TỪ CHỐI khi còn cảnh báo chưa gửi (thông báo cho Quản lý nguồn sẽ mất).
DO $$
BEGIN
  IF to_regclass('tt.agg_alerts') IS NOT NULL AND EXISTS (SELECT 1 FROM tt.agg_alerts WHERE delivered_at IS NULL) THEN
    RAISE EXCEPTION 'Refusing to roll back 0344: tt.agg_alerts has undelivered alerts. Deliver or export them first.';
  END IF;
END $$;
SELECT tt_migration.rollback('0344');

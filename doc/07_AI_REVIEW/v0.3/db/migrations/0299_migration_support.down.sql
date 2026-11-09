-- 0299 down — chỉ chạy sau khi mọi migration 0300–0311 đã được down.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM tt_migration.ledger WHERE migration <> '0299') THEN
    RAISE EXCEPTION 'Refusing to roll back 0299: migrations % are still applied',
      (SELECT string_agg(DISTINCT migration, ', ') FROM tt_migration.ledger WHERE migration <> '0299');
  END IF;
END $$;

SELECT tt_migration.rollback('0299');
DROP SCHEMA tt_migration CASCADE;

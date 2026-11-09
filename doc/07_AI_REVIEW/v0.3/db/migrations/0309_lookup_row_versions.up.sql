-- 0309 — row_version cho Category/Topic/AdminUser/MemberProfile để dùng If-Match (AIR-004)
-- MemberProfile giả định nằm trên tt.users (TBD theo schema 04). Bảng không tồn tại → bỏ qua kèm NOTICE.

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['categories', 'topics', 'users'] LOOP
    IF to_regclass(format('tt.%I', t)) IS NULL THEN
      RAISE NOTICE '0309: tt.% not found — skipped', t;
      CONTINUE;
    END IF;
    PERFORM tt_migration.add_column('0309', t, 'row_version', 'bigint NOT NULL DEFAULT 1');
    PERFORM tt_migration.add_trigger('0309', t, 'trg_' || t || '_bump_row_version',
      format('BEFORE UPDATE ON tt.%I FOR EACH ROW EXECUTE FUNCTION tt.tg_bump_row_version()', t));
  END LOOP;
END $$;

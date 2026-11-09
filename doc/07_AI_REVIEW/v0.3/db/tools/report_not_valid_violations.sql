-- Báo cáo số dòng vi phạm từng CHECK còn NOT VALID do migration v0.3 thêm vào.
-- Chạy TRƯỚC 0310 trên bản sao production để lập kế hoạch backfill. Chỉ đọc dữ liệu tt.* (dùng bảng tạm của phiên).
-- Kết quả: (table_name, constraint_name, violating_rows, definition). Bảng rỗng = có thể chạy 0310.

CREATE TEMP TABLE IF NOT EXISTS tt_not_valid_report (
  table_name      text,
  constraint_name text,
  violating_rows  bigint,
  definition      text
);
TRUNCATE tt_not_valid_report;

DO $$
DECLARE
  r       record;
  v_expr  text;
  v_count bigint;
BEGIN
  FOR r IN
    SELECT l.table_name, c.conname, pg_get_constraintdef(c.oid) AS def
      FROM tt_migration.ledger l
      JOIN pg_constraint c ON c.conname = l.object_name AND c.conrelid = format('tt.%I', l.table_name)::regclass
     WHERE l.object_type = 'constraint' AND c.contype = 'c' AND NOT c.convalidated
     ORDER BY l.seq
  LOOP
    v_expr := regexp_replace(r.def, '^CHECK \((.*)\)( NOT VALID)?$', '\1');
    EXECUTE format('SELECT count(*) FROM tt.%I WHERE NOT (%s)', r.table_name, v_expr) INTO v_count;
    IF v_count > 0 THEN
      INSERT INTO tt_not_valid_report VALUES (r.table_name, r.conname, v_count, r.def);
    END IF;
  END LOOP;
END $$;

SELECT * FROM tt_not_valid_report ORDER BY table_name, constraint_name;

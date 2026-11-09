-- 0321 — VALIDATE các CHECK NOT VALID do 0320 thêm (giống 0310 cho 0300–0309).
-- Chạy sau khi backfill dữ liệu cũ của schema 04 (ví dụ slug không đúng định dạng ^[a-z0-9]+(-[a-z0-9]+)*$, title rỗng).
-- VALIDATE chỉ lấy khóa SHARE UPDATE EXCLUSIVE. Dữ liệu vi phạm → dừng, nêu tên ràng buộc; sửa dữ liệu rồi chạy lại.

DO $$
DECLARE r record;
BEGIN
  FOR r IN
    SELECT l.table_name, l.object_name
      FROM tt_migration.ledger l
      JOIN pg_constraint c ON c.conname = l.object_name AND c.conrelid = format('tt.%I', l.table_name)::regclass
     WHERE l.migration = '0320' AND l.object_type = 'constraint' AND NOT c.convalidated
     ORDER BY l.seq
  LOOP
    BEGIN
      EXECUTE format('ALTER TABLE tt.%I VALIDATE CONSTRAINT %I', r.table_name, r.object_name);
    EXCEPTION WHEN check_violation THEN
      RAISE EXCEPTION 'VALIDATE failed for tt.%.%: existing rows violate it — backfill/map data, then rerun 0321 (%)',
        r.table_name, r.object_name, SQLERRM;
    END;
  END LOOP;
END $$;

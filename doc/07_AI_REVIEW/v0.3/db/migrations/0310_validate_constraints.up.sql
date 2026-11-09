-- 0310 — VALIDATE mọi CHECK do 0300–0309 thêm ở dạng NOT VALID (AIR-024)
-- Chạy RIÊNG, sau khi đã backfill/ánh xạ dữ liệu cũ (xem README §Backfill). VALIDATE chỉ lấy khóa
-- SHARE UPDATE EXCLUSIVE: không chặn đọc/ghi thường. Nếu dữ liệu cũ vi phạm, lệnh dừng và nêu tên ràng buộc
-- → sửa dữ liệu rồi chạy lại; không xóa ràng buộc để "cho qua".

DO $$
DECLARE r record;
BEGIN
  FOR r IN
    SELECT l.table_name, l.object_name
      FROM tt_migration.ledger l
      JOIN pg_constraint c ON c.conname = l.object_name AND c.conrelid = format('tt.%I', l.table_name)::regclass
     WHERE l.object_type = 'constraint' AND NOT c.convalidated
     ORDER BY l.seq
  LOOP
    BEGIN
      EXECUTE format('ALTER TABLE tt.%I VALIDATE CONSTRAINT %I', r.table_name, r.object_name);
    EXCEPTION WHEN check_violation THEN
      RAISE EXCEPTION 'VALIDATE failed for tt.%.%: existing rows violate it — backfill/map data, then rerun 0310 (%)',
        r.table_name, r.object_name, SQLERRM;
    END;
  END LOOP;
END $$;

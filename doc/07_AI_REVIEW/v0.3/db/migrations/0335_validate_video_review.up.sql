-- 0335 — VALIDATE các CHECK NOT VALID do 0334 thêm (giống 0310, 0321, 0332).
-- Chạy sau khi backfill dữ liệu duyệt/gỡ cũ của schema 04 (nếu có): cặp submitted_by/at, reviewed_by/at phải đủ cả hai hoặc trống,
-- video đã gỡ phải có lý do. VALIDATE chỉ lấy khóa SHARE UPDATE EXCLUSIVE. Dữ liệu vi phạm → dừng, nêu tên ràng buộc.

DO $$
DECLARE r record;
BEGIN
  FOR r IN
    SELECT l.table_name, l.object_name
      FROM tt_migration.ledger l
      JOIN pg_constraint c ON c.conname = l.object_name AND c.conrelid = format('tt.%I', l.table_name)::regclass
     WHERE l.migration = '0334' AND l.object_type = 'constraint' AND NOT c.convalidated
     ORDER BY l.seq
  LOOP
    BEGIN
      EXECUTE format('ALTER TABLE tt.%I VALIDATE CONSTRAINT %I', r.table_name, r.object_name);
    EXCEPTION WHEN check_violation THEN
      RAISE EXCEPTION 'VALIDATE failed for tt.%.%: existing rows violate it — backfill/map data, then rerun 0335 (%)',
        r.table_name, r.object_name, SQLERRM;
    END;
  END LOOP;
END $$;

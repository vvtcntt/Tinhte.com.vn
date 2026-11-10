-- 0350 down — trả quảng cáo về mô hình một banner mỗi vị trí, mã unique toàn hệ thống (như 0302).
-- Cảnh báo: mất mọi đặt chỗ/banner/kích thước tạo sau 0350 (xuất dữ liệu trước nếu đã dùng thật). Banner v0.2/0302 cũ vẫn còn
-- nguyên trong các cột cũ của tt.ad_placements vì 0350 không xóa chúng.
-- Từ chối nếu cùng một mã đã có trên nhiều site: unique (code) cũ không thể tạo lại.

DO $$
DECLARE v_code text;
BEGIN
  SELECT code INTO v_code FROM tt.ad_placements GROUP BY code HAVING count(*) > 1 LIMIT 1;
  IF v_code IS NOT NULL THEN
    RAISE EXCEPTION 'Refusing to roll back 0350: placement code % exists on several sites; unique (code) cannot be restored. Retire or rename the extra placements first.', v_code;
  END IF;
END $$;

-- Tạo lại đúng những gì 0350 đã gỡ: unique (code) với tên mặc định của CREATE TABLE ... UNIQUE (0302 / addendum v0.2),
-- và hai CHECK của 0302 ở dạng NOT VALID (chạy lại 0310 để VALIDATE). Bản ghi ledger của 0302 vẫn còn, nên down 0302 gỡ được chúng.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'tt.ad_placements'::regclass AND conname = 'ad_placements_code_key') THEN
    ALTER TABLE tt.ad_placements ADD CONSTRAINT ad_placements_code_key UNIQUE (code);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'tt.ad_placements'::regclass AND conname = 'ck_ad_placements_dimensions') THEN
    ALTER TABLE tt.ad_placements ADD CONSTRAINT ck_ad_placements_dimensions
      CHECK (width IS NOT NULL AND height IS NOT NULL AND width BETWEEN 1 AND 4000 AND height BETWEEN 1 AND 4000) NOT VALID;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'tt.ad_placements'::regclass AND conname = 'ck_ad_placements_serving_complete') THEN
    ALTER TABLE tt.ad_placements ADD CONSTRAINT ck_ad_placements_serving_complete
      CHECK (status NOT IN ('APPROVED', 'ACTIVE') OR (media_asset_id IS NOT NULL AND alt_text IS NOT NULL
             AND btrim(alt_text) <> '' AND target_url IS NOT NULL)) NOT VALID;
  END IF;
END $$;

SELECT tt_migration.rollback('0350');

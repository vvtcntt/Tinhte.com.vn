-- 0340 — Danh mục nguồn nội dung + giấy phép (ADR-0007 §1, phần tối thiểu ADR-0008 §2 cần) · DRAFT chờ DBA + TL + Legal duyệt
-- Chỉ tạo hai bảng danh mục. CHƯA làm (theo ADR-0007, chờ duyệt):
--   - API /admin/sources, /admin/licenses (§1); metadata quyền trên bài (§2); xác nhận quyền (§3); kiểm khi publish (§4);
--   - khóa ngoại tt.media_assets.license_id → tt.licenses (bảng cũ: thêm NOT VALID + VALIDATE ở migration riêng khi DBA chốt Q4).
-- Giả định A-14 (README §3): schema 04 CHƯA có bảng nguồn/giấy phép. Nếu đã có (ADR-0007 Q4) thì sửa migration này dùng bảng sẵn có.
-- Có giấy phép trong bảng KHÔNG có nghĩa là đã được phép khai thác nội dung: Legal nhập và chịu trách nhiệm (CLAUDE.md §3).

SELECT tt_migration.require_column('users', 'id');

-- 1) Nguồn nội dung (bài tự viết, hãng tin, đối tác, thông cáo...). Thuộc News/taxonomy; module Aggregation chỉ đọc.
SELECT tt_migration.track_new_table('0340', 'content_sources');
CREATE TABLE IF NOT EXISTS tt.content_sources (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name        varchar(200) NOT NULL,
  kind        varchar(16) NOT NULL DEFAULT 'OTHER',
  url         varchar(2048),
  is_enabled  boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  row_version bigint NOT NULL DEFAULT 1,
  CONSTRAINT ck_content_sources_name_not_blank CHECK (length(btrim(name)) > 0),
  CONSTRAINT ck_content_sources_kind CHECK (kind IN ('ORIGINAL', 'AGENCY', 'PARTNER', 'PRESS_RELEASE', 'OTHER')),
  CONSTRAINT ck_content_sources_url CHECK (url IS NULL OR url ~ '^https?://[^[:space:]]+$')
);
-- Tên nguồn không trùng (không phân biệt hoa thường). add_index chỉ tạo index thường → UNIQUE tự ghi ledger.
DO $$
BEGIN
  IF to_regclass('tt.ux_content_sources_name') IS NULL THEN
    CREATE UNIQUE INDEX ux_content_sources_name ON tt.content_sources (lower(btrim(name)));
    PERFORM tt_migration.record('0340', 'index', 'content_sources', 'ux_content_sources_name');
  END IF;
END $$;
SELECT tt_migration.add_trigger('0340', 'content_sources', 'trg_content_sources_touch',
  'BEFORE UPDATE ON tt.content_sources FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');

-- 2) Giấy phép / thỏa thuận sử dụng nội dung. evidence_ref trỏ tới hợp đồng trong kho tài liệu (không lưu file ở đây).
SELECT tt_migration.track_new_table('0340', 'licenses');
CREATE TABLE IF NOT EXISTS tt.licenses (
  id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  code                 varchar(60) NOT NULL,
  name                 varchar(200) NOT NULL,
  usage_scope          text NOT NULL,
  requires_attribution boolean NOT NULL DEFAULT true,
  valid_from           timestamptz,
  valid_to             timestamptz,
  evidence_ref         varchar(500),
  is_enabled           boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  row_version          bigint NOT NULL DEFAULT 1,
  CONSTRAINT uq_licenses_code UNIQUE (code),
  CONSTRAINT ck_licenses_code CHECK (code ~ '^[A-Z0-9][A-Z0-9_.-]{0,59}$'),
  CONSTRAINT ck_licenses_name_not_blank CHECK (length(btrim(name)) > 0),
  CONSTRAINT ck_licenses_usage_scope_not_blank CHECK (length(btrim(usage_scope)) > 0),
  CONSTRAINT ck_licenses_validity CHECK (valid_from IS NULL OR valid_to IS NULL OR valid_to > valid_from)
);
SELECT tt_migration.add_trigger('0340', 'licenses', 'trg_licenses_touch',
  'BEFORE UPDATE ON tt.licenses FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');

-- Giấy phép đang hiệu lực tại thời điểm p_at (dùng cho kiểm M2/M3 của 0341 và, sau này, kiểm khi publish — ADR-0007 §4).
CREATE OR REPLACE FUNCTION tt.license_active(p_license_id uuid, p_at timestamptz DEFAULT now())
RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT COALESCE((SELECT l.is_enabled AND (l.valid_from IS NULL OR l.valid_from <= p_at) AND (l.valid_to IS NULL OR l.valid_to > p_at)
                     FROM tt.licenses l WHERE l.id = p_license_id), false);
$$;
SELECT tt_migration.track_function('0340', 'license_active(uuid,timestamp with time zone)');

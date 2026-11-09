-- 0302 — Slot quảng cáo theo hợp đồng v0.3 (AIR-013, AIR-024; FR-ADS-001/002)
-- Hai đường đều hội tụ về cùng một trạng thái:
--   (a) DB chưa có tt.ad_placements → tạo phần lõi (giống v0.2) rồi thêm cột v0.3;
--   (b) DB đã áp 07_DB_ADS_LITE_REFERENCE_v0.2.sql → chỉ thêm cột/ràng buộc còn thiếu.
-- width/height để nullable + CHECK NOT VALID: dòng v0.2 cũ cần backfill kích thước trước khi chạy 0310.

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_table('media_assets');

SELECT tt_migration.track_new_table('0302', 'ad_placements');
CREATE TABLE IF NOT EXISTS tt.ad_placements (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  code           varchar(80) NOT NULL UNIQUE,
  label          varchar(150) NOT NULL DEFAULT 'Quảng cáo',
  status         varchar(20) NOT NULL DEFAULT 'DRAFT',
  media_asset_id uuid REFERENCES tt.media_assets(id),
  target_url     text,
  starts_at      timestamptz,
  ends_at        timestamptz,
  approved_by    uuid REFERENCES tt.users(id),
  approved_at    timestamptz,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  row_version    bigint NOT NULL DEFAULT 1
);

SELECT tt_migration.add_column('0302', 'ad_placements', 'advertiser_name', 'varchar(180)');
SELECT tt_migration.add_column('0302', 'ad_placements', 'alt_text', 'varchar(250)');
SELECT tt_migration.add_column('0302', 'ad_placements', 'width', 'integer');
SELECT tt_migration.add_column('0302', 'ad_placements', 'height', 'integer');
SELECT tt_migration.add_column('0302', 'ad_placements', 'created_by', 'uuid REFERENCES tt.users(id)');
SELECT tt_migration.add_column('0302', 'ad_placements', 'updated_by', 'uuid REFERENCES tt.users(id)');

SELECT tt_migration.add_constraint('0302', 'ad_placements', 'ck_ad_placements_status',
  'CHECK (status IN (''DRAFT'', ''APPROVED'', ''ACTIVE'', ''PAUSED'', ''EXPIRED'')) NOT VALID');
SELECT tt_migration.add_constraint('0302', 'ad_placements', 'ck_ad_placements_code_format',
  'CHECK (code ~ ''^[a-z0-9_-]{2,80}$'') NOT VALID');
-- Chặn javascript:, data:, http: — khớp pattern ^https:// của API (mã lỗi UNSAFE_URL).
SELECT tt_migration.add_constraint('0302', 'ad_placements', 'ck_ad_placements_target_https',
  'CHECK (target_url IS NULL OR target_url ~ ''^https://[^[:space:]]+$'') NOT VALID');
-- Kích thước bắt buộc để FE giữ chỗ, tránh CLS.
SELECT tt_migration.add_constraint('0302', 'ad_placements', 'ck_ad_placements_dimensions',
  'CHECK (width IS NOT NULL AND height IS NOT NULL AND width BETWEEN 1 AND 4000 AND height BETWEEN 1 AND 4000) NOT VALID');
SELECT tt_migration.add_constraint('0302', 'ad_placements', 'ck_ad_placements_schedule',
  'CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at) NOT VALID');
-- v0.2 chỉ bắt người duyệt cho ACTIVE; v0.3 bắt cho cả APPROVED.
SELECT tt_migration.add_constraint('0302', 'ad_placements', 'ck_ad_placements_approval',
  'CHECK (status NOT IN (''APPROVED'', ''ACTIVE'') OR (approved_by IS NOT NULL AND approved_at IS NOT NULL)) NOT VALID');
SELECT tt_migration.add_constraint('0302', 'ad_placements', 'ck_ad_placements_serving_complete',
  'CHECK (status NOT IN (''APPROVED'', ''ACTIVE'') OR (media_asset_id IS NOT NULL AND alt_text IS NOT NULL '
  || 'AND btrim(alt_text) <> '''' AND target_url IS NOT NULL)) NOT VALID');

-- Truy vấn phục vụ GET /ads/placements?placementCodes=... (chỉ ACTIVE trong khung giờ).
SELECT tt_migration.add_index('0302', 'ad_placements', 'ix_ad_placements_serving',
  'ON tt.ad_placements (code, starts_at, ends_at) WHERE status = ''ACTIVE''');

SELECT tt_migration.add_trigger('0302', 'ad_placements', 'trg_ad_placements_touch',
  'BEFORE UPDATE ON tt.ad_placements FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');

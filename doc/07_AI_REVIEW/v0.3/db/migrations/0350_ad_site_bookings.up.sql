-- 0350 — Quảng cáo theo site, tách vị trí khỏi đặt chỗ, nhiều kích thước (ADR-0001; FR-ADS-001/002; UX-PUB-01 đợt 3)
-- DRAFT chờ TL/PO/BA duyệt. Hợp đồng: AdPlacement / AdSize / AdBooking / AdCreative / PublicAdSlot (OpenAPI v0.3 §16 changelog).
--
--   tt.ad_placements         VỊ TRÍ: thêm site_key (mặc định 'www'), unique (site_key, code) thay cho unique (code).
--   tt.ad_placement_sizes    kích thước được phép của vị trí theo breakpoint (MOBILE < 768, TABLET 768–1023, DESKTOP ≥ 1024).
--   tt.ad_bookings           ĐẶT CHỖ: link, lịch chạy, ưu tiên, trạng thái, người duyệt.
--   tt.ad_booking_creatives  BANNER của đặt chỗ, tối đa một mỗi breakpoint; kích thước phải thuộc kích thước của vị trí (khóa ngoại).
--
-- Cột banner cũ của tt.ad_placements (media_asset_id, alt_text, target_url, width, height, advertiser_name, starts_at, ends_at)
-- được GIỮ NGUYÊN để down không mất dữ liệu v0.2; API mới không đọc chúng. Gỡ bằng migration sau khi TL duyệt.
-- Hai CHECK của 0302 ràng vị trí với một banner (ck_ad_placements_dimensions, ck_ad_placements_serving_complete) bị gỡ — quy tắc
-- tương ứng nằm trên bảng đặt chỗ/banner. ck_ad_placements_approval GIỮ: mở một vị trí (APPROVED/ACTIVE) vẫn phải có người duyệt.
-- Không thêm CHECK nào lên bảng cũ, nên không cần migration VALIDATE đi kèm.

SELECT tt_migration.require_table('ad_placements');
SELECT tt_migration.require_table('sites');
SELECT tt_migration.require_table('media_assets');
SELECT tt_migration.require_table('users');

-- ---------------------------------------------------------------- vị trí thuộc một site
SELECT tt_migration.add_column('0350', 'ad_placements', 'site_key', 'varchar(31) NOT NULL DEFAULT ''www'' REFERENCES tt.sites(key)');
-- Vị trí chỉ thuộc site public-web chưa retired (dùng lại hàm của 0312).
SELECT tt_migration.add_trigger('0350', 'ad_placements', 'trg_ad_placements_site_kind',
  'BEFORE INSERT OR UPDATE OF site_key ON tt.ad_placements FOR EACH ROW EXECUTE FUNCTION tt.tg_content_site_kind(''site_key'')');

SELECT tt_migration.add_constraint('0350', 'ad_placements', 'uq_ad_placements_site_code', 'UNIQUE (site_key, code)');
-- Unique (code) toàn hệ thống (0302 / addendum v0.2, tên mặc định ad_placements_code_key) chặn cùng mã trên hai site.
-- Down tạo lại đúng tên này (xem .down.sql).
ALTER TABLE tt.ad_placements DROP CONSTRAINT IF EXISTS ad_placements_code_key;

ALTER TABLE tt.ad_placements DROP CONSTRAINT IF EXISTS ck_ad_placements_dimensions;
ALTER TABLE tt.ad_placements DROP CONSTRAINT IF EXISTS ck_ad_placements_serving_complete;

-- ---------------------------------------------------------------- kích thước cho phép theo breakpoint
SELECT tt_migration.track_new_table('0350', 'ad_placement_sizes');
CREATE TABLE IF NOT EXISTS tt.ad_placement_sizes (
  placement_id uuid NOT NULL REFERENCES tt.ad_placements(id) ON DELETE CASCADE,
  breakpoint   varchar(8) NOT NULL CONSTRAINT ck_ad_placement_sizes_breakpoint CHECK (breakpoint IN ('MOBILE', 'TABLET', 'DESKTOP')),
  width        integer NOT NULL CONSTRAINT ck_ad_placement_sizes_width CHECK (width BETWEEN 1 AND 4000),
  height       integer NOT NULL CONSTRAINT ck_ad_placement_sizes_height CHECK (height BETWEEN 1 AND 4000),
  PRIMARY KEY (placement_id, breakpoint, width, height)
);

-- ---------------------------------------------------------------- đặt chỗ
SELECT tt_migration.track_new_table('0350', 'ad_bookings');
CREATE TABLE IF NOT EXISTS tt.ad_bookings (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  placement_id    uuid NOT NULL REFERENCES tt.ad_placements(id),
  status          varchar(20) NOT NULL DEFAULT 'DRAFT',
  priority        smallint NOT NULL DEFAULT 0,
  advertiser_name varchar(180),
  target_url      text,
  starts_at       timestamptz,
  ends_at         timestamptz,
  approved_by     uuid REFERENCES tt.users(id),
  approved_at     timestamptz,
  created_by      uuid REFERENCES tt.users(id),
  updated_by      uuid REFERENCES tt.users(id),
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  row_version     bigint NOT NULL DEFAULT 1,
  -- Đích cho khóa ngoại kép của banner: banner luôn cùng vị trí với đặt chỗ của nó.
  CONSTRAINT uq_ad_bookings_id_placement UNIQUE (id, placement_id),
  CONSTRAINT ck_ad_bookings_status CHECK (status IN ('DRAFT', 'APPROVED', 'ACTIVE', 'PAUSED', 'EXPIRED')),
  CONSTRAINT ck_ad_bookings_priority CHECK (priority BETWEEN 0 AND 100),
  -- Chặn javascript:, data:, http: — khớp pattern ^https:// của API (UNSAFE_URL).
  CONSTRAINT ck_ad_bookings_target_https CHECK (target_url IS NULL OR target_url ~ '^https://[^[:space:]]+$'),
  CONSTRAINT ck_ad_bookings_schedule CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at),
  CONSTRAINT ck_ad_bookings_approval CHECK (status NOT IN ('APPROVED', 'ACTIVE') OR (approved_by IS NOT NULL AND approved_at IS NOT NULL)),
  CONSTRAINT ck_ad_bookings_serving_target CHECK (status NOT IN ('APPROVED', 'ACTIVE') OR target_url IS NOT NULL)
);

SELECT tt_migration.add_trigger('0350', 'ad_bookings', 'trg_ad_bookings_touch',
  'BEFORE UPDATE ON tt.ad_bookings FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');
-- GET /ads/placements: đặt chỗ ACTIVE của các vị trí được hỏi, ưu tiên cao trước.
SELECT tt_migration.add_index('0350', 'ad_bookings', 'ix_ad_bookings_serving',
  'ON tt.ad_bookings (placement_id, priority DESC, starts_at DESC NULLS LAST) WHERE status = ''ACTIVE''');

-- ---------------------------------------------------------------- banner theo breakpoint
SELECT tt_migration.track_new_table('0350', 'ad_booking_creatives');
CREATE TABLE IF NOT EXISTS tt.ad_booking_creatives (
  booking_id     uuid NOT NULL,
  placement_id   uuid NOT NULL,
  breakpoint     varchar(8) NOT NULL,
  width          integer NOT NULL,
  height         integer NOT NULL,
  media_asset_id uuid NOT NULL REFERENCES tt.media_assets(id),
  alt_text       varchar(250) NOT NULL CONSTRAINT ck_ad_booking_creatives_alt CHECK (btrim(alt_text) <> ''),
  PRIMARY KEY (booking_id, breakpoint),
  CONSTRAINT fk_ad_booking_creatives_booking FOREIGN KEY (booking_id, placement_id)
    REFERENCES tt.ad_bookings (id, placement_id) ON DELETE CASCADE,
  -- Kích thước phải là kích thước vị trí cho phép ở breakpoint đó; không gỡ được kích thước còn banner dùng.
  CONSTRAINT fk_ad_booking_creatives_size FOREIGN KEY (placement_id, breakpoint, width, height)
    REFERENCES tt.ad_placement_sizes (placement_id, breakpoint, width, height)
);
SELECT tt_migration.add_index('0350', 'ad_booking_creatives', 'ix_ad_booking_creatives_size',
  'ON tt.ad_booking_creatives (placement_id, breakpoint, width, height)');

-- ---------------------------------------------------------------- chuyển banner v0.2/0302 sang đặt chỗ
-- Mỗi vị trí cũ có media + kích thước → một đặt chỗ cùng id (dễ đối chiếu) và một banner cho cả ba breakpoint (hành vi cũ:
-- cùng banner mọi màn hình). Dữ liệu cũ vi phạm quy tắc mới (CHECK của 0302 là NOT VALID) được hạ về DRAFT thay vì làm hỏng
-- migration: trạng thái lạ hoặc thiếu duyệt/link → DRAFT; lịch ngược → EXPIRED; thiếu alt → dùng label. Chạy lại an toàn (ON CONFLICT DO NOTHING).
INSERT INTO tt.ad_placement_sizes (placement_id, breakpoint, width, height)
SELECT p.id, bp.breakpoint, p.width, p.height
  FROM tt.ad_placements p
 CROSS JOIN (VALUES ('MOBILE'), ('TABLET'), ('DESKTOP')) AS bp(breakpoint)
 WHERE p.media_asset_id IS NOT NULL AND p.width BETWEEN 1 AND 4000 AND p.height BETWEEN 1 AND 4000
ON CONFLICT DO NOTHING;

INSERT INTO tt.ad_bookings (id, placement_id, status, advertiser_name, target_url, starts_at, ends_at,
                            approved_by, approved_at, created_by, updated_by, created_at)
SELECT p.id, p.id,
       CASE
         WHEN p.status NOT IN ('DRAFT', 'APPROVED', 'ACTIVE', 'PAUSED', 'EXPIRED') THEN 'DRAFT'
         WHEN p.starts_at IS NOT NULL AND p.ends_at IS NOT NULL AND p.ends_at <= p.starts_at THEN 'EXPIRED'
         WHEN p.status IN ('APPROVED', 'ACTIVE')
              AND (p.approved_by IS NULL OR p.approved_at IS NULL OR p.target_url IS NULL
                   OR p.target_url !~ '^https://[^[:space:]]+$') THEN 'DRAFT'
         ELSE p.status
       END,
       p.advertiser_name,
       CASE WHEN p.target_url ~ '^https://[^[:space:]]+$' THEN p.target_url END,
       p.starts_at,
       CASE WHEN p.ends_at > p.starts_at OR p.starts_at IS NULL THEN p.ends_at END,
       p.approved_by, p.approved_at, p.created_by, p.updated_by, p.created_at
  FROM tt.ad_placements p
 WHERE p.media_asset_id IS NOT NULL AND p.width BETWEEN 1 AND 4000 AND p.height BETWEEN 1 AND 4000
ON CONFLICT (id) DO NOTHING;

INSERT INTO tt.ad_booking_creatives (booking_id, placement_id, breakpoint, width, height, media_asset_id, alt_text)
SELECT p.id, p.id, bp.breakpoint, p.width, p.height, p.media_asset_id,
       COALESCE(NULLIF(btrim(p.alt_text), ''), p.label)
  FROM tt.ad_placements p
  JOIN tt.ad_bookings b ON b.id = p.id AND b.placement_id = p.id
 CROSS JOIN (VALUES ('MOBILE'), ('TABLET'), ('DESKTOP')) AS bp(breakpoint)
 WHERE p.media_asset_id IS NOT NULL AND p.width BETWEEN 1 AND 4000 AND p.height BETWEEN 1 AND 4000
ON CONFLICT DO NOTHING;

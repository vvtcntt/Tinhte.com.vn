-- TINHTE.COM.VN — Addendum P0-ADS / reference only, NOT a production migration.
-- Prerequisite: 04_DATABASE_SCHEMA_REFERENCE_v0.1.sql (tt schema and users/media_assets).
-- Do not run on live DB without DBA review, snapshot/rollback, test and deployment approval.

BEGIN;

-- Sponsor metadata belongs on immutable content revisions to preserve editorial history.
ALTER TABLE tt.article_revisions
  ADD COLUMN IF NOT EXISTS content_kind varchar(16) NOT NULL DEFAULT 'EDITORIAL',
  ADD COLUMN IF NOT EXISTS sponsor_name varchar(180),
  ADD COLUMN IF NOT EXISTS sponsor_reference varchar(120);

ALTER TABLE tt.video_revisions
  ADD COLUMN IF NOT EXISTS content_kind varchar(16) NOT NULL DEFAULT 'EDITORIAL',
  ADD COLUMN IF NOT EXISTS sponsor_name varchar(180),
  ADD COLUMN IF NOT EXISTS sponsor_reference varchar(120);

-- Check constraints are added to a new versioned migration after reviewing current data.
-- Approval + legal/rights + non-empty sponsor_name for SPONSORED MUST be validated
-- in the domain transaction BEFORE switching public_revision_id.

CREATE TABLE IF NOT EXISTS tt.ad_placements (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  code varchar(80) NOT NULL UNIQUE,
  label varchar(150) NOT NULL DEFAULT 'Quảng cáo',
  status varchar(20) NOT NULL DEFAULT 'DRAFT'
    CHECK (status IN ('DRAFT','APPROVED','ACTIVE','PAUSED','EXPIRED')),
  media_asset_id uuid REFERENCES tt.media_assets(id),
  target_url text,
  starts_at timestamptz,
  ends_at timestamptz,
  approved_by uuid REFERENCES tt.users(id),
  approved_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  row_version bigint NOT NULL DEFAULT 1 CHECK (row_version > 0),
  CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at),
  CHECK (status NOT IN ('ACTIVE') OR (approved_by IS NOT NULL AND approved_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_ad_placements_active
  ON tt.ad_placements(status,starts_at,ends_at);

INSERT INTO tt.feature_gates(key,enabled) VALUES
  ('sponsored_content_publication',false),
  ('display_ad_slots',false),
  ('shorts_instream_ads',false)
ON CONFLICT (key) DO NOTHING;

COMMIT;

-- PRE-PRODUCTION VERIFICATION (separate):
-- * enforce valid content_kind enum/check on both revisions;
-- * ensure release feature flags synchronized between API and DB;
-- * test concurrent publish + sponsor metadata rights validity;
-- * audit every placement and contentKind change;
-- * test removed content no longer appears via CDN/search/ads;
-- * rollback/backup/permission tests before migration promotion.

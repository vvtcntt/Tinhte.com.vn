-- 0305 — Quyền và hiển thị của media (AIR-018; FR-CMS-009, FR-VID-005/010)
-- Khớp API v0.3 MediaStatus: mediaType, visibility, rightsStatus, licenseId, rightsExpiresAt.
-- Bất biến: media PUBLIC phải có rights CLEARED ("asset private/expired/unlicensed không gắn vào bản công khai").
-- license_id chưa có FK vì bảng giấy phép thuộc schema 04 chưa xác định (TBD với DBA).

SELECT tt_migration.require_column('media_assets', 'id');

SELECT tt_migration.add_column('0305', 'media_assets', 'media_type', 'varchar(10)');
SELECT tt_migration.add_column('0305', 'media_assets', 'visibility', 'varchar(8) NOT NULL DEFAULT ''PRIVATE''');
SELECT tt_migration.add_column('0305', 'media_assets', 'rights_status', 'varchar(12) NOT NULL DEFAULT ''UNVERIFIED''');
SELECT tt_migration.add_column('0305', 'media_assets', 'license_id', 'uuid');
SELECT tt_migration.add_column('0305', 'media_assets', 'rights_expires_at', 'timestamptz');

SELECT tt_migration.add_constraint('0305', 'media_assets', 'ck_media_assets_media_type',
  'CHECK (media_type IS NULL OR media_type IN (''VIDEO'', ''IMAGE'', ''SUBTITLE'', ''OTHER'')) NOT VALID');
SELECT tt_migration.add_constraint('0305', 'media_assets', 'ck_media_assets_visibility',
  'CHECK (visibility IN (''PRIVATE'', ''PUBLIC'')) NOT VALID');
SELECT tt_migration.add_constraint('0305', 'media_assets', 'ck_media_assets_rights_status',
  'CHECK (rights_status IN (''UNVERIFIED'', ''CLEARED'', ''RESTRICTED'', ''EXPIRED'', ''REVOKED'')) NOT VALID');
SELECT tt_migration.add_constraint('0305', 'media_assets', 'ck_media_assets_public_requires_cleared',
  'CHECK (visibility <> ''PUBLIC'' OR rights_status = ''CLEARED'') NOT VALID');

-- Job rights-expiry quét media sắp/đã hết hạn quyền.
SELECT tt_migration.add_index('0305', 'media_assets', 'ix_media_assets_rights_expiry',
  'ON tt.media_assets (rights_expires_at) WHERE rights_status = ''CLEARED'' AND rights_expires_at IS NOT NULL');

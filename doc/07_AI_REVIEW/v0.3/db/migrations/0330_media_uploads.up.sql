-- 0330 — Tải lên media qua upload intent (WBS-046, WBS-036 tối thiểu; FR-VID-005/006/007, FR-CMS-009)
-- Khớp API v0.3: createUploadIntent (fileName, contentType, sizeBytes, checksumSha256), finalizeUpload (uploadToken),
-- getAdminMedia / listAdminMediaLibrary (MediaStatus: status, generation, errorCode, + fileName/contentType/sizeBytes/createdBy/updatedAt).
--
-- Mô hình:
--   • Một dòng tt.media_assets = một object trong kho lưu trữ (storage_key). Tải lên theo intent ngắn hạn:
--     UPLOADING → (finalize: HEAD + size + checksum) UPLOADED → VALIDATING → PROCESSING → READY,
--     hoặc REJECTED / FAILED / QUARANTINED / RESTRICTED (MediaStatus v0.3).
--   • upload token KHÔNG lưu: chỉ lưu sha256 (upload_token_hash) + hạn (upload_expires_at); finalize xong thì xóa cả hai.
--   • Quyền/hiển thị (0305) giữ nguyên mặc định PRIVATE/UNVERIFIED: tải lên không cấp quyền media.
--
-- Giả định schema 04 (bổ sung A-12): tt.media_assets(id, status, created_at). Dòng cũ để NULL các cột tệp (không backfill).
-- CHECK thêm ở dạng NOT VALID, VALIDATE ở 0332.

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_column('media_assets', 'status');
SELECT tt_migration.require_column('media_assets', 'created_at');
SELECT tt_migration.require_column('media_assets', 'rights_status');   -- cần 0305

DO $$
BEGIN
  PERFORM tt_migration.add_column('0330', 'media_assets', 'file_name', 'varchar(255)');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'content_type', 'varchar(150)');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'size_bytes', 'bigint');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'checksum_sha256', 'varchar(64)');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'storage_key', 'varchar(400)');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'generation', 'integer NOT NULL DEFAULT 1');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'error_code', 'varchar(60)');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'upload_token_hash', 'varchar(64)');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'upload_expires_at', 'timestamptz');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'created_by', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'uploaded_at', 'timestamptz');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'updated_at', 'timestamptz NOT NULL DEFAULT now()');
  PERFORM tt_migration.add_column('0330', 'media_assets', 'row_version', 'bigint NOT NULL DEFAULT 1');

  PERFORM tt_migration.add_constraint('0330', 'media_assets', 'ck_media_assets_status',
    'CHECK (status IN (''UPLOADING'', ''UPLOADED'', ''VALIDATING'', ''PROCESSING'', ''READY'', ''FAILED'', ''QUARANTINED'', '
    || '''REJECTED'', ''RESTRICTED'')) NOT VALID');
  PERFORM tt_migration.add_constraint('0330', 'media_assets', 'ck_media_assets_size_positive',
    'CHECK (size_bytes IS NULL OR size_bytes > 0) NOT VALID');
  PERFORM tt_migration.add_constraint('0330', 'media_assets', 'ck_media_assets_checksum_format',
    'CHECK (checksum_sha256 IS NULL OR checksum_sha256 ~ ''^[0-9a-f]{64}$'') NOT VALID');
  PERFORM tt_migration.add_constraint('0330', 'media_assets', 'ck_media_assets_generation',
    'CHECK (generation >= 1) NOT VALID');
  -- Token và hạn đi cùng nhau; chỉ tồn tại khi đang UPLOADING (finalize/hết hạn → xóa).
  PERFORM tt_migration.add_constraint('0330', 'media_assets', 'ck_media_assets_upload_token',
    'CHECK ((upload_token_hash IS NULL) = (upload_expires_at IS NULL) '
    || 'AND (upload_token_hash IS NULL OR (status = ''UPLOADING'' AND upload_token_hash ~ ''^[0-9a-f]{64}$''))) NOT VALID');
  -- Đã qua finalize thì phải biết tệp nào (kho lưu trữ + kích thước + checksum đã kiểm).
  PERFORM tt_migration.add_constraint('0330', 'media_assets', 'ck_media_assets_uploaded_complete',
    'CHECK (uploaded_at IS NULL OR (storage_key IS NOT NULL AND size_bytes IS NOT NULL AND checksum_sha256 IS NOT NULL)) NOT VALID');

  -- Thư viện media: mới nhất trước (created_at, id) cho cursor ổn định; lọc theo loại.
  PERFORM tt_migration.add_index('0330', 'media_assets', 'ix_media_assets_created', 'ON tt.media_assets (created_at, id)');
  PERFORM tt_migration.add_index('0330', 'media_assets', 'ix_media_assets_type_created', 'ON tt.media_assets (media_type, created_at, id)');
  -- Hàng đợi xử lý (worker WBS-038 / bộ giả lập dev): media đang chờ ở một bước pipeline.
  PERFORM tt_migration.add_index('0330', 'media_assets', 'ix_media_assets_pipeline',
    'ON tt.media_assets (status, updated_at) WHERE status IN (''UPLOADED'', ''VALIDATING'', ''PROCESSING'')');
  PERFORM tt_migration.add_index('0330', 'media_assets', 'ix_media_assets_storage_key',
    'ON tt.media_assets (storage_key) WHERE storage_key IS NOT NULL');

  PERFORM tt_migration.add_trigger('0330', 'media_assets', 'trg_media_assets_touch',
    'BEFORE UPDATE ON tt.media_assets FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');
END $$;

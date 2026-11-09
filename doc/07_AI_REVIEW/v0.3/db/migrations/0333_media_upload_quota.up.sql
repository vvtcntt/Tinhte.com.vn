-- 0333 — Hạn mức tải lên + dọn upload intent hết hạn (WBS-046/036; FR-VID-005; ADR-0006)
--   • Hạn mức theo người tải (createUploadIntent → 429 RATE_LIMITED): đếm intent gần đây và tổng dung lượng đang chờ tải lên
--     của một người → index (created_by, created_at).
--   • Dọn intent hết hạn chưa finalize: UPLOADING quá upload_expires_at → FAILED + error_code UPLOAD_EXPIRED, xóa token,
--     xóa object dở trên kho → index một phần theo hạn.
-- Chỉ thêm index (không đổi dữ liệu, không thêm CHECK).

SELECT tt_migration.require_column('media_assets', 'created_by');          -- cần 0330
SELECT tt_migration.require_column('media_assets', 'upload_expires_at');   -- cần 0330

DO $$
BEGIN
  PERFORM tt_migration.add_index('0333', 'media_assets', 'ix_media_assets_creator_created',
    'ON tt.media_assets (created_by, created_at) WHERE created_by IS NOT NULL');
  PERFORM tt_migration.add_index('0333', 'media_assets', 'ix_media_assets_upload_expiry',
    'ON tt.media_assets (upload_expires_at) WHERE status = ''UPLOADING'' AND upload_expires_at IS NOT NULL');
END $$;

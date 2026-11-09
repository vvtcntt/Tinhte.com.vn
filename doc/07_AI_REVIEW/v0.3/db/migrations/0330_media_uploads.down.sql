-- 0330 down — gỡ đúng các cột/ràng buộc/index/trigger 0330 đã tạo (ledger).
-- TỪ CHỐI nếu đã có media được tải lên qua API mới (storage_key): mất cột này là mất dấu object trong kho lưu trữ.
DO $$
BEGIN
  IF tt_migration.has_column('media_assets', 'storage_key')
     AND EXISTS (SELECT 1 FROM tt.media_assets WHERE storage_key IS NOT NULL) THEN
    RAISE EXCEPTION 'Refusing to roll back 0330: tt.media_assets references stored objects (storage_key). Export the data, clean up storage and obtain approval before rolling back.';
  END IF;
END $$;
SELECT tt_migration.rollback('0330');

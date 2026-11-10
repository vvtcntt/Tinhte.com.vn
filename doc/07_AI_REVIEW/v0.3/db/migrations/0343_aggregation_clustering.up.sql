-- 0343 — Gom cụm Radar tin GĐ1 (ADR-0008 §7, §9; BA-01 BR-21…23, BR-32) · FR-AGG-004/005 · DRAFT chờ TL + DBA duyệt
-- 1) agg_items.minhash (64 giá trị) + minhash_bands (32 khóa LSH) + index GIN: tìm mục ứng viên cùng sự kiện mà không quét cả bảng.
--    Index phụ cho mục đã phân loại nhưng chưa vào cụm (worker gom bù).
-- 2) agg_clusters: số mục, số feed, số nguồn độc lập (tính lại khi cụm đổi — danh sách Radar không phải đếm lại), covered_at (nhãn
--    NEW_SINCE_COVERED, BR-22).
-- 3) row_version của cụm là token If-Match của BTV: chỉ tăng khi cột do thao tác CMS đổi (status, assignee_id, covered_article_id). Worker
--    thêm mục/tính lại điểm không làm ETag của BTV hết hiệu lực. Down 0343 tạo lại trigger tăng row_version ở mọi UPDATE của 0341.
-- Bảng của 0341 → không cần NOT VALID/VALIDATE; 0343 không thêm CHECK.

SELECT tt_migration.require_table('agg_items');
SELECT tt_migration.require_table('agg_clusters');

SELECT tt_migration.add_column('0343', 'agg_items', 'minhash', 'integer[]');
SELECT tt_migration.add_column('0343', 'agg_items', 'minhash_bands', 'bigint[]');
SELECT tt_migration.add_index('0343', 'agg_items', 'ix_agg_items_minhash_bands',
  'ON tt.agg_items USING gin (minhash_bands) WHERE cluster_id IS NOT NULL');
SELECT tt_migration.add_index('0343', 'agg_items', 'ix_agg_items_unclustered',
  'ON tt.agg_items (site_key, first_seen_at) WHERE status = ''CLASSIFIED'' AND cluster_id IS NULL');

SELECT tt_migration.add_column('0343', 'agg_clusters', 'item_count', 'integer NOT NULL DEFAULT 0');
SELECT tt_migration.add_column('0343', 'agg_clusters', 'feed_count', 'integer NOT NULL DEFAULT 0');
SELECT tt_migration.add_column('0343', 'agg_clusters', 'source_count', 'integer NOT NULL DEFAULT 0');
SELECT tt_migration.add_column('0343', 'agg_clusters', 'covered_at', 'timestamptz');

DROP TRIGGER IF EXISTS trg_agg_clusters_touch ON tt.agg_clusters;
SELECT tt_migration.add_trigger('0343', 'agg_clusters', 'trg_agg_clusters_touch_cms',
  'BEFORE UPDATE OF status, assignee_id, covered_article_id ON tt.agg_clusters FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');

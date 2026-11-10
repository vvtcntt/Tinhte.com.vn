-- 0346 — Radar tin: kết quả chốt của chủ dự án ngày 10/11/2026 (BA-01 phiếu xin quyết định P-01, P-02, L-03) · ADR-0008 · DRAFT
--   1) P-01: cờ aggregation.radar P2 → P1 (vẫn OFF; bật vẫn cần hồ sơ duyệt của 0300).
--   2) P-02: cờ riêng aggregation.ai_dedup (OFF, P2) cho bước AI xác định tin trùng chủ đề — chỉ bật sau L-07, S-03, F-02.
--   3) P-02: chu kỳ đọc feed mặc định 30 phút (CHECK 5–1440 không đổi).
--   4) P-02: agg_items.category_ids — mọi chuyên mục mục tin đủ ngưỡng (chuyên mục chính + chuyên mục phụ); gom cụm chỉ so mục cùng chuyên mục.
--   5) L-03: index cho việc xóa mục tin không thuộc cụm sau 90 ngày (worker xóa theo lô).

SELECT tt_migration.require_column('feature_gates', 'scope');
SELECT tt_migration.require_column('feature_gates', 'production_default');
SELECT tt_migration.require_table('agg_items');
SELECT tt_migration.require_table('agg_feeds');

-- 1) + 2) cờ. Ghi lại phần đã đổi để bản down chỉ gỡ đúng phần đó.
CREATE TABLE IF NOT EXISTS tt_migration.flag_changed_0346 (key varchar(80) PRIMARY KEY, old_scope varchar(10), added boolean NOT NULL);
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM tt.feature_gates WHERE key = 'aggregation.radar' AND scope = 'P2')
     AND NOT EXISTS (SELECT 1 FROM tt_migration.flag_changed_0346 WHERE key = 'aggregation.radar') THEN
    INSERT INTO tt_migration.flag_changed_0346 (key, old_scope, added) VALUES ('aggregation.radar', 'P2', false);
    UPDATE tt.feature_gates SET scope = 'P1' WHERE key = 'aggregation.radar';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM tt.feature_gates WHERE key = 'aggregation.ai_dedup') THEN
    INSERT INTO tt.feature_gates (key, enabled, scope, production_default, description)
    VALUES ('aggregation.ai_dedup', false, 'P2', false,
            'News radar: AI check of candidate pairs (titles and summaries only) to decide same event. Falls back to MinHash when off or failing. Needs L-07, S-03, F-02.');
    INSERT INTO tt_migration.flag_changed_0346 (key, old_scope, added) VALUES ('aggregation.ai_dedup', NULL, true);
  END IF;
END $$;

-- 3) mặc định chu kỳ đọc. Feed đã có giữ chu kỳ đang đặt.
ALTER TABLE tt.agg_feeds ALTER COLUMN schedule_minutes SET DEFAULT 30;

-- 4) chuyên mục của mục tin. Dòng cũ: chỉ chuyên mục chính.
DO $$
BEGIN
  IF NOT tt_migration.has_column('agg_items', 'category_ids') THEN
    PERFORM tt_migration.add_column('0346', 'agg_items', 'category_ids', 'uuid[] NOT NULL DEFAULT ''{}''');
    UPDATE tt.agg_items SET category_ids = ARRAY[category_id] WHERE category_id IS NOT NULL;
  END IF;
END $$;
-- Chuyên mục chính luôn nằm đầu category_ids (trigger thay cho CHECK: người ghi chỉ đổi category_id — vd. BTV sửa tay — không phải nhớ cột này).
CREATE OR REPLACE FUNCTION tt.tg_agg_items_category_ids()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.category_id IS NOT NULL AND NOT (NEW.category_id = ANY(NEW.category_ids)) THEN
    NEW.category_ids := array_prepend(NEW.category_id, NEW.category_ids);
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0346', 'tg_agg_items_category_ids()');
SELECT tt_migration.add_trigger('0346', 'agg_items', 'trg_agg_items_category_ids',
  'BEFORE INSERT OR UPDATE OF category_id, category_ids ON tt.agg_items FOR EACH ROW EXECUTE FUNCTION tt.tg_agg_items_category_ids()');

-- 5) xóa mục tin lẻ theo tuổi.
SELECT tt_migration.add_index('0346', 'agg_items', 'ix_agg_items_unclustered_age',
  'ON tt.agg_items (first_seen_at) WHERE cluster_id IS NULL');

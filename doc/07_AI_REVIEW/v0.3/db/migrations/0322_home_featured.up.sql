-- 0322 — Tin nổi bật trang chủ do biên tập chọn, theo site (FR-DISC-003, WBS-027/051) · DRAFT chờ DBA + TL duyệt
-- Khớp API v0.3: getAdminHomeFeaturedArticles / putAdminHomeFeaturedArticles (HomeFeaturedArticles), getHome (HomePage).
--
-- Mô hình:
--   • tt.home_layouts: một dòng cho mỗi site public-web; row_version là ETag của danh sách (If-Match khi thay — FR-CMS-007).
--   • tt.home_featured_articles: danh sách bài ghim có thứ tự (position 1..n). Thay cả danh sách trong một transaction.
--     Bài phải thuộc đúng site (primary_site_key). Bài ghim mà sau đó bị gỡ/hết quyền KHÔNG bị xóa ở đây —
--     API public tự loại theo điều kiện hiển thị (FR-DISC-004), nên không lộ nội dung đã gỡ.
--   • tt.home_featured_history: ảnh chụp danh sách sau mỗi lần đổi (ai, lúc nào, row_version) — chỉ thêm (FR-ID-007 / audit).
-- Cần 0312 (tt.sites, articles.primary_site_key).

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_table('sites');
SELECT tt_migration.require_column('articles', 'primary_site_key');

SELECT tt_migration.track_new_table('0322', 'home_layouts');
CREATE TABLE IF NOT EXISTS tt.home_layouts (
  site_key    varchar(31) PRIMARY KEY REFERENCES tt.sites(key),
  row_version bigint NOT NULL DEFAULT 1 CHECK (row_version >= 1),
  updated_by  uuid REFERENCES tt.users(id),
  updated_at  timestamptz NOT NULL DEFAULT now()
);
SELECT tt_migration.add_trigger('0322', 'home_layouts', 'trg_home_layouts_site_kind',
  'BEFORE INSERT OR UPDATE OF site_key ON tt.home_layouts FOR EACH ROW EXECUTE FUNCTION tt.tg_content_site_kind(''site_key'')');
SELECT tt_migration.add_trigger('0322', 'home_layouts', 'trg_home_layouts_touch',
  'BEFORE UPDATE ON tt.home_layouts FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');

SELECT tt_migration.track_new_table('0322', 'home_featured_articles');
CREATE TABLE IF NOT EXISTS tt.home_featured_articles (
  site_key   varchar(31) NOT NULL REFERENCES tt.home_layouts(site_key),
  article_id uuid NOT NULL REFERENCES tt.articles(id),
  position   smallint NOT NULL CHECK (position BETWEEN 1 AND 10),
  PRIMARY KEY (site_key, article_id),
  CONSTRAINT uq_home_featured_articles_position UNIQUE (site_key, position)
);
SELECT tt_migration.add_index('0322', 'home_featured_articles', 'ix_home_featured_articles_article',
  'ON tt.home_featured_articles (article_id)');

-- Bài ghim phải thuộc site của trang chủ (ADR-0001 D8): không ghim bài site khác.
CREATE OR REPLACE FUNCTION tt.tg_home_featured_article_site()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_site text;
BEGIN
  SELECT primary_site_key INTO v_site FROM tt.articles WHERE id = NEW.article_id;
  IF v_site IS DISTINCT FROM NEW.site_key THEN
    PERFORM tt.raise_domain_error('VALIDATION_FAILED',
      format('Article %s belongs to site %s, cannot be featured on site %s', NEW.article_id, v_site, NEW.site_key));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0322', 'tg_home_featured_article_site()');
SELECT tt_migration.add_trigger('0322', 'home_featured_articles', 'trg_home_featured_article_site',
  'BEFORE INSERT OR UPDATE ON tt.home_featured_articles FOR EACH ROW EXECUTE FUNCTION tt.tg_home_featured_article_site()');

SELECT tt_migration.track_new_table('0322', 'home_featured_history');
CREATE TABLE IF NOT EXISTS tt.home_featured_history (
  id          bigserial PRIMARY KEY,
  site_key    varchar(31) NOT NULL REFERENCES tt.sites(key),
  row_version bigint NOT NULL,
  article_ids uuid[] NOT NULL,
  actor_id    uuid REFERENCES tt.users(id),
  created_at  timestamptz NOT NULL DEFAULT now()
);
SELECT tt_migration.add_index('0322', 'home_featured_history', 'ix_home_featured_history_site',
  'ON tt.home_featured_history (site_key, created_at DESC)');
SELECT tt_migration.add_trigger('0322', 'home_featured_history', 'trg_home_featured_history_append_only',
  'BEFORE UPDATE OR DELETE ON tt.home_featured_history FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');
SELECT tt_migration.add_trigger('0322', 'home_featured_history', 'trg_home_featured_history_no_truncate',
  'BEFORE TRUNCATE ON tt.home_featured_history FOR EACH STATEMENT EXECUTE FUNCTION tt.tg_append_only()');

-- Ứng dụng thay danh sách rồi UPDATE tt.home_layouts (tăng row_version) trong cùng transaction;
-- trigger AFTER ghi ảnh chụp danh sách hiện hành. Người thao tác: SET LOCAL tt.actor_id.
CREATE OR REPLACE FUNCTION tt.tg_home_layout_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  INSERT INTO tt.home_featured_history (site_key, row_version, article_ids, actor_id)
  VALUES (NEW.site_key, NEW.row_version,
          COALESCE((SELECT array_agg(f.article_id ORDER BY f.position) FROM tt.home_featured_articles f WHERE f.site_key = NEW.site_key), '{}'),
          COALESCE(NULLIF(current_setting('tt.actor_id', true), '')::uuid, NEW.updated_by));
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0322', 'tg_home_layout_history()');
SELECT tt_migration.add_trigger('0322', 'home_layouts', 'trg_home_layouts_history',
  'AFTER UPDATE ON tt.home_layouts FOR EACH ROW EXECUTE FUNCTION tt.tg_home_layout_history()');

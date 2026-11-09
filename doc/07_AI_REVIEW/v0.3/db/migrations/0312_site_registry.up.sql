-- 0312 — Site Registry trong DB (ADR-0001 D8): tt.sites + site_key cho nội dung
-- Nguồn sự thật: config/sites/sites.json. Bảng tt.sites được đồng bộ bằng SQL sinh từ
-- tools/sites/generate-db-sync.mjs (pipeline deploy chạy sau migration). Site không bao giờ bị xóa: chỉ 'retired'.
-- Nội dung hiện có thuộc site 'www'. API công khai lọc theo site của Host; API admin thêm siteKey khi có site nội dung thứ hai.

SELECT tt_migration.require_column('articles', 'id');
SELECT tt_migration.require_column('articles', 'status');
SELECT tt_migration.require_column('videos', 'id');
SELECT tt_migration.require_column('videos', 'status');
SELECT tt_migration.require_column('categories', 'id');

SELECT tt_migration.track_new_table('0312', 'sites');
CREATE TABLE IF NOT EXISTS tt.sites (
  key        varchar(31) PRIMARY KEY CHECK (key ~ '^[a-z][a-z0-9-]{1,30}$'),
  label      varchar(120) NOT NULL,
  kind       varchar(16) NOT NULL CHECK (kind IN ('public-web', 'cms', 'media', 'api', 'identity')),
  status     varchar(12) NOT NULL CHECK (status IN ('active', 'planned', 'reserved', 'retired')),
  scope      varchar(8) NOT NULL CHECK (scope IN ('P0', 'P0*', 'P0-ADS', 'P1', 'P2')),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

-- Bootstrap tối thiểu để khóa ngoại mặc định 'www' hợp lệ; nội dung đầy đủ do bước đồng bộ ghi đè.
INSERT INTO tt.sites (key, label, kind, status, scope) VALUES ('www', 'Tinhte.com.vn', 'public-web', 'active', 'P0')
ON CONFLICT (key) DO NOTHING;

SELECT tt_migration.add_trigger('0312', 'sites', 'trg_sites_touch',
  'BEFORE UPDATE ON tt.sites FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_updated_at()');
SELECT tt_migration.add_trigger('0312', 'sites', 'trg_sites_no_delete',
  'BEFORE DELETE ON tt.sites FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['articles', 'videos'] LOOP
    PERFORM tt_migration.add_column('0312', t, 'primary_site_key', 'varchar(31) NOT NULL DEFAULT ''www'' REFERENCES tt.sites(key)');
    PERFORM tt_migration.add_index('0312', t, 'ix_' || t || '_primary_site', format('ON tt.%I (primary_site_key, status)', t));
  END LOOP;
  PERFORM tt_migration.add_column('0312', 'categories', 'site_key', 'varchar(31) NOT NULL DEFAULT ''www'' REFERENCES tt.sites(key)');
  PERFORM tt_migration.add_index('0312', 'categories', 'ix_categories_site', 'ON tt.categories (site_key)');
END $$;

-- Nội dung/chuyên mục chỉ thuộc site public-web chưa retired (không gán cho cms/media). TG_ARGV[0] = tên cột site.
CREATE OR REPLACE FUNCTION tt.tg_content_site_kind()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_key    text := to_jsonb(NEW) ->> TG_ARGV[0];
  v_kind   text;
  v_status text;
BEGIN
  SELECT kind, status INTO v_kind, v_status FROM tt.sites WHERE key = v_key;
  IF v_kind IS DISTINCT FROM 'public-web' OR v_status = 'retired' THEN
    PERFORM tt.raise_domain_error('VALIDATION_FAILED',
      format('tt.%s.%s = %s: content must belong to an active/planned public-web site', TG_TABLE_NAME, TG_ARGV[0], v_key));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0312', 'tg_content_site_kind()');

SELECT tt_migration.add_trigger('0312', 'articles', 'trg_articles_site_kind',
  'BEFORE INSERT OR UPDATE OF primary_site_key ON tt.articles FOR EACH ROW EXECUTE FUNCTION tt.tg_content_site_kind(''primary_site_key'')');
SELECT tt_migration.add_trigger('0312', 'videos', 'trg_videos_site_kind',
  'BEFORE INSERT OR UPDATE OF primary_site_key ON tt.videos FOR EACH ROW EXECUTE FUNCTION tt.tg_content_site_kind(''primary_site_key'')');
SELECT tt_migration.add_trigger('0312', 'categories', 'trg_categories_site_kind',
  'BEFORE INSERT OR UPDATE OF site_key ON tt.categories FOR EACH ROW EXECUTE FUNCTION tt.tg_content_site_kind(''site_key'')');

-- 0308 — Redirect slug (FR-NEWS-002, 308 trong GET /articles/{slug}) và kho Idempotency-Key (AIR-023)

SELECT tt_migration.require_column('articles', 'slug');

-- 1) Redirect: đổi slug → slug cũ thành redirect 308; slug cũ của bài A không bị bài B chiếm ("slug trùng không được chiếm URL").
SELECT tt_migration.track_new_table('0308', 'article_slug_redirects');
CREATE TABLE IF NOT EXISTS tt.article_slug_redirects (
  old_slug   varchar(250) PRIMARY KEY,
  article_id uuid NOT NULL REFERENCES tt.articles(id),
  created_at timestamptz NOT NULL DEFAULT now()
);
SELECT tt_migration.add_index('0308', 'article_slug_redirects', 'ix_article_slug_redirects_article',
  'ON tt.article_slug_redirects (article_id)');

CREATE OR REPLACE FUNCTION tt.tg_article_slug_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM tt.article_slug_redirects WHERE old_slug = NEW.slug AND article_id <> NEW.id) THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('Slug "%s" is reserved as a redirect of another article', NEW.slug));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0308', 'tg_article_slug_guard()');

CREATE OR REPLACE FUNCTION tt.tg_article_slug_redirect()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.slug IS DISTINCT FROM OLD.slug THEN
    DELETE FROM tt.article_slug_redirects WHERE old_slug = NEW.slug;   -- bài lấy lại slug cũ của chính nó
    INSERT INTO tt.article_slug_redirects (old_slug, article_id) VALUES (OLD.slug, NEW.id)
    ON CONFLICT (old_slug) DO UPDATE SET article_id = EXCLUDED.article_id, created_at = now();
  END IF;
  RETURN NULL;
END $$;
SELECT tt_migration.track_function('0308', 'tg_article_slug_redirect()');

SELECT tt_migration.add_trigger('0308', 'articles', 'trg_articles_slug_guard',
  'BEFORE INSERT OR UPDATE OF slug ON tt.articles FOR EACH ROW EXECUTE FUNCTION tt.tg_article_slug_guard()');
SELECT tt_migration.add_trigger('0308', 'articles', 'trg_articles_slug_redirect',
  'AFTER UPDATE OF slug ON tt.articles FOR EACH ROW EXECUTE FUNCTION tt.tg_article_slug_redirect()');

-- 2) Idempotency-Key: phạm vi actor + method + route; lưu 24h; status_code NULL = đang xử lý (chặn xử lý song song).
--    Cùng khóa khác request_hash → 409 IDEMPOTENCY_KEY_REUSED (kiểm ở ứng dụng). Job dọn: DELETE ... WHERE expires_at < now().
SELECT tt_migration.track_new_table('0308', 'idempotency_keys');
CREATE TABLE IF NOT EXISTS tt.idempotency_keys (
  actor_key       varchar(80) NOT NULL,     -- user id; ẩn danh: 'anon:' || hash phiên
  http_method     varchar(8) NOT NULL CHECK (http_method IN ('POST', 'PUT', 'PATCH', 'DELETE')),
  route_template  varchar(200) NOT NULL,    -- ví dụ /videos/{videoId}/comments
  idempotency_key varchar(200) NOT NULL CHECK (length(idempotency_key) BETWEEN 8 AND 200),
  request_hash    char(64) NOT NULL,        -- sha256 hex của body chuẩn hóa
  status_code     smallint CHECK (status_code BETWEEN 100 AND 599),
  response_body   jsonb,
  created_at      timestamptz NOT NULL DEFAULT now(),
  expires_at      timestamptz NOT NULL DEFAULT now() + interval '24 hours',
  PRIMARY KEY (actor_key, http_method, route_template, idempotency_key),
  CONSTRAINT ck_idempotency_keys_expiry CHECK (expires_at > created_at)
);
SELECT tt_migration.add_index('0308', 'idempotency_keys', 'ix_idempotency_keys_expiry',
  'ON tt.idempotency_keys (expires_at)');

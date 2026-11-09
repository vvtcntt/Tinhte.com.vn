-- 0320 — Revision bài viết + publish pointer (WBS-022; FR-NEWS-001/002/004/005)
-- Khớp API v0.3: AdminArticle, ArticleRevision, CreateArticleRequest, ArticlePatchRequest, PublishRequest, Article.
--
-- Mô hình:
--   • tt.articles.working_revision_id = bản đang soạn; tt.articles.public_revision_id = "publish pointer" (0306).
--   • Revision chỉ sửa được nội dung khi status = DRAFT và CHƯA là public revision của bài. Sau khi gửi duyệt,
--     được duyệt hoặc được public, nội dung đóng băng: muốn sửa thì tạo revision mới (FR-NEWS-005).
--     Cột trạng thái/duyệt/xuất bản (status, sponsorship_approved_*, published_*) vẫn đổi được để workflow chạy.
--   • Revision không xóa được (lịch sử sửa bài — FR-NEWS-005, FR-CMS-004).
--   • category_id/summary nằm trên revision: đổi chuyên mục hay sapo của bài đang public không lộ ra trước khi publish.
--     Publish chép category_id của revision sang tt.articles.category_id (dùng cho danh sách công khai).
--
-- Giả định schema 04 (bổ sung A-05): tt.article_revisions(title, body jsonb, author_id, created_at), tt.articles(category_id, created_at).
-- Backfill: revision đang là public revision → status PUBLISHED; bài PUBLISHED chưa có published_at → lấy created_at
-- (DBA thay bằng cột ngày xuất bản thật của schema 04 nếu có). CHECK thêm ở dạng NOT VALID, VALIDATE ở 0321.

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_table('categories');
SELECT tt_migration.require_column('article_revisions', 'body');
SELECT tt_migration.require_column('article_revisions', 'author_id');
SELECT tt_migration.require_column('article_revisions', 'content_kind');   -- cần 0301
SELECT tt_migration.require_column('articles', 'working_revision_id');     -- cần 0306
SELECT tt_migration.require_column('articles', 'category_id');

DO $$
BEGIN
  PERFORM tt_migration.add_column('0320', 'article_revisions', 'status', 'varchar(20) NOT NULL DEFAULT ''DRAFT''');
  PERFORM tt_migration.add_column('0320', 'article_revisions', 'summary', 'text');
  PERFORM tt_migration.add_column('0320', 'article_revisions', 'category_id', 'uuid REFERENCES tt.categories(id)');
  PERFORM tt_migration.add_column('0320', 'article_revisions', 'updated_at', 'timestamptz NOT NULL DEFAULT now()');
  PERFORM tt_migration.add_column('0320', 'article_revisions', 'updated_by', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0320', 'article_revisions', 'published_at', 'timestamptz');
  PERFORM tt_migration.add_column('0320', 'article_revisions', 'published_by', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0320', 'articles', 'published_at', 'timestamptz');

  -- Vòng đời revision: DRAFT → IN_REVIEW → CHANGES_REQUESTED | REJECTED | APPROVED → PUBLISHED → SUPERSEDED.
  -- Bảng chuyển trạng thái chi tiết thuộc workflow duyệt (WBS-024).
  PERFORM tt_migration.add_constraint('0320', 'article_revisions', 'ck_article_revisions_status',
    'CHECK (status IN (''DRAFT'', ''IN_REVIEW'', ''CHANGES_REQUESTED'', ''REJECTED'', ''APPROVED'', ''PUBLISHED'', ''SUPERSEDED'')) NOT VALID');
  PERFORM tt_migration.add_constraint('0320', 'article_revisions', 'ck_article_revisions_published_pair',
    'CHECK (published_by IS NULL OR published_at IS NOT NULL) NOT VALID');
  PERFORM tt_migration.add_constraint('0320', 'article_revisions', 'ck_article_revisions_title_not_blank',
    'CHECK (btrim(title) <> '''') NOT VALID');
  PERFORM tt_migration.add_constraint('0320', 'article_revisions', 'ck_article_revisions_body_array',
    'CHECK (body IS NULL OR jsonb_typeof(body) = ''array'') NOT VALID');
  PERFORM tt_migration.add_constraint('0320', 'articles', 'ck_articles_slug_format',
    'CHECK (slug ~ ''^[a-z0-9]+(-[a-z0-9]+)*$'') NOT VALID');

  -- Danh sách CMS: sắp theo updatedAt/createdAt (+ id) cho cursor ổn định; tra revision theo bài.
  PERFORM tt_migration.add_index('0320', 'articles', 'ix_articles_updated', 'ON tt.articles (updated_at, id)');
  PERFORM tt_migration.add_index('0320', 'articles', 'ix_articles_created', 'ON tt.articles (created_at, id)');
  PERFORM tt_migration.add_index('0320', 'articles', 'ix_articles_public_revision', 'ON tt.articles (public_revision_id)');
END $$;

-- Backfill (idempotent: chạy lại không đổi gì).
UPDATE tt.article_revisions r SET status = 'PUBLISHED'
 WHERE r.status = 'DRAFT' AND EXISTS (SELECT 1 FROM tt.articles a WHERE a.public_revision_id = r.id);
UPDATE tt.articles SET published_at = created_at WHERE status = 'PUBLISHED' AND published_at IS NULL;

-- Đóng băng nội dung revision (FR-NEWS-005: "Draft revision không thay đổi phiên bản công khai cho đến khi đủ duyệt").
CREATE OR REPLACE FUNCTION tt.tg_article_revision_frozen()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_frozen boolean := OLD.status <> 'DRAFT' OR EXISTS (SELECT 1 FROM tt.articles WHERE public_revision_id = OLD.id);
BEGIN
  IF (NEW.article_id, NEW.number, NEW.title, NEW.summary, NEW.body, NEW.category_id, NEW.author_id, NEW.content_kind,
      NEW.sponsor_name, NEW.sponsor_reference, NEW.sponsorship_evidence_ref, NEW.sponsorship_valid_from,
      NEW.sponsorship_valid_to, NEW.change_reason, NEW.created_at)
     IS DISTINCT FROM
     (OLD.article_id, OLD.number, OLD.title, OLD.summary, OLD.body, OLD.category_id, OLD.author_id, OLD.content_kind,
      OLD.sponsor_name, OLD.sponsor_reference, OLD.sponsorship_evidence_ref, OLD.sponsorship_valid_from,
      OLD.sponsorship_valid_to, OLD.change_reason, OLD.created_at) THEN
    IF v_frozen THEN
      PERFORM tt.raise_domain_error('STATE_CONFLICT',
        format('Revision %s of article %s is %s and immutable: create a new revision instead',
               OLD.number, OLD.article_id, CASE WHEN OLD.status = 'DRAFT' THEN 'public' ELSE OLD.status END));
    END IF;
    NEW.updated_at := now();
  END IF;

  -- Không "mở băng" revision: đã rời DRAFT thì không quay lại DRAFT.
  IF OLD.status <> 'DRAFT' AND NEW.status = 'DRAFT' THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('Revision %s of article %s cannot return to DRAFT', OLD.number, OLD.article_id));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0320', 'tg_article_revision_frozen()');

SELECT tt_migration.add_trigger('0320', 'article_revisions', 'trg_article_revisions_frozen',
  'BEFORE UPDATE ON tt.article_revisions FOR EACH ROW EXECUTE FUNCTION tt.tg_article_revision_frozen()');
SELECT tt_migration.add_trigger('0320', 'article_revisions', 'trg_article_revisions_no_delete',
  'BEFORE DELETE ON tt.article_revisions FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');

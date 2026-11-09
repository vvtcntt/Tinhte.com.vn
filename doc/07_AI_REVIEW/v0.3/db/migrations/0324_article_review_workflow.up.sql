-- 0324 — Quy trình duyệt bài (WBS-024; FR-NEWS-004, FR-CMS-002, FR-CMS-010)
-- Khớp API v0.3: submitArticleForReview, approveArticleRevision, postAdminArticlesArticleidRequestChanges,
-- postAdminArticlesArticleidReject; ArticleRevision.submitted*/reviewed*/reviewNote.
--
-- Mỗi revision qua tối đa MỘT vòng duyệt (revision đã rời DRAFT không sửa được — 0320; sửa tiếp tạo revision mới),
-- nên người gửi/người duyệt/lý do lưu ngay trên revision và là lịch sử duyệt (FR-CMS-010 "ghi và hiển thị reason").
--
-- Chốt chặn ở DB (domain vẫn kiểm trước, kể cả tách nhiệm vụ SoD — người duyệt ≠ tác giả):
--   • chỉ cho các bước chuyển trạng thái revision hợp lệ (STATE_CONFLICT):
--       DRAFT → IN_REVIEW → APPROVED | CHANGES_REQUESTED | REJECTED;  APPROVED → PUBLISHED → SUPERSEDED;
--       SUPERSEDED → PUBLISHED (rollback, WBS sau)
--   • CHANGES_REQUESTED / REJECTED phải có lý do ≥ 5 ký tự (REASON_REQUIRED);
--   • thông tin gửi/duyệt đã ghi thì không sửa được (STATE_CONFLICT).
-- CHECK thêm ở dạng NOT VALID, VALIDATE ở 0325.

SELECT tt_migration.require_column('article_revisions', 'status');   -- cần 0320
SELECT tt_migration.require_table('users');

DO $$
BEGIN
  PERFORM tt_migration.add_column('0324', 'article_revisions', 'submitted_by', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0324', 'article_revisions', 'submitted_at', 'timestamptz');
  PERFORM tt_migration.add_column('0324', 'article_revisions', 'reviewed_by', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0324', 'article_revisions', 'reviewed_at', 'timestamptz');
  PERFORM tt_migration.add_column('0324', 'article_revisions', 'review_note', 'varchar(2000)');

  PERFORM tt_migration.add_constraint('0324', 'article_revisions', 'ck_article_revisions_submitted_pair',
    'CHECK ((submitted_by IS NULL) = (submitted_at IS NULL)) NOT VALID');
  PERFORM tt_migration.add_constraint('0324', 'article_revisions', 'ck_article_revisions_reviewed_pair',
    'CHECK ((reviewed_by IS NULL) = (reviewed_at IS NULL)) NOT VALID');

  -- Hàng đợi duyệt (WBS-031): revision đang chờ, cũ nhất trước.
  PERFORM tt_migration.add_index('0324', 'article_revisions', 'ix_article_revisions_in_review',
    'ON tt.article_revisions (submitted_at) WHERE status = ''IN_REVIEW''');
END $$;

CREATE OR REPLACE FUNCTION tt.tg_article_revision_workflow()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status = 'IN_REVIEW')
    OR (OLD.status = 'IN_REVIEW' AND NEW.status IN ('APPROVED', 'CHANGES_REQUESTED', 'REJECTED'))
    OR (OLD.status = 'APPROVED' AND NEW.status = 'PUBLISHED')
    OR (OLD.status = 'PUBLISHED' AND NEW.status = 'SUPERSEDED')
    OR (OLD.status = 'SUPERSEDED' AND NEW.status = 'PUBLISHED')) THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('Revision %s of article %s cannot go from %s to %s', OLD.number, OLD.article_id, OLD.status, NEW.status));
  END IF;

  IF NEW.status IN ('CHANGES_REQUESTED', 'REJECTED') AND NEW.status IS DISTINCT FROM OLD.status
     AND (NEW.review_note IS NULL OR length(btrim(NEW.review_note)) < 5) THEN
    PERFORM tt.raise_domain_error('REASON_REQUIRED', format('%s requires a reason of at least 5 characters', NEW.status));
  END IF;

  IF (OLD.submitted_at IS NOT NULL AND (NEW.submitted_by, NEW.submitted_at) IS DISTINCT FROM (OLD.submitted_by, OLD.submitted_at))
     OR (OLD.reviewed_at IS NOT NULL AND (NEW.reviewed_by, NEW.reviewed_at, NEW.review_note)
                                         IS DISTINCT FROM (OLD.reviewed_by, OLD.reviewed_at, OLD.review_note)) THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('Review record of revision %s of article %s is immutable', OLD.number, OLD.article_id));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0324', 'tg_article_revision_workflow()');

SELECT tt_migration.add_trigger('0324', 'article_revisions', 'trg_article_revisions_workflow',
  'BEFORE UPDATE OF status, submitted_by, submitted_at, reviewed_by, reviewed_at, review_note ON tt.article_revisions '
  || 'FOR EACH ROW EXECUTE FUNCTION tt.tg_article_revision_workflow()');

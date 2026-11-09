-- 0331 — Revision video cho CMS (WBS-046; nền cho WBS-040). Cùng mô hình với bài viết (0320, ADR-0005).
-- Khớp API v0.3: AdminVideo (+ summary), CreateVideoRequest, UpdateVideoRequest.
--
-- Mô hình:
--   • tt.videos.working_revision_id = bản đang soạn; public_revision_id = publish pointer (0306, chốt chặn 0307).
--   • Revision chỉ sửa được nội dung khi status = DRAFT và CHƯA là public revision; sau đó đóng băng → sửa = revision mới.
--     Cột trạng thái/duyệt tài trợ vẫn đổi được để workflow (WBS-040) chạy. Revision không xóa được.
--   • tt.videos.media_id (schema 04) = media đang gắn. Đổi media của video đã từng public thuộc WBS-040 (API trả 409).
--
-- Giả định schema 04 (A-12): tt.video_revisions(id, video_id, number, title, created_at), tt.videos(id, slug, media_id, created_at).
-- Backfill: revision đang là public revision → PUBLISHED. CHECK thêm ở dạng NOT VALID, VALIDATE ở 0332.

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_column('video_revisions', 'title');
SELECT tt_migration.require_column('video_revisions', 'content_kind');   -- cần 0301
SELECT tt_migration.require_column('videos', 'working_revision_id');     -- cần 0306
SELECT tt_migration.require_column('videos', 'media_id');

DO $$
BEGIN
  PERFORM tt_migration.add_column('0331', 'video_revisions', 'status', 'varchar(20) NOT NULL DEFAULT ''DRAFT''');
  PERFORM tt_migration.add_column('0331', 'video_revisions', 'summary', 'text');
  PERFORM tt_migration.add_column('0331', 'video_revisions', 'author_id', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0331', 'video_revisions', 'updated_at', 'timestamptz NOT NULL DEFAULT now()');
  PERFORM tt_migration.add_column('0331', 'video_revisions', 'updated_by', 'uuid REFERENCES tt.users(id)');

  PERFORM tt_migration.add_constraint('0331', 'video_revisions', 'ck_video_revisions_status',
    'CHECK (status IN (''DRAFT'', ''IN_REVIEW'', ''CHANGES_REQUESTED'', ''REJECTED'', ''APPROVED'', ''PUBLISHED'', ''SUPERSEDED'')) NOT VALID');
  PERFORM tt_migration.add_constraint('0331', 'video_revisions', 'ck_video_revisions_title_not_blank',
    'CHECK (btrim(title) <> '''') NOT VALID');

  -- Danh sách CMS: sắp theo updatedAt/createdAt (+ id) cho cursor ổn định; tra video theo media.
  PERFORM tt_migration.add_index('0331', 'videos', 'ix_videos_updated', 'ON tt.videos (updated_at, id)');
  PERFORM tt_migration.add_index('0331', 'videos', 'ix_videos_created', 'ON tt.videos (created_at, id)');
  PERFORM tt_migration.add_index('0331', 'videos', 'ix_videos_media', 'ON tt.videos (media_id) WHERE media_id IS NOT NULL');
END $$;

-- Backfill (idempotent).
UPDATE tt.video_revisions r SET status = 'PUBLISHED'
 WHERE r.status = 'DRAFT' AND EXISTS (SELECT 1 FROM tt.videos v WHERE v.public_revision_id = r.id);

CREATE OR REPLACE FUNCTION tt.tg_video_revision_frozen()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_frozen boolean := OLD.status <> 'DRAFT' OR EXISTS (SELECT 1 FROM tt.videos WHERE public_revision_id = OLD.id);
BEGIN
  IF (NEW.video_id, NEW.number, NEW.title, NEW.summary, NEW.author_id, NEW.content_kind, NEW.sponsor_name, NEW.sponsor_reference,
      NEW.sponsorship_evidence_ref, NEW.sponsorship_valid_from, NEW.sponsorship_valid_to, NEW.change_reason, NEW.created_at)
     IS DISTINCT FROM
     (OLD.video_id, OLD.number, OLD.title, OLD.summary, OLD.author_id, OLD.content_kind, OLD.sponsor_name, OLD.sponsor_reference,
      OLD.sponsorship_evidence_ref, OLD.sponsorship_valid_from, OLD.sponsorship_valid_to, OLD.change_reason, OLD.created_at) THEN
    IF v_frozen THEN
      PERFORM tt.raise_domain_error('STATE_CONFLICT',
        format('Revision %s of video %s is %s and immutable: create a new revision instead',
               OLD.number, OLD.video_id, CASE WHEN OLD.status = 'DRAFT' THEN 'public' ELSE OLD.status END));
    END IF;
    NEW.updated_at := now();
  END IF;

  IF OLD.status <> 'DRAFT' AND NEW.status = 'DRAFT' THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('Revision %s of video %s cannot return to DRAFT', OLD.number, OLD.video_id));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0331', 'tg_video_revision_frozen()');

SELECT tt_migration.add_trigger('0331', 'video_revisions', 'trg_video_revisions_frozen',
  'BEFORE UPDATE ON tt.video_revisions FOR EACH ROW EXECUTE FUNCTION tt.tg_video_revision_frozen()');
SELECT tt_migration.add_trigger('0331', 'video_revisions', 'trg_video_revisions_no_delete',
  'BEFORE DELETE ON tt.video_revisions FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');

-- 0334 — Quy trình duyệt/xuất bản video, media theo revision, chốt chặn publish (WBS-040; FR-VID-008, FR-VID-010, FR-CMS-002/010)
-- Khớp API v0.3: postAdminVideosVideoid{Submit,Approve,RequestChanges,Reject,Publish,Unpublish};
-- AdminVideo (+ revisionStatus, submitted*/reviewed*/reviewNote, unpublishedAt/unpublishReason — CHANGELOG mục 13).
--
-- Mô hình (ADR-0006 §6):
--   • Media là nội dung của revision: tt.video_revisions.media_id, đóng băng cùng nội dung (DRAFT chưa public mới đổi được).
--     Đổi media của video đã public = revision mới → duyệt → publish; bản công khai giữ media cũ tới lúc đó.
--   • Duyệt chốt đúng "thế hệ" media (approved_media_generation = media_assets.generation lúc duyệt). Publish khi media đã
--     xử lý lại (generation khác) → STATE_CONFLICT, phải duyệt lại.
--   • tt.videos.media_id (schema 04) trở thành cột DẪN XUẤT = media của public revision (NULL khi chưa/không còn public),
--     do trigger đặt khi đổi publish pointer; ghi thẳng cột này bị chặn. Phát/cấp quyền xem (WBS-039) đọc cột này.
--   • Gỡ (unpublish) xóa publish pointer, ghi unpublished_at/by + lý do trên video.
--
-- Chốt chặn ở DB (domain vẫn kiểm trước, kể cả SoD — người duyệt/xuất bản ≠ tác giả/người sửa revision):
--   • bước chuyển trạng thái revision hợp lệ (STATE_CONFLICT):
--       DRAFT → IN_REVIEW → APPROVED | CHANGES_REQUESTED | REJECTED;  APPROVED → PUBLISHED → SUPERSEDED → PUBLISHED (rollback)
--   • gửi duyệt/duyệt cần media; duyệt ghi người duyệt + thế hệ media; CHANGES_REQUESTED/REJECTED cần lý do ≥ 5 ký tự (REASON_REQUIRED);
--   • hồ sơ gửi/duyệt và thế hệ media đã duyệt không sửa được;
--   • đổi publish pointer sang revision mới (publish/rollback) chỉ khi media READY + VIDEO (MEDIA_NOT_READY), quyền media và video
--     CLEARED còn hạn (RIGHTS_INVALID), thế hệ media khớp lúc duyệt (STATE_CONFLICT). SPONSORED vẫn qua trigger 0307 (chạy trước).
--
-- Backfill (idempotent): revision working/public nhận media đang gắn với video; tt.videos.media_id = media của public revision.
-- CHECK thêm ở dạng NOT VALID, VALIDATE ở 0335.

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_column('video_revisions', 'status');        -- cần 0331
SELECT tt_migration.require_column('media_assets', 'generation');       -- cần 0330
SELECT tt_migration.require_column('videos', 'rights_expires_at');      -- cần 0306

DO $$
BEGIN
  PERFORM tt_migration.add_column('0334', 'video_revisions', 'media_id', 'uuid REFERENCES tt.media_assets(id)');
  PERFORM tt_migration.add_column('0334', 'video_revisions', 'approved_media_generation', 'integer');
  PERFORM tt_migration.add_column('0334', 'video_revisions', 'submitted_by', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0334', 'video_revisions', 'submitted_at', 'timestamptz');
  PERFORM tt_migration.add_column('0334', 'video_revisions', 'reviewed_by', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0334', 'video_revisions', 'reviewed_at', 'timestamptz');
  PERFORM tt_migration.add_column('0334', 'video_revisions', 'review_note', 'varchar(2000)');
  PERFORM tt_migration.add_column('0334', 'video_revisions', 'published_at', 'timestamptz');
  PERFORM tt_migration.add_column('0334', 'video_revisions', 'published_by', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0334', 'videos', 'unpublished_at', 'timestamptz');
  PERFORM tt_migration.add_column('0334', 'videos', 'unpublished_by', 'uuid REFERENCES tt.users(id)');
  PERFORM tt_migration.add_column('0334', 'videos', 'unpublish_reason', 'varchar(2000)');

  PERFORM tt_migration.add_constraint('0334', 'video_revisions', 'ck_video_revisions_submitted_pair',
    'CHECK ((submitted_by IS NULL) = (submitted_at IS NULL)) NOT VALID');
  PERFORM tt_migration.add_constraint('0334', 'video_revisions', 'ck_video_revisions_reviewed_pair',
    'CHECK ((reviewed_by IS NULL) = (reviewed_at IS NULL)) NOT VALID');
  PERFORM tt_migration.add_constraint('0334', 'video_revisions', 'ck_video_revisions_published_pair',
    'CHECK (published_by IS NULL OR published_at IS NOT NULL) NOT VALID');
  PERFORM tt_migration.add_constraint('0334', 'video_revisions', 'ck_video_revisions_approved_generation',
    'CHECK (approved_media_generation IS NULL OR (approved_media_generation >= 1 AND media_id IS NOT NULL)) NOT VALID');
  -- Gỡ phải có lý do (FR-VID-010, ActionReason ≥ 5 ký tự). unpublished_by có thể NULL khi job hệ thống gỡ (hết quyền).
  PERFORM tt_migration.add_constraint('0334', 'videos', 'ck_videos_unpublish_reason',
    'CHECK (unpublished_at IS NULL OR length(btrim(unpublish_reason)) >= 5) NOT VALID');

  -- Một media chỉ gắn một video (kiểm ở domain) + tra video theo media khi quyền media đổi (WBS-023/039).
  PERFORM tt_migration.add_index('0334', 'video_revisions', 'ix_video_revisions_media',
    'ON tt.video_revisions (media_id, video_id) WHERE media_id IS NOT NULL');
  -- Hàng đợi duyệt (WBS-031): revision video đang chờ, cũ nhất trước.
  PERFORM tt_migration.add_index('0334', 'video_revisions', 'ix_video_revisions_in_review',
    'ON tt.video_revisions (submitted_at) WHERE status = ''IN_REVIEW''');
END $$;

-- Backfill (trước khi tạo trigger; trigger đóng băng 0331 không bao gồm media_id).
UPDATE tt.video_revisions r SET media_id = v.media_id
  FROM tt.videos v
 WHERE r.video_id = v.id AND v.media_id IS NOT NULL AND r.media_id IS NULL
   AND r.id IN (v.working_revision_id, v.public_revision_id);
UPDATE tt.videos v SET media_id = p.media_id
  FROM tt.video_revisions p
 WHERE p.id = v.public_revision_id AND v.media_id IS DISTINCT FROM p.media_id;
UPDATE tt.videos SET media_id = NULL WHERE public_revision_id IS NULL AND media_id IS NOT NULL;

CREATE OR REPLACE FUNCTION tt.tg_video_revision_workflow()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_frozen boolean := OLD.status <> 'DRAFT' OR EXISTS (SELECT 1 FROM tt.videos WHERE public_revision_id = OLD.id);
BEGIN
  -- Giá trị ngoài vòng đời để CHECK ck_video_revisions_status báo (0331).
  IF NEW.status IS DISTINCT FROM OLD.status
     AND NEW.status IN ('DRAFT', 'IN_REVIEW', 'CHANGES_REQUESTED', 'REJECTED', 'APPROVED', 'PUBLISHED', 'SUPERSEDED') AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status = 'IN_REVIEW')
    OR (OLD.status = 'IN_REVIEW' AND NEW.status IN ('APPROVED', 'CHANGES_REQUESTED', 'REJECTED'))
    OR (OLD.status = 'APPROVED' AND NEW.status = 'PUBLISHED')
    OR (OLD.status = 'PUBLISHED' AND NEW.status = 'SUPERSEDED')
    OR (OLD.status = 'SUPERSEDED' AND NEW.status = 'PUBLISHED')) THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('Revision %s of video %s cannot go from %s to %s', OLD.number, OLD.video_id, OLD.status, NEW.status));
  END IF;

  -- Media là nội dung của revision: đổi được như tiêu đề (DRAFT, chưa public).
  IF NEW.media_id IS DISTINCT FROM OLD.media_id AND v_frozen THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('Media of revision %s of video %s is frozen: create a new revision instead', OLD.number, OLD.video_id));
  END IF;

  IF NEW.status = 'IN_REVIEW' AND OLD.status = 'DRAFT' AND NEW.media_id IS NULL THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('Revision %s of video %s has no media to review', OLD.number, OLD.video_id));
  END IF;

  IF NEW.approved_media_generation IS DISTINCT FROM OLD.approved_media_generation
     AND NOT (OLD.status = 'IN_REVIEW' AND NEW.status = 'APPROVED') THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('Approved media generation of revision %s of video %s is only set when approving', OLD.number, OLD.video_id));
  END IF;

  IF NEW.status = 'APPROVED' AND OLD.status = 'IN_REVIEW'
     AND (NEW.media_id IS NULL OR NEW.approved_media_generation IS NULL OR NEW.reviewed_at IS NULL) THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('Approving revision %s of video %s requires its media, the approved media generation and the reviewer', OLD.number, OLD.video_id));
  END IF;

  IF NEW.status IN ('CHANGES_REQUESTED', 'REJECTED') AND NEW.status IS DISTINCT FROM OLD.status
     AND (NEW.review_note IS NULL OR length(btrim(NEW.review_note)) < 5) THEN
    PERFORM tt.raise_domain_error('REASON_REQUIRED', format('%s requires a reason of at least 5 characters', NEW.status));
  END IF;

  IF (OLD.submitted_at IS NOT NULL AND (NEW.submitted_by, NEW.submitted_at) IS DISTINCT FROM (OLD.submitted_by, OLD.submitted_at))
     OR (OLD.reviewed_at IS NOT NULL AND (NEW.reviewed_by, NEW.reviewed_at, NEW.review_note)
                                         IS DISTINCT FROM (OLD.reviewed_by, OLD.reviewed_at, OLD.review_note)) THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('Review record of revision %s of video %s is immutable', OLD.number, OLD.video_id));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0334', 'tg_video_revision_workflow()');

SELECT tt_migration.add_trigger('0334', 'video_revisions', 'trg_video_revisions_workflow',
  'BEFORE UPDATE OF status, media_id, approved_media_generation, submitted_by, submitted_at, reviewed_by, reviewed_at, review_note '
  || 'ON tt.video_revisions FOR EACH ROW EXECUTE FUNCTION tt.tg_video_revision_workflow()');

-- Publish gate + tt.videos.media_id dẫn xuất từ public revision. Tên trigger xếp sau trg_videos_guard_publish (0307) nên
-- lỗi tài trợ (FEATURE_DISABLED/SPONSORSHIP_*) được báo trước.
CREATE OR REPLACE FUNCTION tt.tg_video_publish_gate()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  r      record;
  v_rows integer;
BEGIN
  IF TG_OP = 'UPDATE' AND NEW.public_revision_id IS NOT DISTINCT FROM OLD.public_revision_id THEN
    IF NEW.media_id IS DISTINCT FROM OLD.media_id THEN
      PERFORM tt.raise_domain_error('STATE_CONFLICT',
        format('tt.videos.media_id of video %s follows its public revision: change the media through a new revision', NEW.id));
    END IF;
    RETURN NEW;
  END IF;

  IF NEW.public_revision_id IS NULL THEN
    NEW.media_id := NULL;   -- chưa/không còn public: không media nào được phát công khai qua video này
    RETURN NEW;
  END IF;

  SELECT rv.media_id, rv.approved_media_generation, m.status AS media_status, m.media_type, m.generation,
         m.rights_status AS media_rights, m.rights_expires_at AS media_rights_expires_at
    INTO r
    FROM tt.video_revisions rv LEFT JOIN tt.media_assets m ON m.id = rv.media_id
   WHERE rv.id = NEW.public_revision_id AND rv.video_id = NEW.id;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  IF v_rows = 0 THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('Revision %s does not belong to video %s', NEW.public_revision_id, NEW.id));
  END IF;

  IF r.media_id IS NULL OR r.media_status IS DISTINCT FROM 'READY' OR r.media_type IS DISTINCT FROM 'VIDEO' THEN
    PERFORM tt.raise_domain_error('MEDIA_NOT_READY',
      format('Media of revision %s is %s: only a READY video can be published', NEW.public_revision_id, COALESCE(r.media_status, 'missing')));
  END IF;
  IF r.approved_media_generation IS NOT NULL AND r.approved_media_generation <> r.generation THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('Media of revision %s changed since approval (generation %s → %s): review it again', NEW.public_revision_id,
             r.approved_media_generation, r.generation));
  END IF;
  IF r.media_rights <> 'CLEARED' OR r.media_rights_expires_at <= now() THEN
    PERFORM tt.raise_domain_error('RIGHTS_INVALID', format('Media rights are %s%s: publishing requires CLEARED, unexpired rights',
      r.media_rights, CASE WHEN r.media_rights_expires_at <= now() THEN ' (expired)' ELSE '' END));
  END IF;
  IF NEW.rights_status <> 'CLEARED' OR NEW.rights_expires_at <= now() THEN
    PERFORM tt.raise_domain_error('RIGHTS_INVALID', format('Video rights are %s%s: publishing requires CLEARED, unexpired rights',
      NEW.rights_status, CASE WHEN NEW.rights_expires_at <= now() THEN ' (expired)' ELSE '' END));
  END IF;

  NEW.media_id := r.media_id;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0334', 'tg_video_publish_gate()');

SELECT tt_migration.add_trigger('0334', 'videos', 'trg_videos_publish_gate',
  'BEFORE INSERT OR UPDATE OF public_revision_id, media_id ON tt.videos FOR EACH ROW EXECUTE FUNCTION tt.tg_video_publish_gate()');

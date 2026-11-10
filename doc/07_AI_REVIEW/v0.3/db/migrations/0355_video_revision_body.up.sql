-- 0355 — Thân (mô tả) dạng khối cho video: tt.video_revisions.body (FR-CMS-001; AdminVideo.bodyBlocks, UpdateVideoRequest.bodyBlocks)
-- DRAFT chờ TL/PO duyệt (CHANGELOG mục 22). Cùng định dạng BodyBlock với tt.article_revisions.body; ứng dụng kiểm cấu trúc
-- (Tinhte.Platform.Content.BodyBlocks), DB chỉ chặn giá trị không phải mảng JSON.
--
--   body jsonb NOT NULL DEFAULT '[]'  Revision cũ nhận mảng rỗng (giá trị mặc định hằng → PostgreSQL ≥ 11 không ghi lại bảng).
--   ck_video_revisions_body_array     Dạng NOT VALID (bảng cũ), VALIDATE ở 0356.
--   trg_video_revisions_body_frozen   Thân là nội dung của revision: chỉ đổi được khi revision còn DRAFT và chưa public, giống
--                                     tiêu đề/tóm tắt (tg_video_revision_frozen của 0331) và media (0334). Trigger riêng thay vì sửa hàm
--                                     của 0331 để down chỉ gỡ đúng phần 0355 đã tạo.

SELECT tt_migration.require_column('video_revisions', 'status');   -- cần 0331
SELECT tt_migration.require_table('videos');

DO $$
BEGIN
  PERFORM tt_migration.add_column('0355', 'video_revisions', 'body', 'jsonb NOT NULL DEFAULT ''[]''::jsonb');
  PERFORM tt_migration.add_constraint('0355', 'video_revisions', 'ck_video_revisions_body_array',
    'CHECK (jsonb_typeof(body) = ''array'') NOT VALID');
END $$;

CREATE OR REPLACE FUNCTION tt.tg_video_revision_body_frozen()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.body IS DISTINCT FROM OLD.body THEN
    IF OLD.status <> 'DRAFT' OR EXISTS (SELECT 1 FROM tt.videos WHERE public_revision_id = OLD.id) THEN
      PERFORM tt.raise_domain_error('STATE_CONFLICT',
        format('Body of revision %s of video %s is frozen: create a new revision instead', OLD.number, OLD.video_id));
    END IF;
    NEW.updated_at := now();
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0355', 'tg_video_revision_body_frozen()');

SELECT tt_migration.add_trigger('0355', 'video_revisions', 'trg_video_revisions_body_frozen',
  'BEFORE UPDATE OF body ON tt.video_revisions FOR EACH ROW EXECUTE FUNCTION tt.tg_video_revision_body_frozen()');

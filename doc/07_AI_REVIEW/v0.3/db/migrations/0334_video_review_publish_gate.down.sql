-- 0334 down — gỡ đúng các cột/ràng buộc/index/trigger/hàm 0334 đã tạo (ledger) và trả tt.videos.media_id về nghĩa cũ
-- (media đang gắn của bản working, như 0331).
-- TỪ CHỐI nếu đã có lịch sử duyệt/xuất bản/gỡ (bằng chứng quy trình, FR-CMS-010, FR-VID-010) hoặc video có media bản nháp khác
-- media bản công khai (cột cũ chỉ giữ được một media → mất dữ liệu).
DO $$
BEGIN
  IF tt_migration.has_column('video_revisions', 'submitted_at')
     AND EXISTS (SELECT 1 FROM tt_migration.ledger WHERE migration = '0334' AND object_type = 'column' AND object_name = 'submitted_at') THEN
    IF EXISTS (SELECT 1 FROM tt.video_revisions WHERE submitted_at IS NOT NULL OR reviewed_at IS NOT NULL OR published_at IS NOT NULL)
       OR EXISTS (SELECT 1 FROM tt.videos WHERE unpublished_at IS NOT NULL) THEN
      RAISE EXCEPTION 'Refusing to roll back 0334: video review/publish/unpublish history exists. Export the data and obtain approval before rolling back.';
    END IF;
    IF EXISTS (SELECT 1 FROM tt.videos v
                 JOIN tt.video_revisions w ON w.id = v.working_revision_id
                 JOIN tt.video_revisions p ON p.id = v.public_revision_id
                WHERE w.media_id IS DISTINCT FROM p.media_id) THEN
      RAISE EXCEPTION 'Refusing to roll back 0334: a video has a working revision whose media differs from its public revision (media revision in progress).';
    END IF;

    CREATE TEMP TABLE tt_0334_media ON COMMIT DROP AS
      SELECT v.id, COALESCE(w.media_id, p.media_id) AS media_id
        FROM tt.videos v
        LEFT JOIN tt.video_revisions w ON w.id = v.working_revision_id
        LEFT JOIN tt.video_revisions p ON p.id = v.public_revision_id;
  END IF;
END $$;

SELECT tt_migration.rollback('0334');

DO $$
BEGIN
  IF to_regclass('pg_temp.tt_0334_media') IS NOT NULL THEN
    UPDATE tt.videos v SET media_id = t.media_id FROM pg_temp.tt_0334_media t
     WHERE t.id = v.id AND v.media_id IS DISTINCT FROM t.media_id;
  END IF;
END $$;

-- 0307 — Chốt chặn ở DB cho thao tác ghi có điều kiện pháp lý (AIR-002, AIR-019; FR-NEWS-008, FR-COM-009)
-- Phòng thủ nhiều lớp: domain vẫn kiểm trước; trigger bảo đảm KHÔNG đường ghi nào (job, script, bug) vượt qua.
--
-- 1) Publish guard: đổi public_revision_id sang revision SPONSORED chỉ khi
--      • cờ ads.sponsored_publication ON           → nếu không: FEATURE_DISABLED
--      • sponsorship đã duyệt (approved_by/at)     → nếu không: SPONSORSHIP_LABEL_REQUIRED
--      • now() nằm trong [valid_from, valid_to)    → nếu không: SPONSORSHIP_EXPIRED
--    và revision phải thuộc đúng bài/video        → nếu không: STATE_CONFLICT
-- 2) Community write guard: INSERT bình luận chỉ khi cờ community.video_comments ON → FEATURE_DISABLED.
--
-- Đọc cờ bằng SELECT ... FOR SHARE: lệnh tắt cờ (UPDATE) phải chờ các transaction ghi đang chạy kết thúc,
-- transaction ghi bắt đầu sau khi tắt cờ sẽ thấy OFF (FR-COM-009 "tắt giữa request phải bị chặn ở transaction ghi").
-- Ứng dụng map SQLSTATE P0001 + MESSAGE sang Problem.code (403/422/409).

SELECT tt_migration.require_column('feature_gates', 'enabled');
SELECT tt_migration.require_column('articles', 'public_revision_id');
SELECT tt_migration.require_column('videos', 'public_revision_id');
SELECT tt_migration.require_column('article_revisions', 'article_id');
SELECT tt_migration.require_column('video_revisions', 'video_id');
SELECT tt_migration.require_column('article_revisions', 'sponsorship_approved_by');  -- cần 0301

CREATE OR REPLACE FUNCTION tt.flag_enabled_for_write(p_key text)
RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE v_enabled boolean;
BEGIN
  SELECT enabled INTO v_enabled FROM tt.feature_gates WHERE key = p_key FOR SHARE;
  RETURN COALESCE(v_enabled, false);
END $$;
SELECT tt_migration.track_function('0307', 'flag_enabled_for_write(text)');

-- TG_ARGV[0] = bảng revision, TG_ARGV[1] = cột khóa ngoại tới bài/video.
CREATE OR REPLACE FUNCTION tt.tg_guard_sponsored_publish()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  r      record;
  v_rows integer;
BEGIN
  IF NEW.public_revision_id IS NULL
     OR (TG_OP = 'UPDATE' AND NEW.public_revision_id IS NOT DISTINCT FROM OLD.public_revision_id) THEN
    RETURN NEW;
  END IF;

  EXECUTE format('SELECT %I AS owner_id, content_kind, sponsorship_approved_by, sponsorship_approved_at, '
                 'sponsorship_valid_from, sponsorship_valid_to FROM tt.%I WHERE id = $1', TG_ARGV[1], TG_ARGV[0])
    INTO r USING NEW.public_revision_id;
  GET DIAGNOSTICS v_rows = ROW_COUNT;

  IF v_rows = 0 OR r.owner_id IS DISTINCT FROM NEW.id THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('Revision %s does not belong to %s %s', NEW.public_revision_id, TG_TABLE_NAME, NEW.id));
  END IF;

  IF r.content_kind = 'SPONSORED' THEN
    IF NOT tt.flag_enabled_for_write('ads.sponsored_publication') THEN
      PERFORM tt.raise_domain_error('FEATURE_DISABLED', 'ads.sponsored_publication is OFF: sponsored revisions cannot be published');
    END IF;
    IF r.sponsorship_approved_by IS NULL OR r.sponsorship_approved_at IS NULL THEN
      PERFORM tt.raise_domain_error('SPONSORSHIP_LABEL_REQUIRED', 'Sponsorship of revision ' || NEW.public_revision_id || ' is not approved');
    END IF;
    IF now() < r.sponsorship_valid_from OR now() >= r.sponsorship_valid_to THEN
      PERFORM tt.raise_domain_error('SPONSORSHIP_EXPIRED',
        format('Sponsorship window [%s, %s) does not include now', r.sponsorship_valid_from, r.sponsorship_valid_to));
    END IF;
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0307', 'tg_guard_sponsored_publish()');

SELECT tt_migration.add_trigger('0307', 'articles', 'trg_articles_guard_publish',
  'BEFORE INSERT OR UPDATE OF public_revision_id ON tt.articles FOR EACH ROW '
  || 'EXECUTE FUNCTION tt.tg_guard_sponsored_publish(''article_revisions'', ''article_id'')');
SELECT tt_migration.add_trigger('0307', 'videos', 'trg_videos_guard_publish',
  'BEFORE INSERT OR UPDATE OF public_revision_id ON tt.videos FOR EACH ROW '
  || 'EXECUTE FUNCTION tt.tg_guard_sponsored_publish(''video_revisions'', ''video_id'')');

-- TG_ARGV[0] = khóa cờ bắt buộc ON.
CREATE OR REPLACE FUNCTION tt.tg_guard_community_write()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NOT tt.flag_enabled_for_write(TG_ARGV[0]) THEN
    PERFORM tt.raise_domain_error('FEATURE_DISABLED', format('%s is OFF: write to tt.%s rejected', TG_ARGV[0], TG_TABLE_NAME));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0307', 'tg_guard_community_write()');

DO $$
BEGIN
  IF to_regclass('tt.comments') IS NOT NULL THEN
    PERFORM tt_migration.add_trigger('0307', 'comments', 'trg_comments_guard_write',
      'BEFORE INSERT ON tt.comments FOR EACH ROW EXECUTE FUNCTION tt.tg_guard_community_write(''community.video_comments'')');
  ELSE
    RAISE NOTICE '0307: tt.comments not found — community write guard skipped (add it when the comments table exists)';
  END IF;
END $$;

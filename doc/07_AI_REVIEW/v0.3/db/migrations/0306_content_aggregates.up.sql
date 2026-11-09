-- 0306 — Trường quản trị của bài/video theo AdminArticle/AdminVideo v0.3 (AIR-004, AIR-005, AIR-018, AIR-021)
-- workingRevisionId, publicRevisionId, ownerId, assigneeId, rightsStatus, rightsExpiresAt, scheduledAt, updatedAt, rowVersion.
-- Cột đã có trong schema 04 (cùng tên) được giữ nguyên. CHECK để NOT VALID — nội dung cũ cần backfill trước 0310:
--   • status theo enum ContentStatus v0.3 (9 giá trị);
--   • PUBLISHED phải có public_revision_id và rights_status = CLEARED (FR-NEWS-006, FR-VID-008);
--   • SCHEDULED phải có scheduled_at.

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_column('articles', 'status');
SELECT tt_migration.require_column('videos', 'status');
SELECT tt_migration.require_column('article_revisions', 'id');
SELECT tt_migration.require_column('video_revisions', 'id');

DO $$
DECLARE
  t   text;
  rev text;
BEGIN
  FOR t, rev IN SELECT * FROM (VALUES ('articles', 'article_revisions'), ('videos', 'video_revisions')) AS x(t, rev) LOOP
    PERFORM tt_migration.add_column('0306', t, 'working_revision_id', format('uuid REFERENCES tt.%I(id)', rev));
    PERFORM tt_migration.add_column('0306', t, 'public_revision_id', format('uuid REFERENCES tt.%I(id)', rev));
    PERFORM tt_migration.add_column('0306', t, 'owner_id', 'uuid REFERENCES tt.users(id)');
    PERFORM tt_migration.add_column('0306', t, 'assignee_id', 'uuid REFERENCES tt.users(id)');
    PERFORM tt_migration.add_column('0306', t, 'rights_status', 'varchar(12) NOT NULL DEFAULT ''UNVERIFIED''');
    PERFORM tt_migration.add_column('0306', t, 'rights_expires_at', 'timestamptz');
    PERFORM tt_migration.add_column('0306', t, 'scheduled_at', 'timestamptz');
    PERFORM tt_migration.add_column('0306', t, 'updated_at', 'timestamptz NOT NULL DEFAULT now()');
    PERFORM tt_migration.add_column('0306', t, 'row_version', 'bigint NOT NULL DEFAULT 1');

    PERFORM tt_migration.add_constraint('0306', t, 'ck_' || t || '_status',
      'CHECK (status IN (''DRAFT'', ''IN_REVIEW'', ''CHANGES_REQUESTED'', ''REJECTED'', ''APPROVED'', '
      || '''SCHEDULED'', ''PUBLISHED'', ''UNPUBLISHED'', ''RETRACTED'')) NOT VALID');
    PERFORM tt_migration.add_constraint('0306', t, 'ck_' || t || '_rights_status',
      'CHECK (rights_status IN (''UNVERIFIED'', ''CLEARED'', ''RESTRICTED'', ''EXPIRED'', ''REVOKED'')) NOT VALID');
    PERFORM tt_migration.add_constraint('0306', t, 'ck_' || t || '_published_requires_public_revision',
      'CHECK (status <> ''PUBLISHED'' OR public_revision_id IS NOT NULL) NOT VALID');
    PERFORM tt_migration.add_constraint('0306', t, 'ck_' || t || '_published_requires_cleared_rights',
      'CHECK (status <> ''PUBLISHED'' OR rights_status = ''CLEARED'') NOT VALID');
    PERFORM tt_migration.add_constraint('0306', t, 'ck_' || t || '_scheduled_requires_time',
      'CHECK (status <> ''SCHEDULED'' OR scheduled_at IS NOT NULL) NOT VALID');

    -- Work queue / danh sách CMS (filter status + assignee, sort updatedAt desc).
    PERFORM tt_migration.add_index('0306', t, 'ix_' || t || '_work_queue',
      format('ON tt.%I (status, assignee_id, updated_at DESC)', t));
    -- Job rights-expiry: nội dung đang public sắp hết quyền.
    PERFORM tt_migration.add_index('0306', t, 'ix_' || t || '_rights_expiry',
      format('ON tt.%I (rights_expires_at) WHERE status = ''PUBLISHED'' AND rights_expires_at IS NOT NULL', t));

    PERFORM tt_migration.add_trigger('0306', t, 'trg_' || t || '_touch',
      format('BEFORE UPDATE ON tt.%I FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()', t));
  END LOOP;
END $$;

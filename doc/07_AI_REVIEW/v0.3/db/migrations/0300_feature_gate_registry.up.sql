-- 0300 — Registry cờ tính năng (AIR-001), version + audit cho kiểm tra cờ trong transaction (AIR-019)
-- Danh sách cờ sinh từ x-feature-flags của 07_OPENAPI_TINHTE_v0.3_DRAFT.yaml — sửa registry ở spec trước, rồi sinh lại khối VALUES.
--
-- Đổi tên khóa cũ → khóa registry, GIỮ NGUYÊN trạng thái enabled. Nhiều khóa cũ cùng ánh xạ một khóa mới mà
-- trạng thái khác nhau → lấy OFF (an toàn) và NOTICE. Khóa không thuộc registry giữ nguyên và NOTICE.
-- Ánh xạ cũ → mới lưu ở tt_migration.flag_key_map_0300 để bản down khôi phục chính xác.

SELECT tt_migration.require_column('feature_gates', 'key');
SELECT tt_migration.require_column('feature_gates', 'enabled');
SELECT tt_migration.require_table('users');

SELECT tt_migration.add_column('0300', 'feature_gates', 'scope', 'varchar(10)');
SELECT tt_migration.add_column('0300', 'feature_gates', 'production_default', 'boolean NOT NULL DEFAULT false');
SELECT tt_migration.add_column('0300', 'feature_gates', 'description', 'text');
SELECT tt_migration.add_column('0300', 'feature_gates', 'approval_reference', 'varchar(120)');
SELECT tt_migration.add_column('0300', 'feature_gates', 'approved_by', 'uuid REFERENCES tt.users(id)');
SELECT tt_migration.add_column('0300', 'feature_gates', 'approved_at', 'timestamptz');
SELECT tt_migration.add_column('0300', 'feature_gates', 'version', 'bigint NOT NULL DEFAULT 1');
SELECT tt_migration.add_column('0300', 'feature_gates', 'updated_at', 'timestamptz NOT NULL DEFAULT now()');

CREATE TABLE IF NOT EXISTS tt_migration.flag_key_map_0300 (
  new_key     varchar(80) NOT NULL,
  old_key     varchar(80),          -- NULL: khóa mới được thêm, không có khóa cũ
  old_enabled boolean
);

DO $$
DECLARE
  f          record;
  v_count    integer;
  v_all_on   boolean;
  v_any_on   boolean;
  v_unmapped text;
BEGIN
  IF EXISTS (SELECT 1 FROM tt_migration.flag_key_map_0300) THEN
    RETURN;  -- đã chạy
  END IF;

  FOR f IN SELECT * FROM (VALUES
    ('community.video_comments', 'P0*', false, 'Public read/write of video comments, replies, likes and reports.',
     ARRAY['video_comments_enabled', 'community.comments.enabled', 'community.comments']::text[]),
    ('community.article_comments', 'P0*', false, 'Reserved. No endpoints in v0.3; PO decides P0* vs P1 (AIR-020).',
     ARRAY['article_comments_enabled']::text[]),
    ('community.member', 'P0*', false, 'Member OIDC login, eligibility, verification and /me endpoints.',
     ARRAY['community.member']::text[]),
    ('community.moderation', 'P0*', true, 'Staff moderation tooling. Stays ON while public community flags are OFF so existing content can be cleaned up (AIR-007). Only Security may turn it off.',
     ARRAY[]::text[]),
    ('community.ugc', 'P1', false, 'Reserved: user uploads and creator publishing. No endpoints.',
     ARRAY['community.ugc.enabled', 'user_upload_enabled', 'public_creator_publish_enabled']::text[]),
    ('community.appeals', 'P1', false, 'Member appeals against moderation decisions.',
     ARRAY['community.appeals']::text[]),
    ('member.bookmarks', 'P1', false, 'Saved videos for members.',
     ARRAY['member.bookmark']::text[]),
    ('ads.sponsored_publication', 'P0-ADS', false, 'Allows publishing revisions with contentKind=SPONSORED. Checked inside the publish transaction and again by scheduled publish jobs (AIR-002).',
     ARRAY['sponsored_content_publication', 'ads.sponsored_publication.enabled']::text[]),
    ('ads.display_slots', 'P0-ADS', false, 'Public static ad slots. When OFF, GET /ads/placements returns 200 with empty items (no layout gap).',
     ARRAY['display_ad_slots', 'ads.display_slots']::text[]),
    ('ads.shorts_instream', 'P1', false, 'In-stream ads inside Shorts. OFF in MVP.',
     ARRAY['shorts_instream_ads']::text[]),
    ('ads.campaign_advanced', 'P1', false, 'Campaign management and ad reports.',
     ARRAY['ads.campaign_advanced']::text[]),
    ('discovery.series', 'P1', false, 'Series pages (FR-NEWS-009).',
     ARRAY[]::text[]),
    ('discovery.search_suggestions', 'P1', false, 'Search suggestions (FR-DISC-006).',
     ARRAY[]::text[]),
    ('cms.related_suggestions', 'P1', false, 'Related content suggestions in CMS (FR-CMS-008).',
     ARRAY[]::text[]),
    ('analytics.operations', 'P1', false, 'Editorial/moderation operations metrics (FR-ANL-006).',
     ARRAY[]::text[]),
    ('ai.assistance', 'P2', false, 'AI metadata/transcript assistance.',
     ARRAY['ai.assistance']::text[]),
    ('ai.search', 'P2', false, 'AI search with sources.',
     ARRAY['ai.search']::text[])
  ) AS r(key, scope, production_default, description, legacy)
  LOOP
    INSERT INTO tt_migration.flag_key_map_0300 (new_key, old_key, old_enabled)
      SELECT f.key, g.key, g.enabled FROM tt.feature_gates g WHERE g.key = f.key OR g.key = ANY (f.legacy);

    SELECT count(*), bool_and(enabled), bool_or(enabled) INTO v_count, v_all_on, v_any_on
      FROM tt.feature_gates WHERE key = f.key OR key = ANY (f.legacy);

    IF v_count > 1 AND v_all_on IS DISTINCT FROM v_any_on THEN
      RAISE NOTICE '0300: legacy keys for % disagree (some ON, some OFF) → set OFF', f.key;
    END IF;
    IF v_count = 0 THEN
      INSERT INTO tt_migration.flag_key_map_0300 (new_key, old_key, old_enabled) VALUES (f.key, NULL, NULL);
    END IF;

    DELETE FROM tt.feature_gates WHERE key = f.key OR key = ANY (f.legacy);
    INSERT INTO tt.feature_gates (key, enabled, scope, production_default, description)
      VALUES (f.key, CASE WHEN v_count = 0 THEN f.production_default ELSE COALESCE(v_all_on, false) END,
              f.scope, f.production_default, f.description);
  END LOOP;

  SELECT string_agg(key, ', ' ORDER BY key) INTO v_unmapped FROM tt.feature_gates WHERE scope IS NULL;
  IF v_unmapped IS NOT NULL THEN
    RAISE NOTICE '0300: keys not in registry kept unchanged (review with TL): %', v_unmapped;
  END IF;
END $$;

-- Bật cờ phải có hồ sơ duyệt (trừ cờ mặc định ON như community.moderation). NOT VALID: kiểm dòng mới ngay,
-- dòng cũ được kiểm ở 0310 → nếu production đang bật cờ không có hồ sơ duyệt, 0310 sẽ dừng để bổ sung.
SELECT tt_migration.add_constraint('0300', 'feature_gates', 'ck_feature_gates_enabled_requires_approval',
  'CHECK (NOT enabled OR production_default OR (approval_reference IS NOT NULL AND approved_by IS NOT NULL AND approved_at IS NOT NULL)) NOT VALID');
SELECT tt_migration.add_constraint('0300', 'feature_gates', 'ck_feature_gates_scope',
  'CHECK (scope IS NULL OR scope IN (''P0'', ''P0*'', ''P0-ADS'', ''P1'', ''P2'')) NOT VALID');

-- Nhật ký thay đổi cờ (chỉ thêm). Ứng dụng đặt người thao tác bằng: SET LOCAL tt.actor_id = '<uuid>';
SELECT tt_migration.track_new_table('0300', 'feature_gate_changes');
CREATE TABLE IF NOT EXISTS tt.feature_gate_changes (
  id                 bigserial PRIMARY KEY,
  key                varchar(80) NOT NULL,
  old_enabled        boolean,
  new_enabled        boolean NOT NULL,
  approval_reference varchar(120),
  changed_by         uuid,
  changed_at         timestamptz NOT NULL DEFAULT now()
);

CREATE OR REPLACE FUNCTION tt.tg_feature_gate_versioning()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'UPDATE' THEN
    NEW.version := OLD.version + 1;
    NEW.updated_at := now();
  END IF;
  IF TG_OP = 'INSERT' OR NEW.enabled IS DISTINCT FROM OLD.enabled THEN
    INSERT INTO tt.feature_gate_changes (key, old_enabled, new_enabled, approval_reference, changed_by)
    VALUES (NEW.key, CASE WHEN TG_OP = 'UPDATE' THEN OLD.enabled END, NEW.enabled, NEW.approval_reference,
            NULLIF(current_setting('tt.actor_id', true), '')::uuid);
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0300', 'tg_feature_gate_versioning()');

SELECT tt_migration.add_trigger('0300', 'feature_gates', 'trg_feature_gates_versioning',
  'BEFORE INSERT OR UPDATE ON tt.feature_gates FOR EACH ROW EXECUTE FUNCTION tt.tg_feature_gate_versioning()');
SELECT tt_migration.add_trigger('0300', 'feature_gate_changes', 'trg_feature_gate_changes_append_only',
  'BEFORE UPDATE OR DELETE ON tt.feature_gate_changes FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');
SELECT tt_migration.add_trigger('0300', 'feature_gate_changes', 'trg_feature_gate_changes_no_truncate',
  'BEFORE TRUNCATE ON tt.feature_gate_changes FOR EACH STATEMENT EXECUTE FUNCTION tt.tg_append_only()');

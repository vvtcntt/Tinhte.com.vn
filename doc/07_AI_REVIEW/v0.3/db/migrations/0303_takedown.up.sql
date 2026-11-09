-- 0303 — Vòng đời takedown + bằng chứng chỉ thêm + đo lan truyền (AIR-008, AIR-018; FR-MOD-003/004, FR-VID-010, FR-DISC-004)
-- Khớp API v0.3: TakedownRecord, TakedownEvidenceAppend, TakedownActionRequest, PropagationStep.
-- Bất biến thực thi ở DB (phòng thủ nhiều lớp, không thay kiểm tra ở domain):
--   • yêu cầu takedown không xóa được; evidence/actions chỉ thêm (UPDATE/DELETE/TRUNCATE bị chặn);
--   • chuyển trạng thái hợp lệ: RECEIVED→TRIAGED→ACTIONED|REJECTED→CLOSED;
--   • ACTIONED phải có ít nhất một action (kiểm tra hoãn tới COMMIT);
--   • action UNPUBLISH/RESTRICT tự sinh 7 bước lan truyền PENDING để đo SLA.

SELECT tt_migration.require_table('users');

SELECT tt_migration.track_new_table('0303', 'takedown_requests');
CREATE TABLE IF NOT EXISTS tt.takedown_requests (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  content_type    varchar(16) NOT NULL CHECK (content_type IN ('ARTICLE', 'VIDEO', 'COMMENT', 'MEDIA')),
  content_id      uuid NOT NULL,
  requester_type  varchar(20) NOT NULL CHECK (requester_type IN ('RIGHTS_HOLDER', 'INDIVIDUAL', 'AUTHORITY', 'INTERNAL')),
  status          varchar(16) NOT NULL DEFAULT 'RECEIVED' CHECK (status IN ('RECEIVED', 'TRIAGED', 'ACTIONED', 'REJECTED', 'CLOSED')),
  reason          text NOT NULL CHECK (btrim(reason) <> ''),
  received_at     timestamptz NOT NULL DEFAULT now(),
  legal_reference varchar(200),
  assignee_id     uuid REFERENCES tt.users(id),
  sla_due_at      timestamptz,
  closed_at       timestamptz,
  created_by      uuid REFERENCES tt.users(id),
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  row_version     bigint NOT NULL DEFAULT 1,
  CONSTRAINT ck_takedown_requests_closed_at CHECK (status <> 'CLOSED' OR closed_at IS NOT NULL)
);

SELECT tt_migration.track_new_table('0303', 'takedown_evidence');
CREATE TABLE IF NOT EXISTS tt.takedown_evidence (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  takedown_id uuid NOT NULL REFERENCES tt.takedown_requests(id),
  ref         varchar(300) NOT NULL CHECK (btrim(ref) <> ''),
  note        text,
  added_by    uuid REFERENCES tt.users(id),
  added_at    timestamptz NOT NULL DEFAULT now()
);

SELECT tt_migration.track_new_table('0303', 'takedown_actions');
CREATE TABLE IF NOT EXISTS tt.takedown_actions (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  takedown_id uuid NOT NULL REFERENCES tt.takedown_requests(id),
  action      varchar(16) NOT NULL CHECK (action IN ('UNPUBLISH', 'RESTRICT', 'NO_ACTION')),
  reason      text NOT NULL CHECK (length(btrim(reason)) >= 5),
  actor_id    uuid REFERENCES tt.users(id),
  at          timestamptz NOT NULL DEFAULT now()
);

SELECT tt_migration.track_new_table('0303', 'takedown_propagation');
CREATE TABLE IF NOT EXISTS tt.takedown_propagation (
  takedown_id  uuid NOT NULL REFERENCES tt.takedown_requests(id),
  step         varchar(24) NOT NULL CHECK (step IN ('ORIGIN_DENY', 'PLAYBACK_GRANT_REVOKE', 'CDN_PURGE', 'SEARCH_REMOVE',
                                                    'SITEMAP_REMOVE', 'FEED_CACHE_PURGE', 'AD_SLOT_REMOVE')),
  status       varchar(16) NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING', 'DONE', 'FAILED', 'NOT_APPLICABLE')),
  attempts     integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
  last_error   text,
  completed_at timestamptz,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (takedown_id, step),
  CONSTRAINT ck_takedown_propagation_done_at CHECK (status <> 'DONE' OR completed_at IS NOT NULL)
);

SELECT tt_migration.add_index('0303', 'takedown_requests', 'ix_takedown_requests_queue',
  'ON tt.takedown_requests (status, sla_due_at)');
SELECT tt_migration.add_index('0303', 'takedown_requests', 'ix_takedown_requests_content',
  'ON tt.takedown_requests (content_type, content_id)');
SELECT tt_migration.add_index('0303', 'takedown_evidence', 'ix_takedown_evidence_takedown',
  'ON tt.takedown_evidence (takedown_id)');
SELECT tt_migration.add_index('0303', 'takedown_actions', 'ix_takedown_actions_takedown',
  'ON tt.takedown_actions (takedown_id)');
SELECT tt_migration.add_index('0303', 'takedown_propagation', 'ix_takedown_propagation_open',
  'ON tt.takedown_propagation (status, updated_at) WHERE status IN (''PENDING'', ''FAILED'')');

-- Chuyển trạng thái hợp lệ + tự đặt closed_at.
CREATE OR REPLACE FUNCTION tt.tg_takedown_status_transition()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status IS DISTINCT FROM OLD.status THEN
    IF NOT ((OLD.status = 'RECEIVED' AND NEW.status = 'TRIAGED')
         OR (OLD.status = 'TRIAGED'  AND NEW.status IN ('ACTIONED', 'REJECTED'))
         OR (OLD.status IN ('ACTIONED', 'REJECTED') AND NEW.status = 'CLOSED')) THEN
      PERFORM tt.raise_domain_error('STATE_CONFLICT',
        format('Takedown %s: transition %s → %s is not allowed', OLD.id, OLD.status, NEW.status));
    END IF;
    IF NEW.status = 'CLOSED' AND NEW.closed_at IS NULL THEN
      NEW.closed_at := now();
    END IF;
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0303', 'tg_takedown_status_transition()');

CREATE OR REPLACE FUNCTION tt.tg_takedown_actioned_requires_action()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'ACTIONED' AND NOT EXISTS (SELECT 1 FROM tt.takedown_actions a WHERE a.takedown_id = NEW.id) THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('Takedown %s cannot be ACTIONED without a linked action', NEW.id));
  END IF;
  RETURN NULL;
END $$;
SELECT tt_migration.track_function('0303', 'tg_takedown_actioned_requires_action()');

CREATE OR REPLACE FUNCTION tt.tg_takedown_seed_propagation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.action IN ('UNPUBLISH', 'RESTRICT') THEN
    INSERT INTO tt.takedown_propagation (takedown_id, step)
    SELECT NEW.takedown_id, s
      FROM unnest(ARRAY['ORIGIN_DENY', 'PLAYBACK_GRANT_REVOKE', 'CDN_PURGE', 'SEARCH_REMOVE',
                        'SITEMAP_REMOVE', 'FEED_CACHE_PURGE', 'AD_SLOT_REMOVE']) AS s
    ON CONFLICT (takedown_id, step) DO NOTHING;
  END IF;
  RETURN NULL;
END $$;
SELECT tt_migration.track_function('0303', 'tg_takedown_seed_propagation()');

SELECT tt_migration.add_trigger('0303', 'takedown_requests', 'trg_takedown_requests_transition',
  'BEFORE UPDATE OF status ON tt.takedown_requests FOR EACH ROW EXECUTE FUNCTION tt.tg_takedown_status_transition()');
SELECT tt_migration.add_trigger('0303', 'takedown_requests', 'trg_takedown_requests_touch',
  'BEFORE UPDATE ON tt.takedown_requests FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');
SELECT tt_migration.add_trigger('0303', 'takedown_requests', 'ctg_takedown_actioned_requires_action',
  'AFTER INSERT OR UPDATE OF status ON tt.takedown_requests DEFERRABLE INITIALLY DEFERRED FOR EACH ROW '
  || 'EXECUTE FUNCTION tt.tg_takedown_actioned_requires_action()', true);
SELECT tt_migration.add_trigger('0303', 'takedown_requests', 'trg_takedown_requests_no_delete',
  'BEFORE DELETE ON tt.takedown_requests FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');
SELECT tt_migration.add_trigger('0303', 'takedown_requests', 'trg_takedown_requests_no_truncate',
  'BEFORE TRUNCATE ON tt.takedown_requests FOR EACH STATEMENT EXECUTE FUNCTION tt.tg_append_only()');

SELECT tt_migration.add_trigger('0303', 'takedown_evidence', 'trg_takedown_evidence_append_only',
  'BEFORE UPDATE OR DELETE ON tt.takedown_evidence FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');
SELECT tt_migration.add_trigger('0303', 'takedown_evidence', 'trg_takedown_evidence_no_truncate',
  'BEFORE TRUNCATE ON tt.takedown_evidence FOR EACH STATEMENT EXECUTE FUNCTION tt.tg_append_only()');

SELECT tt_migration.add_trigger('0303', 'takedown_actions', 'trg_takedown_actions_append_only',
  'BEFORE UPDATE OR DELETE ON tt.takedown_actions FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');
SELECT tt_migration.add_trigger('0303', 'takedown_actions', 'trg_takedown_actions_no_truncate',
  'BEFORE TRUNCATE ON tt.takedown_actions FOR EACH STATEMENT EXECUTE FUNCTION tt.tg_append_only()');
SELECT tt_migration.add_trigger('0303', 'takedown_actions', 'trg_takedown_actions_seed_propagation',
  'AFTER INSERT ON tt.takedown_actions FOR EACH ROW EXECUTE FUNCTION tt.tg_takedown_seed_propagation()');

SELECT tt_migration.add_trigger('0303', 'takedown_propagation', 'trg_takedown_propagation_touch',
  'BEFORE UPDATE ON tt.takedown_propagation FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_updated_at()');
SELECT tt_migration.add_trigger('0303', 'takedown_propagation', 'trg_takedown_propagation_no_delete',
  'BEFORE DELETE ON tt.takedown_propagation FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');

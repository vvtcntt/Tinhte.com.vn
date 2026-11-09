-- 0304 — Moderation case: ưu tiên, người xử lý, SLA, lịch sử chỉ thêm (AIR-006; FR-MOD-001/002, FR-COM-007)
-- Khớp API v0.3: ModerationCase (priority, violationType, assigneeId, slaDueAt, rowVersion, history[]),
-- ModerationActionRequest (APPROVE/REJECT/HIDE/REMOVE/RESTORE/LOCK/DISMISS/ESCALATE) + ASSIGN cho /assign.
-- Trạng thái cũ trong schema 04 có thể khác enum v0.3 → CHECK để NOT VALID; DBA ánh xạ dữ liệu trước 0310.

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_column('moderation_cases', 'id');
SELECT tt_migration.require_column('moderation_cases', 'entity_type');
SELECT tt_migration.require_column('moderation_cases', 'status');

SELECT tt_migration.add_column('0304', 'moderation_cases', 'priority', 'varchar(10) NOT NULL DEFAULT ''NORMAL''');
SELECT tt_migration.add_column('0304', 'moderation_cases', 'violation_type', 'varchar(16)');
SELECT tt_migration.add_column('0304', 'moderation_cases', 'assignee_id', 'uuid REFERENCES tt.users(id)');
SELECT tt_migration.add_column('0304', 'moderation_cases', 'sla_due_at', 'timestamptz');
SELECT tt_migration.add_column('0304', 'moderation_cases', 'row_version', 'bigint NOT NULL DEFAULT 1');
SELECT tt_migration.add_column('0304', 'moderation_cases', 'updated_at', 'timestamptz NOT NULL DEFAULT now()');

SELECT tt_migration.add_constraint('0304', 'moderation_cases', 'ck_moderation_cases_status',
  'CHECK (status IN (''OPEN'', ''IN_REVIEW'', ''ACTIONED'', ''DISMISSED'', ''ESCALATED'')) NOT VALID');
SELECT tt_migration.add_constraint('0304', 'moderation_cases', 'ck_moderation_cases_priority',
  'CHECK (priority IN (''LOW'', ''NORMAL'', ''HIGH'', ''URGENT'')) NOT VALID');
SELECT tt_migration.add_constraint('0304', 'moderation_cases', 'ck_moderation_cases_violation_type',
  'CHECK (violation_type IS NULL OR violation_type IN (''SPAM'', ''ABUSE'', ''ILLEGAL'', ''PRIVACY'', ''COPYRIGHT'', ''OTHER'')) NOT VALID');
SELECT tt_migration.add_constraint('0304', 'moderation_cases', 'ck_moderation_cases_entity_type',
  'CHECK (entity_type IN (''COMMENT'', ''ARTICLE'', ''VIDEO'', ''USER'')) NOT VALID');

SELECT tt_migration.track_new_table('0304', 'moderation_case_history');
CREATE TABLE IF NOT EXISTS tt.moderation_case_history (
  id          bigserial PRIMARY KEY,
  case_id     uuid NOT NULL REFERENCES tt.moderation_cases(id),
  at          timestamptz NOT NULL DEFAULT now(),
  actor_id    uuid REFERENCES tt.users(id),
  action      varchar(12) NOT NULL CHECK (action IN ('APPROVE', 'REJECT', 'HIDE', 'REMOVE', 'RESTORE', 'LOCK',
                                                     'DISMISS', 'ESCALATE', 'ASSIGN')),
  reason      text,
  from_status varchar(20),
  to_status   varchar(20),
  CONSTRAINT ck_moderation_case_history_reason CHECK (action = 'ASSIGN' OR (reason IS NOT NULL AND length(btrim(reason)) >= 5))
);

SELECT tt_migration.add_index('0304', 'moderation_cases', 'ix_moderation_cases_queue',
  'ON tt.moderation_cases (status, priority, sla_due_at)');
SELECT tt_migration.add_index('0304', 'moderation_cases', 'ix_moderation_cases_assignee',
  'ON tt.moderation_cases (assignee_id) WHERE status IN (''OPEN'', ''IN_REVIEW'', ''ESCALATED'')');
SELECT tt_migration.add_index('0304', 'moderation_case_history', 'ix_moderation_case_history_case',
  'ON tt.moderation_case_history (case_id, at)');

SELECT tt_migration.add_trigger('0304', 'moderation_cases', 'trg_moderation_cases_touch',
  'BEFORE UPDATE ON tt.moderation_cases FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');
SELECT tt_migration.add_trigger('0304', 'moderation_case_history', 'trg_moderation_case_history_append_only',
  'BEFORE UPDATE OR DELETE ON tt.moderation_case_history FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');
SELECT tt_migration.add_trigger('0304', 'moderation_case_history', 'trg_moderation_case_history_no_truncate',
  'BEFORE TRUNCATE ON tt.moderation_case_history FOR EACH STATEMENT EXECUTE FUNCTION tt.tg_append_only()');

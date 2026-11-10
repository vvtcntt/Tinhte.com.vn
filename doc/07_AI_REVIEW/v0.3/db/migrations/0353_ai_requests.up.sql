-- 0353 — Nhật ký gọi AI cho gợi ý metadata (FR-AI-001, FR-AI-004; POST /admin/ai/metadata-suggestions, GET /admin/ai/audit)
-- DRAFT chờ TL/PO/Legal duyệt. Cờ ai.assistance (P2, production mặc định OFF, cần PO + Legal duyệt — x-feature-flags).
--
--   tt.ai_requests   MỘT dòng cho MỖI lần gửi nội dung ra nhà cung cấp AI (kể cả lần thất bại): ai gửi, nội dung nào,
--                    nhà cung cấp/model nào, kết quả, số token, thời gian. Là bằng chứng audit → chỉ thêm, không xóa.
--
-- Kiểm soát dữ liệu nhạy cảm (FR-AI-004): KHÔNG lưu prompt, nội dung gửi đi hay câu trả lời của model — chỉ lưu độ dài và
-- sha256 của nội dung gửi đi (đủ để chứng minh "đã gửi đúng bản này" mà không nhân bản dữ liệu ra một bảng khác).
--
-- Cổng cờ trong transaction ghi: INSERT bị chặn khi ai.assistance OFF (FEATURE_DISABLED) — dùng lại tt.tg_guard_community_write
-- của 0307 (hàm chung "cờ phải ON mới được ghi bảng này"). Ứng dụng tự kiểm cờ trước, trigger là lớp chặn cuối.
--
-- Vòng đời một dòng: PENDING (tạo trước khi gọi nhà cung cấp) → SUCCEEDED | FAILED (một lần, sau khi có kết quả).
-- Cột định danh (ai, cái gì, nhà cung cấp, model, hash) không sửa được; dòng đã kết thúc không sửa được (STATE_CONFLICT).
-- Bảng mới hoàn toàn → không có CHECK NOT VALID, không cần migration VALIDATE.

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_table('feature_gates');

DO $$
BEGIN
  IF to_regprocedure('tt.tg_guard_community_write()') IS NULL OR to_regprocedure('tt.flag_enabled_for_write(text)') IS NULL THEN
    RAISE EXCEPTION 'Precondition failed: function tt.tg_guard_community_write() / tt.flag_enabled_for_write(text) (migration 0307) is missing';
  END IF;
END $$;

SELECT tt_migration.track_new_table('0353', 'ai_requests');
CREATE TABLE IF NOT EXISTS tt.ai_requests (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  actor_id          uuid NOT NULL REFERENCES tt.users(id),
  action            varchar(40) NOT NULL,
  object_type       varchar(10) NOT NULL,
  object_id         uuid NOT NULL,
  provider_key      varchar(40) NOT NULL,
  model             varchar(80) NOT NULL,
  status            varchar(10) NOT NULL DEFAULT 'PENDING',
  input_chars       integer NOT NULL,
  input_sha256      char(64) NOT NULL,
  prompt_tokens     integer,
  completion_tokens integer,
  latency_ms        integer,
  error_code        varchar(40),
  created_at        timestamptz NOT NULL DEFAULT now(),
  completed_at      timestamptz,
  CONSTRAINT ck_ai_requests_action CHECK (action IN ('ai.suggest_tags', 'ai.suggest_summary')),
  CONSTRAINT ck_ai_requests_object_type CHECK (object_type IN ('ARTICLE', 'VIDEO')),
  CONSTRAINT ck_ai_requests_status CHECK (status IN ('PENDING', 'SUCCEEDED', 'FAILED')),
  CONSTRAINT ck_ai_requests_provider CHECK (provider_key ~ '^[a-z0-9][a-z0-9_-]{0,39}$' AND btrim(model) <> ''),
  CONSTRAINT ck_ai_requests_input CHECK (input_chars >= 0 AND input_sha256 ~ '^[0-9a-f]{64}$'),
  CONSTRAINT ck_ai_requests_usage CHECK ((prompt_tokens IS NULL OR prompt_tokens >= 0)
                                         AND (completion_tokens IS NULL OR completion_tokens >= 0)
                                         AND (latency_ms IS NULL OR latency_ms >= 0)),
  -- Kết thúc ⇔ có completed_at; FAILED phải nêu mã lỗi, SUCCEEDED thì không.
  CONSTRAINT ck_ai_requests_completion CHECK ((status = 'PENDING') = (completed_at IS NULL)
                                              AND (status = 'FAILED') = (error_code IS NOT NULL))
);

-- Chỉ cho PENDING → SUCCEEDED/FAILED một lần; không đổi cột định danh.
CREATE OR REPLACE FUNCTION tt.tg_ai_requests_guard_update()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status <> 'PENDING' OR NEW.status NOT IN ('SUCCEEDED', 'FAILED') THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT',
      format('AI request %s is %s: only PENDING → SUCCEEDED/FAILED is allowed', OLD.id, OLD.status));
  END IF;
  IF (NEW.id, NEW.actor_id, NEW.action, NEW.object_type, NEW.object_id, NEW.provider_key, NEW.model, NEW.input_chars,
      NEW.input_sha256, NEW.created_at)
     IS DISTINCT FROM
     (OLD.id, OLD.actor_id, OLD.action, OLD.object_type, OLD.object_id, OLD.provider_key, OLD.model, OLD.input_chars,
      OLD.input_sha256, OLD.created_at) THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('AI request %s: audit identity columns cannot change', OLD.id));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0353', 'tg_ai_requests_guard_update()');

SELECT tt_migration.add_trigger('0353', 'ai_requests', 'trg_ai_requests_feature_gate',
  'BEFORE INSERT ON tt.ai_requests FOR EACH ROW EXECUTE FUNCTION tt.tg_guard_community_write(''ai.assistance'')');
SELECT tt_migration.add_trigger('0353', 'ai_requests', 'trg_ai_requests_guard_update',
  'BEFORE UPDATE ON tt.ai_requests FOR EACH ROW EXECUTE FUNCTION tt.tg_ai_requests_guard_update()');
SELECT tt_migration.add_trigger('0353', 'ai_requests', 'trg_ai_requests_append_only',
  'BEFORE DELETE ON tt.ai_requests FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');
SELECT tt_migration.add_trigger('0353', 'ai_requests', 'trg_ai_requests_no_truncate',
  'BEFORE TRUNCATE ON tt.ai_requests FOR EACH STATEMENT EXECUTE FUNCTION tt.tg_append_only()');

-- GET /admin/ai/audit: mới nhất trước, cursor (created_at, id); lọc theo người.
SELECT tt_migration.add_index('0353', 'ai_requests', 'ix_ai_requests_audit', 'ON tt.ai_requests (created_at DESC, id DESC)');
-- Hạn mức theo người trong cửa sổ trượt (RATE_LIMITED) + lọc actorId.
SELECT tt_migration.add_index('0353', 'ai_requests', 'ix_ai_requests_actor', 'ON tt.ai_requests (actor_id, created_at DESC)');

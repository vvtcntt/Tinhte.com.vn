-- 0354 down — trả CHECK ck_ai_requests_action về 2 hành động của 0353.
-- TỪ CHỐI nếu nhật ký đã có dòng 'ai.rewrite': tt.ai_requests là bằng chứng audit chỉ-thêm (FR-AI-004), không xóa được để cho qua.

DO $$
BEGIN
  IF to_regclass('tt.ai_requests') IS NOT NULL AND EXISTS (SELECT 1 FROM tt.ai_requests WHERE action = 'ai.rewrite') THEN
    RAISE EXCEPTION 'Refusing to roll back 0354: tt.ai_requests already records ai.rewrite calls (AI usage audit trail). Export the data and obtain approval before rolling back.';
  END IF;
END $$;

SELECT tt_migration.rollback('0354');

-- CHECK gốc của 0353 (nằm trong CREATE TABLE nên không thuộc ledger — down 0353 gỡ cùng bảng).
DO $$
BEGIN
  IF to_regclass('tt.ai_requests') IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'tt.ai_requests'::regclass AND conname = 'ck_ai_requests_action') THEN
    ALTER TABLE tt.ai_requests ADD CONSTRAINT ck_ai_requests_action CHECK (action IN ('ai.suggest_tags', 'ai.suggest_summary'));
  END IF;
END $$;

-- 0354 — Hành động 'ai.rewrite' trong nhật ký gọi AI (POST /admin/ai/rewrite; FR-AI-001, FR-AI-004)
-- DRAFT chờ TL/PO/Legal duyệt. Cờ ai.assistance giữ OFF (production mặc định OFF, cần PO + Legal duyệt — x-feature-flags).
--
-- Viết lại đoạn văn người biên tập đã chọn dùng chung bảng tt.ai_requests với gợi ý metadata: cùng cổng cờ, hạn mức theo người,
-- chỉ lưu độ dài + sha256 của đoạn gửi đi. 0353 tạo CHECK ck_ai_requests_action cùng CREATE TABLE nên không có bản ghi ledger;
-- ở đây thay bằng CHECK cùng tên có thêm 'ai.rewrite', dạng NOT VALID (bảng đã có thể có dữ liệu) và VALIDATE ở 0356 — giống
-- 0351/0352. Nới danh sách không thể làm dòng cũ vi phạm. Chạy lại an toàn: chỉ thay khi CHECK hiện tại chưa có 'ai.rewrite'.
-- Danh mục action phải khớp AiTasks/AiRewriteModes (C#) và mô tả getAdminAiAudit (test AiUnitTests đọc migration mới nhất định nghĩa CHECK này).

SELECT tt_migration.require_table('ai_requests');   -- cần 0353

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
     WHERE conrelid = 'tt.ai_requests'::regclass AND conname = 'ck_ai_requests_action'
       AND pg_get_constraintdef(oid) LIKE '%''ai.rewrite''%'
  ) THEN
    ALTER TABLE tt.ai_requests DROP CONSTRAINT IF EXISTS ck_ai_requests_action;
  END IF;
END $$;

SELECT tt_migration.add_constraint('0354', 'ai_requests', 'ck_ai_requests_action',
  'CHECK (action IN (''ai.suggest_tags'', ''ai.suggest_summary'', ''ai.rewrite'')) NOT VALID');

-- 0353 down — gỡ nhật ký gọi AI. Từ chối khi đã có dòng: đó là bằng chứng audit FR-AI-004 (xuất dữ liệu và xin duyệt trước).
SELECT tt_migration.refuse_if_rows('0353', 'ai_requests', 'AI usage audit trail');
SELECT tt_migration.rollback('0353');

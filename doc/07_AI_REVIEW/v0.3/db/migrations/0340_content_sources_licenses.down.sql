-- 0340 down — TỪ CHỐI nếu danh mục đã có dữ liệu: giấy phép là bằng chứng pháp lý, nguồn được hồ sơ thu thập/bài viết tham chiếu.
-- Xuất dữ liệu + duyệt trước. Chạy sau down 0341 (hồ sơ thu thập tham chiếu tt.content_sources/tt.licenses).
SELECT tt_migration.refuse_if_rows('0340', 'licenses', 'licence records and contract evidence references');
SELECT tt_migration.refuse_if_rows('0340', 'content_sources', 'content source catalogue');
SELECT tt_migration.rollback('0340');

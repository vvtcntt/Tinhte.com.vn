-- 0342 — Bộ thu thập Radar tin GĐ1 (ADR-0008 §3, §5, §6; BA-01 BR-06) · FR-AGG-002/003 · DRAFT chờ TL + DBA duyệt
-- 1) agg_feeds.failing_since: lúc bắt đầu chuỗi lỗi liên tiếp hiện tại (NULL khi đang tốt). Dùng cho BR-06 "lỗi liên tục 24 giờ ⇒ tự PAUSED"
--    mà không phải quét agg_fetch_log (chỉ giữ 30 ngày). Worker đặt khi lỗi, xóa khi tải được; CMS đổi URL cũng xóa.
-- 2) row_version của feed là token If-Match cho CMS: chỉ tăng khi cột CMS sửa được thay đổi (url, format, is_enabled, schedule_minutes,
--    category_mapping). Cập nhật của worker (lease, ETag, sức khỏe, lịch) không còn làm ETag của BTV hết hiệu lực mỗi lần thu thập.
-- 3) Index cho danh sách mục tin theo site (listAdminAggregationItems: site_key + first_seen_at giảm dần).
-- Bảng mới của 0341 → không cần NOT VALID/VALIDATE (CLAUDE.md §3 chỉ áp cho CHECK trên bảng cũ); 0342 không thêm CHECK.

SELECT tt_migration.require_table('agg_feeds');
SELECT tt_migration.require_table('agg_items');

SELECT tt_migration.add_column('0342', 'agg_feeds', 'failing_since', 'timestamptz');

-- Trigger cũ (0341) tăng row_version ở MỌI UPDATE; trigger mới chỉ khi cột của CMS nằm trong SET. Down 0342 tạo lại trigger cũ.
DROP TRIGGER IF EXISTS trg_agg_feeds_touch ON tt.agg_feeds;
SELECT tt_migration.add_trigger('0342', 'agg_feeds', 'trg_agg_feeds_touch_cms',
  'BEFORE UPDATE OF url, format, is_enabled, schedule_minutes, category_mapping ON tt.agg_feeds FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');

SELECT tt_migration.add_index('0342', 'agg_items', 'ix_agg_items_site_recent', 'ON tt.agg_items (site_key, first_seen_at DESC, id DESC)');

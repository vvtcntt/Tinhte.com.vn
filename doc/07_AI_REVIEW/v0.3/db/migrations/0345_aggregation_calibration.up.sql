-- 0345 — Dữ liệu hiệu chỉnh gom cụm Radar tin (ADR-0008 §7; BA-01 BR-23: "tách/gộp thủ công được ghi lại để cải thiện ngưỡng") · FR-AGG-004 · DRAFT
-- agg_cluster_actions ghi thêm, ở dòng của cụm trong đường dẫn thao tác:
--   item_ids       = mục bị chuyển (tách: mục sang cụm mới; gộp: mục của cụm nguồn),
--   other_item_ids = phía bên kia lúc thao tác (tách: mục ở lại; gộp: mục của cụm đích trước khi gộp).
-- Nhờ đó cặp "khác sự kiện" (tách) và "cùng sự kiện" (gộp) cố định tại thời điểm BTV quyết định, không phụ thuộc thay đổi sau đó.
-- Bảng chỉ thêm (0341): thêm cột cho phép NULL không đụng dòng cũ; không thêm CHECK.

SELECT tt_migration.require_table('agg_cluster_actions');

SELECT tt_migration.add_column('0345', 'agg_cluster_actions', 'item_ids', 'uuid[]');
SELECT tt_migration.add_column('0345', 'agg_cluster_actions', 'other_item_ids', 'uuid[]');
SELECT tt_migration.add_index('0345', 'agg_cluster_actions', 'ix_agg_cluster_actions_calibration',
  'ON tt.agg_cluster_actions (created_at) WHERE action IN (''SPLIT'', ''MERGE'') AND item_ids IS NOT NULL');

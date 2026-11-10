-- 0341 down — TỪ CHỐI nếu đã có lịch sử duyệt nguồn, lịch sử trạng thái hay hành động trên cụm (audit), hoặc cờ
-- aggregation.radar đang bật (đã có hồ sơ duyệt của PO). Mục tin/cụm/nhật ký thu thập bị xóa cùng bảng.
SELECT tt_migration.refuse_if_rows('0341', 'agg_source_approvals', 'source usage-mode/licence approval audit trail');
SELECT tt_migration.refuse_if_rows('0341', 'agg_source_status_history', 'source pause/resume/retire audit trail');
SELECT tt_migration.refuse_if_rows('0341', 'agg_cluster_actions', 'editor actions on clusters');

DO $$
BEGIN
  IF to_regclass('tt_migration.flag_added_0341') IS NULL THEN
    RETURN;
  END IF;
  IF EXISTS (SELECT 1 FROM tt.feature_gates g JOIN tt_migration.flag_added_0341 a ON a.key = g.key WHERE g.enabled) THEN
    RAISE EXCEPTION 'Refusing to roll back 0341: aggregation.radar is enabled. Turn it off (with approval) first.';
  END IF;
  DELETE FROM tt.feature_gates g USING tt_migration.flag_added_0341 a WHERE a.key = g.key;
END $$;
DROP TABLE IF EXISTS tt_migration.flag_added_0341;

SELECT tt_migration.rollback('0341');

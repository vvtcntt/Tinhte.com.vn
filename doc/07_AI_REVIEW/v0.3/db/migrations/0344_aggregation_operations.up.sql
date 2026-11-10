-- 0344 — Vận hành Radar tin GĐ1 (ADR-0008 §4, §9; BA-01 BR-06, NFR-AGG-06) · FR-AGG-002 · DRAFT chờ TL + DBA duyệt
-- 1) agg_alerts: hàng đợi cảnh báo (outbox) khi worker tự đổi trạng thái nguồn — ENABLED → DEGRADED (5 lần lỗi liên tiếp) và → PAUSED
--    (lỗi liên tục 24 giờ). Ghi trong CÙNG transaction đổi trạng thái nên không mất; worker gửi ra kênh ngoài (webhook, nếu cấu hình) rồi đánh
--    dấu delivered_at. CMS đọc qua getAdminAggregationStats. Không chỉ thêm: delivered_at/attempts/last_error được cập nhật.
-- 2) agg_worker_heartbeats: mỗi tiến trình worker ghi nhịp tim mỗi vòng quét (giám sát: worker còn chạy không, lỗi gần nhất).
-- Dọn agg_fetch_log quá 30 ngày do worker làm (index ix_agg_fetch_log_retention của 0341).

SELECT tt_migration.require_table('agg_source_profiles');

SELECT tt_migration.track_new_table('0344', 'agg_alerts');
CREATE TABLE IF NOT EXISTS tt.agg_alerts (
  id                bigserial PRIMARY KEY,
  content_source_id uuid NOT NULL REFERENCES tt.agg_source_profiles(content_source_id),
  kind              varchar(20) NOT NULL,
  message           varchar(2000) NOT NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  delivered_at      timestamptz,
  attempts          integer NOT NULL DEFAULT 0,
  last_error        varchar(500),
  CONSTRAINT ck_agg_alerts_kind CHECK (kind IN ('SOURCE_AUTO_PAUSED', 'SOURCE_DEGRADED')),
  CONSTRAINT ck_agg_alerts_attempts CHECK (attempts >= 0),
  CONSTRAINT ck_agg_alerts_message_not_blank CHECK (length(btrim(message)) > 0)
);
SELECT tt_migration.add_index('0344', 'agg_alerts', 'ix_agg_alerts_pending', 'ON tt.agg_alerts (created_at, id) WHERE delivered_at IS NULL');
SELECT tt_migration.add_index('0344', 'agg_alerts', 'ix_agg_alerts_source', 'ON tt.agg_alerts (content_source_id, created_at DESC)');

SELECT tt_migration.track_new_table('0344', 'agg_worker_heartbeats');
CREATE TABLE IF NOT EXISTS tt.agg_worker_heartbeats (
  worker_id         varchar(100) PRIMARY KEY,
  host              varchar(200),
  version           varchar(50),
  started_at        timestamptz NOT NULL,
  last_seen_at      timestamptz NOT NULL DEFAULT now(),
  last_cycle_feeds  integer NOT NULL DEFAULT 0,
  radar_enabled     boolean NOT NULL DEFAULT false,
  last_error        varchar(500),
  CONSTRAINT ck_agg_worker_heartbeats_feeds CHECK (last_cycle_feeds >= 0)
);
SELECT tt_migration.add_index('0344', 'agg_worker_heartbeats', 'ix_agg_worker_heartbeats_seen', 'ON tt.agg_worker_heartbeats (last_seen_at DESC)');

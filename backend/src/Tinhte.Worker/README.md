# Tinhte.Worker — worker thu thập Radar tin (ADR-0008 §3–§4)

> DRAFT — ADR-0008 vẫn PROPOSED (A-1 chờ TL, A-4 chờ Security). Image chưa được build thử trên máy dựng (không có Docker) và CI chưa build image.

Tiến trình riêng (generic host), **không** chạy trong API. Mỗi vòng quét:

1. Kiểm cờ `aggregation.radar` (tt.feature_gates). OFF ⇒ không thu thập, vẫn ghi nhịp tim.
2. Thuê tối đa `Aggregation:Worker:Parallelism` feed đến hạn (`FOR UPDATE SKIP LOCKED` + `lease_until`), tải song song qua bộ thu thập an toàn (SSRF, robots.txt, 20 giây, 5 MB), ghi mục tin + gom cụm trong một transaction.
3. Mỗi `Aggregation:Worker:MaintenanceMinutes` phút: đóng cụm hết cửa sổ 48 giờ, gom bù, tính lại điểm, dọn `agg_fetch_log` cũ hơn `Aggregation:FetchLogRetentionDays` (30) ngày.
4. Gửi cảnh báo nguồn chờ gửi (`tt.agg_alerts`) ra webhook nếu có cấu hình.
5. Ghi nhịp tim (`tt.agg_worker_heartbeats`) và chạm tệp liveness (nếu cấu hình).

Chạy nhiều bản cùng lúc an toàn (lease + khóa tư vấn theo site). Bản chết giữa chừng: lease hết hạn sau `LeaseSeconds`, bản khác lấy lại feed.

## Triển khai

Thứ tự mỗi lần phát hành: `Tinhte.DbMigrator up` (migration, bước riêng) → API → worker.

```bash
docker build -f backend/src/Tinhte.Worker/Dockerfile -t tinhte-worker .
```

| Cấu hình | Bắt buộc | Ghi chú |
|---|---|---|
| `ConnectionStrings__Tinhte` | có | Secret. Không có ⇒ worker thoát mã 1 |
| `Sites__Environment` | khuyến nghị | `production` / `staging` / `local` — dùng để dựng User-Agent `TinhteNewsBot/x.y (+<origin site public-web>/bot)` |
| `Aggregation__Alerts__WebhookUrl` | không | https; incoming webhook kiểu Slack/Teams/Google Chat. Secret. Không có ⇒ cảnh báo chỉ hiện trên CMS |
| `Aggregation__Worker__LivenessFile` | không | Image đặt sẵn `/tmp/tinhte-worker.alive` cho `HEALTHCHECK` |
| `Aggregation__Worker__Parallelism` / `PollSeconds` / `LeaseSeconds` / `MaintenanceMinutes` | không | 8 / 30 / 120 / 5 |
| `Aggregation__NewsroomTimeZone`, `Aggregation__Clustering__*`, `Aggregation__Scoring__*`, `Aggregation__Classification__*` | không | Xem `appsettings.json`, ADR-0008 mục tiến độ |

- Mạng: worker cần **ra** Internet (443) tới các nguồn RSS và webhook; **không** cần vào. Không đặt proxy HTTP cho worker: bộ thu thập tắt proxy để kiểm IP đích lúc kết nối (chống SSRF).
- Chạy local bằng Docker: `docker compose --profile worker up -d worker` (sau khi `postgres` chạy và đã migrate).
- Chạy local không Docker: `dotnet run --project backend/src/Tinhte.Worker` với `ConnectionStrings__Tinhte`.
- Dừng an toàn: SIGTERM — vòng đang chạy bị hủy, lease còn lại hết hạn tự nhiên.
- Rollback: dừng worker là đủ (API/CMS không phụ thuộc worker); tắt cờ `aggregation.radar` cũng dừng thu thập ở mọi bản.

## Giám sát

| Tín hiệu | Ở đâu | Gợi ý cảnh báo |
|---|---|---|
| Nhịp tim | `tt.agg_worker_heartbeats`; CMS → Radar tin (`stats.workers`, `healthy` = có nhịp tim trong 5 phút) | Không có worker `healthy` nào trong 10 phút khi cờ ON |
| Liveness container | `HEALTHCHECK` (tệp được chạm trong 3 phút, chỉ khi vòng quét không lỗi hạ tầng) | Container unhealthy |
| Sức khỏe nguồn | CMS → Nguồn thu thập; `stats.fetchSuccessRate24h`, `stats.sources[].worstFeedHealth` | Tỉ lệ thành công 24 giờ < 80% |
| Cảnh báo nguồn | `tt.agg_alerts` (tự DEGRADED / tự PAUSED theo BR-06); CMS hiện banner; webhook nếu cấu hình | Cảnh báo chưa gửi được sau 10 lần thử (`attempts = 10`, `delivered_at IS NULL`) |
| Số đo | Meter `Tinhte.Aggregation`: `aggregation.feed.fetches` (outcome, failure), `aggregation.items.new/updated`, `aggregation.feed.fetch.duration` (ms), `aggregation.alerts`, `aggregation.fetch_log.purged` | Đọc tạm bằng `dotnet-counters monitor -n Tinhte.Worker --counters Tinhte.Aggregation`; exporter (OpenTelemetry/Prometheus) chờ WBS-021 chọn nền tảng |
| Log | stdout (JSON tùy nền tảng); mức `Warning`: đích không an toàn, nguồn tự PAUSED, gửi cảnh báo lỗi | — |

Truy vấn nhanh khi trực:

```sql
SELECT worker_id, last_seen_at, radar_enabled, last_cycle_feeds, last_error FROM tt.agg_worker_heartbeats ORDER BY last_seen_at DESC LIMIT 10;
SELECT kind, content_source_id, message, created_at, attempts, last_error FROM tt.agg_alerts WHERE delivered_at IS NULL ORDER BY created_at;
SELECT outcome, failure, count(*) FROM tt.agg_fetch_log WHERE started_at > now() - interval '1 hour' GROUP BY 1, 2;
```

## Xử lý sự cố

- **Nguồn tự PAUSED**: xem lý do trên CMS (Nguồn thu thập → nguồn), sửa feed (URL/định dạng) hoặc liên hệ nguồn, rồi bấm *Tiếp tục*. Nguồn bị chặn bởi robots.txt (`BLOCKED_BY_ROBOTS`): **không** né chặn — liên hệ nguồn hoặc ngừng nguồn.
- **Không worker nào healthy**: kiểm container/log; `last_error` trong nhịp tim; kết nối DB.
- **Bảng `agg_fetch_log` phình**: kiểm worker còn chạy bảo trì (log "Purged …"); giảm `FetchLogRetentionDays` nếu Legal/DBA đồng ý.

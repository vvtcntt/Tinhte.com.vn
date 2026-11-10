# Tinhte.Worker — worker thu thập Radar tin (ADR-0008 §3–§4)

> DRAFT — ADR-0008 vẫn PROPOSED (A-1 chờ TL, A-4 chờ Security). Image chưa được build thử trên máy dựng (không có Docker); job CI `worker-image` (build + chạy thử + quét Trivy) đã thêm nhưng chưa chạy trên GitHub Actions.

Tiến trình riêng (generic host), **không** chạy trong API. Mỗi vòng quét:

1. Kiểm cờ `aggregation.radar` (tt.feature_gates). OFF ⇒ không thu thập, vẫn ghi nhịp tim.
2. Thuê tối đa `Aggregation:Worker:Parallelism` feed đến hạn (`FOR UPDATE SKIP LOCKED` + `lease_until`), tải song song qua bộ thu thập an toàn (SSRF, robots.txt, 20 giây, 5 MB), ghi mục tin + gom cụm trong một transaction.
3. Mỗi `Aggregation:Worker:MaintenanceMinutes` phút: đóng cụm hết cửa sổ 48 giờ, gom bù, tính lại điểm, dọn `agg_fetch_log` cũ hơn `Aggregation:FetchLogRetentionDays` (30) ngày, xóa mục tin **không thuộc cụm nào** cũ hơn `Aggregation:UnclusteredItemRetentionDays` (90) ngày (BA-01 L-03; mục trong cụm — kể cả cụm đã có bài — được giữ).
4. Gửi cảnh báo nguồn chờ gửi (`tt.agg_alerts`) ra webhook nếu có cấu hình (tùy chọn).

Quy tắc quét (chủ dự án chốt 10/11/2026, BA-01 P-02): feed mới mặc định đọc **30 phút** một lần; worker **chỉ nhận tin trong ngày** (bỏ mục có thời điểm trước 00:00 hôm nay theo `Aggregation:NewsroomTimeZone`; tắt bằng `Aggregation__Intake__TodayOnly=false` để về quy tắc 24 giờ lần đầu); gom cụm chỉ so tin **cùng chuyên mục**. Bước AI xác định tin trùng chủ đề (cờ `aggregation.ai_dedup`) **chưa nối**: bản mặc định không gọi AI.
5. Ghi nhịp tim (`tt.agg_worker_heartbeats`) và chạm tệp liveness (nếu cấu hình).

Chạy nhiều bản cùng lúc an toàn (lease + khóa tư vấn theo site). Bản chết giữa chừng: lease hết hạn sau `LeaseSeconds`, bản khác lấy lại feed.

## Triển khai

Thứ tự mỗi lần phát hành: `Tinhte.DbMigrator up` (migration, bước riêng) → API → worker.

**Điều kiện mạng — bắt buộc (Security duyệt bộ thu thập kèm điều kiện, BA-01 S-01 / ADR-0008 A-4, 10/11/2026):**
- worker chạy trong **mạng riêng** (subnet/VPC/network riêng của môi trường), **không** chung mạng với máy dev hay dịch vụ quản trị;
- tường lửa chiều **ra**: chỉ cho **Internet cổng 443** (nguồn RSS, endpoint OTLP của Grafana Cloud, webhook nếu dùng) và **kết nối tới DB** (cổng PostgreSQL của môi trường); chặn mọi cổng/đích khác, gồm dải nội bộ và địa chỉ metadata của nhà cung cấp đám mây;
- chiều **vào**: không mở cổng nào (worker không nghe cổng);
- DNS: dùng resolver của môi trường; bộ thu thập vẫn tự kiểm IP đích lúc kết nối (SSRF) — tường lửa là lớp thứ hai, không thay thế.
Môi trường chưa đáp ứng các điều kiện này thì **không** bật cờ `aggregation.radar` ở đó.

```bash
docker build -f backend/src/Tinhte.Worker/Dockerfile -t tinhte-worker .
```

| Cấu hình | Bắt buộc | Ghi chú |
|---|---|---|
| `ConnectionStrings__Tinhte` | có | Secret. Không có ⇒ worker thoát mã 1 |
| `Sites__Environment` | khuyến nghị | `production` / `staging` / `local` — dùng để dựng User-Agent `TinhteNewsBot/x.y (+<origin site public-web>/bot)` |
| `Aggregation__Alerts__WebhookUrl` | không (tùy chọn) | https; incoming webhook kiểu Slack/Teams/Google Chat. Secret. Kênh cảnh báo chính là email của Grafana Cloud (mục Giám sát); không có webhook ⇒ cảnh báo nguồn vẫn hiện trên CMS và qua số đo `aggregation.alerts` |
| `Aggregation__Worker__LivenessFile` | không | Image đặt sẵn `/tmp/tinhte-worker.alive` cho `HEALTHCHECK` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` (hoặc `Telemetry__Otlp__Endpoint`) | có ở staging/production (điều kiện thí điểm) | Endpoint OTLP của stack Grafana Cloud; kèm `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` và `OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic <instance:token>` (secret). Không đặt ⇒ không gửi số đo ra ngoài |
| `Aggregation__Intake__TodayOnly` | không | `true` (mặc định): chỉ nhận tin trong ngày theo giờ tòa soạn; `false`: quy tắc 24 giờ lần đầu (BR-14) |
| `Aggregation__UnclusteredItemRetentionDays` | không | 90 (7–3650): ngày giữ mục tin không thuộc cụm (L-03) |
| `Aggregation__Worker__Parallelism` / `PollSeconds` / `LeaseSeconds` / `MaintenanceMinutes` | không | 8 / 30 / 120 / 5 |
| `Aggregation__NewsroomTimeZone`, `Aggregation__Clustering__*`, `Aggregation__Scoring__*`, `Aggregation__Classification__*` | không | Xem `appsettings.json`, ADR-0008 mục tiến độ |

- Mạng: worker cần **ra** Internet (443) tới các nguồn RSS và webhook; **không** cần vào. Không đặt proxy HTTP cho worker: bộ thu thập tắt proxy để kiểm IP đích lúc kết nối (chống SSRF).
- Chạy local bằng Docker: `docker compose --profile worker up -d worker` (sau khi `postgres` chạy và đã migrate).
- Chạy local không Docker: `dotnet run --project backend/src/Tinhte.Worker` với `ConnectionStrings__Tinhte`.
- Dừng an toàn: SIGTERM — vòng đang chạy bị hủy, lease còn lại hết hạn tự nhiên.
- Rollback: dừng worker là đủ (API/CMS không phụ thuộc worker); tắt cờ `aggregation.radar` cũng dừng thu thập ở mọi bản.

## Giám sát

Nền tảng: **Grafana Cloud** (chủ dự án chốt 10/11/2026, BA-01 T-05). Cảnh báo qua **email** (contact point email của Grafana) gửi **2 thành viên quản lý trực**; notification policy định tuyến theo nhãn `notify="email"` của luật. Địa chỉ email đặt trên Grafana Cloud, không ghi trong repo.

1. Tạo access policy token (scope `metrics:write`) cho stack; đặt `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS` cho worker (secret).
2. Kiểm số đo về tới Grafana: metric `aggregation_worker_last_cycle_time_seconds` có dữ liệu (tên sau chuyển đổi OTLP → Prometheus; đối chiếu lại tên thật, sửa luật nếu khác).
3. Nạp luật `infra/monitoring/aggregation-worker.rules.yml` (`mimirtool rules load …` hoặc tạo alert rule trên Grafana từ cùng biểu thức).
4. Tạo contact point email (2 địa chỉ) + notification policy `notify = email`; bấm *Test* để gửi thử — **gửi được email là điều kiện (3) để bắt đầu thí điểm**.

| Tín hiệu | Ở đâu | Gợi ý cảnh báo |
|---|---|---|
| Nhịp tim | `tt.agg_worker_heartbeats`; CMS → Radar tin (`stats.workers`, `healthy` = có nhịp tim trong 5 phút) | Không có worker `healthy` nào trong 10 phút khi cờ ON |
| Liveness container | `HEALTHCHECK` (tệp được chạm trong 3 phút, chỉ khi vòng quét không lỗi hạ tầng) | Container unhealthy |
| Sức khỏe nguồn | CMS → Nguồn thu thập; `stats.fetchSuccessRate24h`, `stats.sources[].worstFeedHealth` | Tỉ lệ thành công 24 giờ < 80% |
| Cảnh báo nguồn | `tt.agg_alerts` (tự DEGRADED / tự PAUSED theo BR-06); CMS hiện banner; webhook nếu cấu hình | Cảnh báo chưa gửi được sau 10 lần thử (`attempts = 10`, `delivered_at IS NULL`) |
| Số đo | Meter `Tinhte.Aggregation`: `aggregation.feed.fetches` (outcome, failure), `aggregation.items.new/updated`, `aggregation.feed.fetch.duration` (ms), `aggregation.alerts`, `aggregation.fetch_log.purged`, `aggregation.items.purged`, gauge `aggregation.worker.last_cycle.time`, `aggregation.worker.radar_enabled`, `aggregation.worker.leased`; cộng số đo runtime và HttpClient | Xuất qua **OTLP** khi đặt `OTEL_EXPORTER_OTLP_ENDPOINT` (vd `http://otel-collector:4317`) hoặc `Telemetry__Otlp__Endpoint`; không đặt thì không gửi gì ra ngoài (vẫn đọc được bằng `dotnet-counters monitor -n Tinhte.Worker --counters Tinhte.Aggregation`). Luật cảnh báo mẫu: `infra/monitoring/aggregation-worker.rules.yml` |
| Log | stdout (JSON tùy nền tảng); mức `Warning`: đích không an toàn, nguồn tự PAUSED, gửi cảnh báo lỗi | — |

Truy vấn nhanh khi trực:

```sql
SELECT worker_id, last_seen_at, radar_enabled, last_cycle_feeds, last_error FROM tt.agg_worker_heartbeats ORDER BY last_seen_at DESC LIMIT 10;
SELECT kind, content_source_id, message, created_at, attempts, last_error FROM tt.agg_alerts WHERE delivered_at IS NULL ORDER BY created_at;
SELECT outcome, failure, count(*) FROM tt.agg_fetch_log WHERE started_at > now() - interval '1 hour' GROUP BY 1, 2;
```

## Hiệu chỉnh ngưỡng gom cụm

```bash
dotnet Tinhte.Worker.dll calibrate --days 90      # trong container: docker run --rm -e ConnectionStrings__Tinhte=... tinhte-worker calibrate
```

Chỉ đọc. Lấy cặp có nhãn từ các lần BTV tách/gộp cụm (migration 0345), chấm lưới ngưỡng `Aggregation:Clustering:Similarity` / `SimilarityWithEntity` và in đề xuất khi đủ ≥ 30 cặp mỗi loại. Đổi ngưỡng qua pull request (BA/TL duyệt), không sửa tay trên môi trường.

## Xử lý sự cố

- **Nguồn tự PAUSED**: xem lý do trên CMS (Nguồn thu thập → nguồn), sửa feed (URL/định dạng) hoặc liên hệ nguồn, rồi bấm *Tiếp tục*. Nguồn bị chặn bởi robots.txt (`BLOCKED_BY_ROBOTS`): **không** né chặn — liên hệ nguồn hoặc ngừng nguồn.
- **Không worker nào healthy**: kiểm container/log; `last_error` trong nhịp tim; kết nối DB.
- **Bảng `agg_fetch_log` phình**: kiểm worker còn chạy bảo trì (log "Purged …"); giảm `FetchLogRetentionDays` nếu Legal/DBA đồng ý.

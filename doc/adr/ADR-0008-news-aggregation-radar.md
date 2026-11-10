---
id: ADR-0008
title: Module Aggregation — thu thập RSS và Radar tin (giai đoạn 1, chưa có AI viết)
status: PROPOSED — ĐỀ XUẤT, CHƯA HIỆN THỰC. Chờ TL (kiến trúc, hợp đồng), PO (phạm vi, ưu tiên — BA-01 Q-AGG-06), BA/PO (quyền mới), Security (bộ thu thập), Legal (chế độ khai thác nguồn)
date: 2026-11-09
related: BA-01, ADR-0001, ADR-0002, ADR-0003, ADR-0004, ADR-0007, WBS-019, WBS-020, WBS-023, WBS-031, FR-AGG-001…005
---

# ADR-0008 — Module Aggregation: thu thập RSS và Radar tin (GĐ1)

## Bối cảnh

- BA-01 (`doc/ba/BA-01_TONG_HOP_TIN_RSS_AI_v0.1_DRAFT.md`) phân tích nghiệp vụ: đọc RSS của nhiều nguồn, tìm tin mới hôm nay theo chuyên mục, gom tin cùng sự kiện, rồi (GĐ2) để AI viết lại có kiểm duyệt.
- Chủ dự án đã chốt (D2, 09/11/2026): AI viết lại, bắt buộc kiểm duyệt, không tự đăng.
- **ADR này chỉ quyết định GĐ1 "Radar tin":**
  - thu thập feed, chuẩn hóa, khử trùng, phân loại bằng luật, gom cụm;
  - màn Radar và màn Quản lý nguồn;
  - **không gọi AI**, không đưa nội dung nguồn ra ngoài, không xuất bản gì.
- Lý do tách GĐ1: GĐ1 có giá trị ngay cho tòa soạn và gần như không phụ thuộc các câu hỏi pháp lý còn mở (BA-01 Q-AGG-01/02/05). GĐ2 sẽ có ADR riêng.
- Hiện trạng repo:
  - chưa có worker hay hàng đợi job (WBS-020 chưa làm, `ContentPublished` còn TODO);
  - ADR-0007 (danh mục nguồn/giấy phép) mới là đề xuất;
  - backend có 5 module, mỗi module chỉ tham chiếu `Tinhte.Platform` (test `Modules_reference_only_the_platform`).

## Quyết định

### 1. Module mới `Tinhte.Modules.Aggregation`

- Một `IModule` mới, controller `sealed` kế thừa `TinhteControllerBase`, route `ApiRoutes.Prefix + "/admin/aggregation/..."` (ADR-0002).
- Chỉ phục vụ trên site có `apiSurfaces: ["admin"]` (ADR-0001). Site public-web gọi các route này ⇒ 404.
- DB qua `ITinhteDatabase`, ghi bằng `InTransactionAsync(actorId, …)`, danh sách `PageRequest → CursorPage<T>`, sửa có `If-Match` (ADR-0003).
- Chỉ tham chiếu `Tinhte.Platform`. Đọc bảng của module khác (`tt.categories`, `tt.content_sources`, `tt.articles`) bằng SQL chỉ đọc; **không** gọi service của module khác.
- Khi hiện thực, test `Modules_reference_only_the_platform` đổi số module từ 5 lên 6.

### 2. Nguồn: hồ sơ thu thập gắn vào danh mục nguồn của ADR-0007

- `tt.content_sources` (ADR-0007 §1) vẫn là danh mục nguồn duy nhất, thuộc News/taxonomy.
- Module Aggregation **không thêm cột** vào bảng đó. Thông tin thu thập nằm ở bảng riêng `tt.agg_source_profiles`, khóa chính = `content_source_id` (quan hệ 1–1):
  - `status`: `PROPOSED | ENABLED | PAUSED | DEGRADED | RETIRED`;
  - `usage_mode`: `M0 | M1 | M2 | M3` (BA-01 mục 5), mặc định `M0`;
  - `license_id` (bắt buộc khi M2/M3), `terms_evidence_ref`, `trust_weight` 1–5, `language`, `site_keys`;
  - `approved_by/at`, `row_version`.
- Đường dẫn API dùng chính `sourceId` = id nguồn trong `tt.content_sources`. Tạo hồ sơ = `POST /admin/aggregation/sources` với `contentSourceId` có sẵn.
- **Phụ thuộc:** migration của ADR-0007 §1 (bảng nguồn/giấy phép) phải chạy trước. Migration của module này gọi `tt_migration.require_table('tt', 'content_sources')` và dừng nếu thiếu.
- **Đổi chế độ khai thác hoặc giấy phép chỉ qua thao tác duyệt** `POST …/sources/{id}/approval`:
  - quyền riêng có MFA;
  - ghi lịch sử chỉ thêm `tt.agg_source_approvals`;
  - PATCH thường không đổi được hai trường này (cùng lý do ADR-0007 loại `PATCH rightsStatus`).
- Không xóa hồ sơ; chỉ chuyển `RETIRED` (BR-07).

### 3. Lịch thu thập: thuê feed qua `next_fetch_at`, không chờ WBS-020

- Mỗi feed (`tt.agg_feeds`) có `next_fetch_at`, `lease_until`, `consecutive_failures`.
- Worker lặp: `SELECT … WHERE next_fetch_at <= now() AND (lease_until IS NULL OR lease_until < now()) ORDER BY next_fetch_at LIMIT n FOR UPDATE SKIP LOCKED`, đặt `lease_until`, commit, rồi mới gọi HTTP.
- Xử lý xong một feed trong **một transaction**: ghi mục tin, cập nhật `etag/last_modified`, tính `next_fetch_at` (chu kỳ + jitter, hoặc lùi theo BR-04), xóa lease.
- Như vậy:
  - nhiều worker chạy song song không xử lý trùng một feed;
  - worker chết giữa chừng thì lease hết hạn, feed được lấy lại;
  - không cần hàng đợi chung.
- Khi WBS-020 có hàng đợi/outbox chung: các bước sau thu thập (gom cụm lại, thông báo) có thể chuyển sang đó. Lịch theo `next_fetch_at` vẫn giữ.

### 4. Tiến trình worker riêng

- Project mới `Tinhte.Worker` (generic host, `BackgroundService`), dùng chung `Tinhte.Platform` và phần Application của module Aggregation. Không chạy trong tiến trình API.
- Cấu hình chạy: số feed xử lý song song (mặc định 8), chu kỳ quét (mặc định 30 giây).
- Một feed treo không chặn feed khác (timeout từng request, BR-05). Worker sập không ảnh hưởng API/CMS.
- Triển khai như bước deploy riêng, giống `Tinhte.DbMigrator`.

### 5. Bộ thu thập an toàn (Security duyệt)

- **SSRF:**
  - `SocketsHttpHandler.ConnectCallback` tự phân giải DNS rồi **kiểm IP trước khi kết nối**.
  - Chặn loopback, private (RFC 1918), link-local, CGNAT, multicast, ULA IPv6, địa chỉ metadata đám mây.
  - Tắt auto-redirect; tự theo tối đa 3 lần, mỗi lần kiểm lại.
  - Vi phạm khi khai báo/thử feed ⇒ 422 `UNSAFE_URL`.
- **Giới hạn:** timeout 20 giây; đọc tối đa 5 MB sau giải nén (đếm byte khi đọc stream); content-type XML/JSON.
- **XML:** `DtdProcessing.Prohibit`, `XmlResolver = null` (chống XXE và bom entity). Mục hỏng thì bỏ mục, không bỏ cả feed.
- **robots.txt:** đọc và cache 24 giờ theo host; bị chặn ⇒ không thu thập, báo sức khỏe nguồn.
- **User-Agent:** `TinhteNewsBot/<phiên bản> (+<URL trang giới thiệu bot>)`. URL suy ra từ Site Registry (site public-web, đường dẫn cấu hình `Aggregation:BotInfoPath`), không viết cứng host.
- **HTTP lịch sự:** gửi `If-None-Match`/`If-Modified-Since`; tôn trọng `Retry-After`, `ttl`, `sy:updatePeriod`.
- Định dạng hỗ trợ: RSS 2.0, RSS 1.0 (RDF), Atom 1.0, JSON Feed 1.1 (BA-01 Phụ lục A).

### 6. Chuẩn hóa, "hôm nay", khử trùng

- Khóa mục tin theo BR-10:
  1. `(source_id, guid)`;
  2. nếu không có: URL chuẩn hóa;
  3. nếu không có: hash `(tiêu đề, ngày)`.
- Hai index UNIQUE: `(source_id, guid)` và `(source_id, url_hash)`.
- **"Hôm nay"** theo múi giờ tòa soạn: cấu hình `Aggregation:NewsroomTimeZone`, mặc định `Asia/Ho_Chi_Minh`.
  - Lưu `published_at` (UTC) và `first_seen_at`.
  - Thời điểm không tin được ⇒ `time_reliable = false` (BR-11/12).
- Mục cũ đổi nội dung đáng kể ⇒ tăng `version` của mục, gắn nhãn "Nguồn đã cập nhật" (BR-13).
- Bật feed lần đầu: chỉ nhận mục trong 24 giờ gần nhất (BR-14).
- Làm sạch HTML ở phía server; CMS chỉ nhận văn bản thuần (BR-16).
- GĐ1 **không lưu toàn văn** bài nguồn: chỉ tiêu đề, link, tóm tắt feed, tác giả, thời điểm, category của feed. Toàn văn chỉ cần cho GĐ2 và phụ thuộc chế độ M2/M3.

### 7. Phân loại và gom cụm không dùng AI

- **Phân loại** (BR-30 tầng 1–2):
  - ánh xạ category của feed → chuyên mục (cấu hình theo feed);
  - từ khóa của chuyên mục (`tt.agg_category_rules`: từ khóa bao gồm, từ khóa loại trừ, trọng số; BTV cấu hình).
  - Ra `category_id` + `confidence` theo ngưỡng BR-31.
- **Từ khóa nhạy cảm** (BR-34):
  - GĐ1 lưu ở `config/aggregation/sensitive-topics.json`, theo site;
  - đổi qua pull request có review của Legal/Ban biên tập; lịch sử nằm trong git;
  - không có API sửa ở GĐ1.
- **Gom cụm:**
  - so độ giống tiêu đề + tóm tắt đã chuẩn hóa (bỏ dấu, bỏ từ dừng; MinHash trên shingle từ);
  - cộng thực thể chung (từ khóa sản phẩm/hãng trong luật);
  - cửa sổ 48 giờ, chỉ trong cùng site.
- **Giới hạn đã biết:** chưa gom được tin tiếng Anh và tiếng Việt về cùng sự kiện. Cần embedding đa ngôn ngữ ở GĐ2 sau khi chọn nhà cung cấp (BA-01 Q-AGG-15). BTV gộp tay được (BR-23).
- **Điểm ưu tiên** theo BR-32. Trọng số cấu hình `Aggregation:Scoring`, đổi có ghi lịch sử.

### 8. Cờ và quyền

- **Cờ mới `aggregation.radar`:**
  - production mặc định **OFF**, người duyệt bật: PO;
  - phạm vi tạm ghi **P2** (thấp nhất) cho tới khi PO xếp hạng (BA-01 Q-AGG-06). Không tự nâng.
  - Mọi thao tác `/admin/aggregation/*` kiểm cờ phía server; thao tác ghi kiểm trong transaction.
  - **Worker cũng kiểm cờ** mỗi vòng quét. Cờ OFF ⇒ không thu thập.
- **Quyền mới** (thêm vào `config/rbac/rbac.json` + `Permissions` khi hiện thực; BA/PO duyệt, BA-01 Q-AGG-07):

| Quyền | MFA | Dùng cho | Đề xuất cấp cho |
|---|---|---|---|
| `aggregation.read` | không | Radar, chi tiết cụm, thao tác trên cụm (bỏ qua, theo dõi, nhận việc, gộp/tách), danh sách mục tin, thống kê | EDITOR |
| `aggregation.sources_manage` | không | Tạo/sửa hồ sơ nguồn, feed, luật chuyên mục, thử feed, tạm dừng/tiếp tục/ngừng | EDITOR |
| `aggregation.sources_approve` | **có** | Duyệt chế độ khai thác + giấy phép của nguồn | Chờ PO + Legal (có thể là vai trò mới) |

- Tách nhiệm vụ: người đề xuất nguồn không tự duyệt chế độ M2/M3 cho nguồn đó ⇒ `SOD_VIOLATION`. Duyệt M0/M1 thì không cần tách.

### 9. Dữ liệu (migration mới, dải số chưa dùng; không sửa migration đã áp)

| Bảng | Ghi chú |
|---|---|
| `agg_source_profiles` | Mục 2 |
| `agg_source_approvals` | Lịch sử duyệt chế độ/giấy phép; chỉ thêm (`tg_append_only`) |
| `agg_source_status_history` | Lịch sử tạm dừng/tiếp tục/ngừng kèm lý do; chỉ thêm |
| `agg_feeds` | URL, định dạng, chu kỳ, `etag`, `last_modified`, `next_fetch_at`, `lease_until`, sức khỏe, ánh xạ category, `row_version`. Đổi URL ⇒ hồ sơ nguồn về `PROPOSED` (BR-01) |
| `agg_fetch_log` | Nhật ký mỗi lần thu thập; giữ 30 ngày (job dọn) |
| `agg_items` | Mục tin (mục 6) |
| `agg_clusters` | Cụm, `site_key`, điểm, trạng thái, `assignee_id`, `covered_article_id`, `row_version` |
| `agg_cluster_actions` | Bỏ qua/theo dõi/mở lại/nhận việc/đánh dấu đã đưa tin/tách/gộp; chỉ thêm |
| `agg_category_rules` | Luật phân loại theo chuyên mục; `row_version` |

- Mọi bảng có `site_key` hoặc suy ra được site (ADR-0001).
- Ràng buộc ở DB:
  - M2/M3 phải có `license_id`;
  - `ENABLED` phải có `approved_by/at`;
  - lý do bắt buộc khi tạm dừng/ngừng/bỏ qua cụm (`REASON_REQUIRED`);
  - mục tin không đổi `source_id` sau khi tạo.
- Migration chạy bằng `Tinhte.DbMigrator`, mỗi file một transaction, dùng `tt_migration.*`.
- **Thêm hàng `aggregation.radar` vào `tt.feature_gates`** (registry 0300) bằng migration mới, giá trị OFF.

### 10. Hợp đồng

- Spec v0.3 thêm **24 thao tác** dưới tag `CmsAggregation`, x-scope `P2`, x-feature-gate `aggregation.radar`, x-requirements `FR-AGG-001…005`.
- FR-AGG-001…005 nằm ở file truy vết bổ sung `trace/07_FR_ADDENDUM_v0.3_DRAFT.csv`, không thêm vào bộ 84 FR gốc.
  - Test tĩnh đọc thêm file này.
  - T22 vẫn kiểm đúng 84 FR gốc.
- Không thêm mã lỗi mới ở GĐ1. Lỗi khi tải feed (timeout, 404 của nguồn, parse hỏng) là **dữ liệu** trong kết quả thử feed/sức khỏe nguồn (`FeedFailure`), không phải `Problem.code`.
- 4 mã lỗi mới của BA-01 thuộc GĐ2.

## Tiến độ hiện thực (10/11/2026) — vẫn PROPOSED, chờ duyệt

Lát 1 (FR-AGG-001, một phần FR-AGG-002), theo phương án mặc định của ADR cho các câu hỏi chưa trả lời:

- **Migration 0340** (`content_sources`, `licenses`, `tt.license_active`): phần tối thiểu của ADR-0007 §1. Chưa API nguồn/giấy phép.
- **Migration 0341**: mọi bảng ở §9 + cờ `aggregation.radar` (OFF, P2). Ràng buộc DB như §9; thêm: chế độ/giấy phép chỉ đổi kèm bản ghi duyệt khớp, RETIRED là trạng thái cuối, feed/hồ sơ không xóa.
- **Module `Tinhte.Modules.Aggregation`**: 9 thao tác (list/create/get/patch nguồn, approval, pause/resume/retire, POST feed, PATCH feed).
  - Cờ đọc thẳng `tt.feature_gates`; thao tác ghi dùng `tt.flag_enabled_for_write` trong transaction.
  - SSRF khi khai báo feed: https, cổng 443, không user:pass, chặn IP/tên nội bộ và tên phân giải ra IP nội bộ (danh sách dải chờ Security — A-4). Tên chưa phân giải được vẫn nhận; worker phải kiểm lại IP lúc kết nối.
  - Thêm feed / đổi URL: xóa người duyệt hiện hành; ENABLED/DEGRADED → PROPOSED, PAUSED giữ PAUSED (không resume được tới khi duyệt lại).
- **Quyền** (`rbac.json`, DRAFT — chờ BA/PO): EDITOR có `aggregation.read` + `aggregation.sources_manage`; APPROVER có `aggregation.read` + `aggregation.sources_approve` (**tạm** theo chỉ đạo chủ dự án 10/11/2026, chờ PO + Legal chốt A-3; APPROVER cần `read` để lấy ETag trước khi duyệt).
- **Giả định cho A-1…A-5**: A-1 worker riêng (chưa làm); A-2 bảng 1–1, 0340 chạy trước 0341; A-3 như trên, không thêm vai trò mới; A-4 danh sách dải chặn như `FeedUrlPolicy` (chờ Security); A-5 giữ P2.
- **Chưa làm (sau lát 1)**: worker thu thập, `feed-tests`, luật chuyên mục, mục tin, cụm/Radar, thống kê, màn CMS (WBS-144).

Lát 2 (FR-AGG-002, FR-AGG-003, một phần FR-AGG-004) — bộ thu thập, worker, mục tin; vẫn PROPOSED, chưa có quyết định TL/Security/PO:

- **Bộ thu thập** (`Infrastructure/Collection`, §5):
  - `SocketsHttpHandler.ConnectCallback` tự phân giải DNS và kiểm **mọi** địa chỉ bằng `FeedUrlPolicy.IsBlocked` ngay trước khi mở socket (chặn cả DNS rebinding); một địa chỉ nội bộ trong câu trả lời DNS là chặn cả tên miền. `UseProxy = false`, không cookie.
  - Không tự redirect: tự theo tối đa 3 lần, mỗi bước kiểm lại URL (`FeedUrlPolicy.Parse`: https, cổng 443, không tên/IP nội bộ) + DNS + robots.txt.
  - Timeout 20 giây cho cả lượt (gồm robots và redirect); đọc tối đa 5 MB **sau** giải nén (đếm byte khi đọc); chỉ nhận content-type `*/xml`, `*+xml`, `*/json`, `*+json`; `If-None-Match`/`If-Modified-Since`; `Retry-After` khi 429/503.
  - robots.txt theo RFC 9309, cache 24 giờ theo host trong bộ nhớ từng tiến trình. 4xx ⇒ được phép; 5xx/429 ⇒ coi như cấm (nhớ 1 giờ); lỗi mạng khi đọc robots ⇒ không nhớ, để chính request feed báo lỗi thật.
  - User-Agent `TinhteNewsBot/<major.minor> (+<origin site public-web><Aggregation:BotInfoPath, mặc định /bot>)`, site lấy từ `Aggregation:BotSiteKey` hoặc site public-web đầu tiên của Site Registry. **Trang giới thiệu bot chưa có** trên web (việc riêng).
  - Parser RSS 2.0/RSS 1.0/Atom/JSON Feed: `DtdProcessing.Prohibit`, `XmlResolver = null`; chịu BOM, khai báo UTF-8 nhưng thực tế windows-1258, entity HTML chưa khai báo, `&` trần, ký tự điều khiển, link tương đối, `xml:base`. Mục hỏng bỏ mục; tối đa 500 mục/feed.
- **Chuẩn hóa + khử trùng** (§6): văn bản thuần (bỏ script/style/iframe/form…, thẻ, entity); tiêu đề ≤ 1000, tóm tắt ≤ 4000 ký tự. Khóa: `guid` HOẶC `url_hash` (SHA-256 URL chuẩn hóa: host thường, bỏ fragment/`utm_*`/fbclid/gclid…, sắp query, bỏ "/" cuối) — chịu được guid đổi mỗi lần tải; cùng bài khác category ⇒ gộp category (BR-20). Tiêu đề/tóm tắt đổi ⇒ `version + 1`, `source_updated_at` (BR-13). Lần đầu bật feed chỉ nhận mục 24 giờ (BR-14).
  - "Hôm nay" theo `Aggregation:NewsroomTimeZone` (mặc định `Asia/Ho_Chi_Minh`; Windows + InvariantGlobalization dùng tên Windows dự phòng).
  - Thời điểm hiệu lực = `published_at` nếu có và không ở tương lai quá 10 phút so với lúc thấy, ngược lại `first_seen_at`; `time_reliable = false` khi thiếu, ở tương lai > 10 phút hoặc cũ hơn 30 ngày (TC-AGG-06/07).
- **Worker** `backend/src/Tinhte.Worker` (generic host, §3–§4): mỗi vòng kiểm cờ `aggregation.radar` (OFF ⇒ không thuê gì), thuê tối đa `Aggregation:Worker:Parallelism` (8) feed bằng `FOR UPDATE SKIP LOCKED` + `lease_until` (`LeaseSeconds` 120), HTTP ngoài transaction, rồi một transaction: kiểm lại cờ + lease (lease mất/feed bị sửa ⇒ không ghi gì), mục tin, `agg_fetch_log`, ETag/sức khỏe/lịch, sức khỏe nguồn. Khóa hồ sơ → feed như CMS.
  - Lịch: chu kỳ hoặc `ttl`/`sy:updatePeriod` nếu dài hơn (≤ 24 giờ); lỗi ⇒ lùi cấp số, tối đa 6 giờ; `Retry-After` tôn trọng tới 24 giờ; jitter ≤ 10% (≤ 2 phút).
  - BR-06: feed lỗi ≥ 5 lần liên tiếp ⇒ nguồn `DEGRADED`; mọi feed đang bật của nguồn lỗi liên tục ≥ 24 giờ ⇒ tự `PAUSED` (lý do ghi lịch sử, người thao tác = hệ thống); tải được lại ⇒ `DEGRADED` → `ENABLED`. Chưa có kênh thông báo (chỉ lịch sử trạng thái + log).
  - Mục không có link http(s) dùng được bị bỏ: hợp đồng bắt buộc `AggregationItem.url` (tầng 3 "hash tiêu đề + ngày" của BR-10 không dùng).
- **Phân loại** (§7, BR-30 tầng 1–2, BR-31): ánh xạ category của feed ⇒ độ tin 1,0; từ khóa: điểm = 2 × số từ khóa (bao gồm + thực thể) khớp tiêu đề + số từ khóa chỉ khớp tóm tắt, độ tin = min(0,95; 0,4 + 0,15 × điểm); có từ khóa loại trừ ⇒ bỏ chuyên mục. Ngưỡng `Aggregation:Classification:AutoAssign|Review` (0,80/0,50), ghi đè theo site. Khớp nguyên từ, chữ thường, **giữ dấu** (bỏ dấu làm "đảng" trùng "đang"). Chưa đủ ngưỡng ⇒ `OUT_OF_SCOPE`. Từ khóa nhạy cảm: `config/aggregation/sensitive-topics.json` (file mẫu DRAFT, chờ Legal/Ban biên tập) ⇒ `sensitive = true`. Chưa gom cụm (lát 3).
- **API** (3 controller mới, quyền sẵn có — không đổi `rbac.json`): `POST /admin/aggregation/feed-tests` (dùng chính bộ thu thập, không ghi DB; URL/redirect/DNS nội bộ ⇒ 422 `UNSAFE_URL`; lỗi nguồn là dữ liệu; tối đa `Aggregation:FeedTests:PerMinute` = 10 lần/phút/người trong bộ nhớ mỗi bản API ⇒ 429 `RATE_LIMITED` + `Retry-After`), `GET /admin/aggregation/category-rules?siteKey=`, `PATCH …/category-rules/{categoryId}` (If-Match; chưa có luật = rowVersion 1; chuyên mục tắt ⇒ 409), `GET /admin/aggregation/items` (CursorPage theo `first_seen_at` giảm dần; `date`/`window` theo giờ tòa soạn; cursor LAST_24H mang mốc đầu cửa sổ).
- **Hợp đồng**: thêm `404 NOT_FOUND` vào 4 thao tác có `siteKey` (category-rules, items, clusters, stats) cho khớp mô tả tham số `AggregationSiteKeyQuery` ("404 otherwise") — trước đó mô tả có 404 nhưng `responses`/`x-error-codes` thiếu. Không thêm mã lỗi mới.
- **Migration 0342** (`aggregation_collector`): `agg_feeds.failing_since`; `row_version` của feed chỉ tăng khi cột CMS sửa được đổi (cập nhật của worker không làm ETag của BTV hết hiệu lực); index mục tin theo site. PATCH feed đổi URL cũng xóa `failing_since`.
- **Giả định cho câu hỏi mở (lát 2)**:
  - **A-1** (TL): worker là project riêng `Tinhte.Worker`, deploy riêng như `Tinhte.DbMigrator`; chạy nhiều bản song song được. Chưa có Dockerfile/manifest triển khai, health check hay giám sát (WBS-021).
  - **A-2**: không đổi so với lát 1.
  - **A-3**: không cần quyền mới — `feed-tests` và PATCH luật dùng `aggregation.sources_manage`, danh sách luật/mục tin dùng `aggregation.read`.
  - **A-4** (Security **chưa duyệt** bộ thu thập): dùng đúng các giới hạn ADR §5 (dải chặn của `FeedUrlPolicy`, 20 giây, 5 MB, 3 redirect, robots cache 24 giờ). Các lựa chọn tự đặt, cần Security xác nhận: robots 5xx/429 ⇒ cấm 1 giờ, lỗi mạng ⇒ bỏ qua robots; kết nối bị từ chối/ngắt ⇒ `TIMEOUT`; đích nội bộ phát hiện **lúc thu thập** (redirect/DNS rebinding) ghi `HTTP_STATUS` (redirect) hoặc `DNS` (địa chỉ) vì enum `AggregationFeedFailure` chưa có giá trị riêng — đề xuất thêm `UNSAFE_TARGET` ở bản spec sau; giới hạn thử feed 10 lần/phút/người tính trong bộ nhớ từng bản API.
  - **A-5** (PO): giữ P2, cờ OFF; không bật cờ ở đâu ngoài DB test.
- **Kiểm tra**: PGlite 328/328 (AG26–AG27 mới); `dotnet test` 370/370 với `TT_TEST_PG` (PGlite, `Pooling=false`), không DB 290 pass + 80 skip; test bộ thu thập không gọi mạng (handler giả; ConnectCallback thử bằng resolver giả, không mở socket). Phần "SKIP LOCKED bỏ qua dòng đang bị khóa" chỉ chạy trên PostgreSQL thật (CI).
- **Chưa làm (sau lát 2)**: gom cụm MinHash + Radar cụm + thao tác cụm (lát 3), thống kê, màn CMS (WBS-144), trang giới thiệu bot, job dọn `agg_fetch_log` 30 ngày, thông báo khi nguồn tự `PAUSED`, triển khai/giám sát worker.

Lát 3 (FR-AGG-004 phần gom cụm, FR-AGG-005) — gom cụm, Radar cụm, thao tác cụm, thống kê; vẫn PROPOSED:

- **Gom cụm** (§7, BR-21): MinHash 64 hàm băm trên shingle âm tiết + cặp âm tiết liền nhau của tiêu đề + tóm tắt (dạng so khớp, giữ dấu, bỏ từ dừng Việt/Anh); LSH 32 dải × 2 hàng lưu ở `agg_items.minhash_bands` + index GIN để tìm ứng viên. Cùng cụm khi độ giống ≥ `Aggregation:Clustering:Similarity` (0,5), hoặc ≥ `SimilarityWithEntity` (0,25) **và** chung một từ khóa thực thể (`entity_keywords` của luật chuyên mục cùng site); cùng site; trong `WindowHours` (48 giờ) theo thời điểm hiệu lực. Chỉ mục `CLASSIFIED` mới vào cụm (`OUT_OF_SCOPE` không lên Radar, vẫn tìm được ở danh sách mục tin); vào cụm ⇒ `CLUSTERED`. Cụm `CLOSED` không nhận mục mới; cụm `DISMISSED` vẫn nhận (không làm tin đã bỏ qua nổi lại).
  - Chạy trong chính transaction ghi mục của worker, dưới khóa tư vấn theo site (`pg_advisory_xact_lock`) để hai worker không mở hai cụm cho một sự kiện; thao tác tách/gộp của BTV cũng lấy khóa này trước khóa dòng cụm.
  - Đo trên tiêu đề mẫu: cùng sự kiện viết khác ≈ 0,33–0,39 (vào cụm nhờ thực thể chung), khác sự kiện ≤ 0,1. **Ngưỡng chưa hiệu chỉnh bằng dữ liệu thật**; tách/gộp tay được ghi lịch sử để hiệu chỉnh (BR-23). Vẫn chưa gom được tin Anh–Việt (giới hạn đã biết).
- **Tóm tắt cụm**: mục đại diện = nguồn uy tín nhất rồi sớm nhất; chuyên mục chính = chuyên mục nhiều mục nhất ("cần xem" khi mọi mục của chuyên mục đó cần xem); `sourceCount` = số feed, `independentSourceCount` = số nguồn; nhãn `SINGLE_SOURCE`, `SENSITIVE`, `SOURCE_UPDATED`, `NEW_SINCE_COVERED` (mục thấy sau `covered_at`); `maxUsageMode` tính lúc đọc.
- **Điểm** (BR-32, 0–100): trọng số `Aggregation:Scoring:Sources|Trust|Relevance|Velocity|Freshness` = 35/15/15/15/20 (đổi qua pull request): nguồn độc lập (5 = tối đa), uy tín trung bình, độ tin chuyên mục trung bình, số nguồn mới trong 2 giờ (3 = tối đa), độ mới (về 0 sau 48 giờ không có mục mới). "Chủ đề nóng trong ngày" **chưa tính** — GĐ1 chưa có nơi BTV đặt chủ đề nóng.
- **Bảo trì** (worker, mỗi `Aggregation:Worker:MaintenanceMinutes` = 5 phút, từng site một transaction): cụm `OPEN` chưa ai nhận và không có mục mới quá 48 giờ ⇒ `CLOSED`; gom bù mục `CLASSIFIED` chưa vào cụm (mục của lát 2); tính lại điểm cụm `OPEN`/`WATCHING` (độ mới giảm theo thời gian).
- **API** (9 thao tác, đều `aggregation.read` như hợp đồng): danh sách (điểm giảm dần, rồi lúc thấy đầu, rồi id; mặc định `OPEN`+`WATCHING`; cụm có hoạt động trong cửa sổ `date`/`window`), chi tiết (mục theo thời gian + lịch sử, ETag), `dismiss` (lý do), `watch`, `reopen` (từ `DISMISSED`/`WATCHING`), `claim` (người khác đã nhận ⇒ 409; chính mình ⇒ giữ nguyên), `coverage` (bài phải tồn tại và thuộc site của cụm ⇒ 422; đã `COVERED` ⇒ 409), `split` (`[Idempotent]`, 201, mục phải thuộc cụm và phải còn ≥ 1 mục ở lại ⇒ 422, cụm `CLOSED` ⇒ 409), `merge` (cùng site, khác chính nó, đích tồn tại ⇒ 422; nguồn `CLOSED`/`COVERED` hoặc đích `CLOSED` ⇒ 409). Mọi thao tác ghi `agg_cluster_actions`; split/merge ghi ở cả hai cụm. `stats`: số mục/cụm/cụm mở của ngày, tỉ lệ tải thành công 24 giờ, nguồn theo trạng thái, sức khỏe từng nguồn, `timeZone`.
- **Migration 0343** (`aggregation_clustering`): chữ ký MinHash + dải LSH + index GIN; bộ đếm và `covered_at` của cụm; `row_version` cụm chỉ tăng theo thao tác CMS (`status`, `assignee_id`, `covered_article_id`) — worker thêm mục/tính điểm không làm ETag của BTV hết hiệu lực.
- **Giả định (lát 3)**, ngoài các giả định A-1…A-5 ở trên (không đổi):
  - Cụm nguồn sau khi gộp giữ ảnh chụp số liệu lúc gộp (`itemCount`… như trước), danh sách mục rỗng — vì hợp đồng đòi `itemCount ≥ 1`. Cụm `COVERED` không gộp vào cụm khác được (gộp chiều ngược lại).
  - Worker đổi trạng thái sang `CLOSED` không ghi `agg_cluster_actions` (bảng đòi `actor_id`); lịch sử chỉ có thao tác của người.
  - Trạng thái `DRAFTING/DRAFTED/CONVERTED` của BA-01 thuộc GĐ2, chưa có.
- **Hợp đồng**: không đổi ở lát 3.
- **Kiểm tra**: PGlite 336/336 (AG28–AG29 mới); test đơn vị MinHash/tính cụm; `[DbFact]` cho gom cụm liên nguồn (TC-AGG-10), bảo trì, Radar + vòng đời thao tác + lịch sử, thống kê.
- **Chưa làm (sau lát 3)**: màn CMS Radar + chi tiết cụm + Quản lý nguồn (WBS-144), trang giới thiệu bot, job dọn `agg_fetch_log` 30 ngày, thông báo khi nguồn tự `PAUSED`, triển khai/giám sát worker, hiệu chỉnh ngưỡng gom cụm bằng dữ liệu thật, "chủ đề nóng" cho điểm.

## Hệ quả

- **Được:**
  - tòa soạn có Radar tin mà không có rủi ro dữ liệu ra nước ngoài hay viết lại nội dung;
  - không phải chờ WBS-020;
  - lịch thu thập an toàn khi chạy nhiều worker;
  - ranh giới module giữ nguyên.
- **Mất:**
  - thêm một tiến trình để triển khai và giám sát (WBS-021);
  - gom cụm GĐ1 yếu hơn với tin đa ngôn ngữ;
  - phụ thuộc migration ADR-0007 §1 có trước.
- **Việc khi được duyệt:**
  - migration mới + test PGlite;
  - module + worker;
  - `rbac.json`/`Permissions`/test ma trận;
  - màn CMS Radar và Quản lý nguồn (WBS-144);
  - cập nhật `FeatureFlagKeys` (đã làm cùng spec).
- WBS đề xuất: WBS-140…145 (`PROPOSED_PENDING_PM_SIGNOFF`, chưa có effort).

## Phương án đã loại

| Phương án | Lý do loại |
|---|---|
| Chạy thu thập trong tiến trình API (`IHostedService`) | Mỗi bản API đều quét. Một feed treo ảnh hưởng thời gian phản hồi API. Scale API kéo theo scale bot. |
| Chờ WBS-020 rồi mới làm | Lịch theo `next_fetch_at` + `SKIP LOCKED` đủ cho bài toán thu thập và đơn giản hơn hàng đợi chung |
| Thêm cột thu thập vào `tt.content_sources` | Trộn dữ liệu của hai module vào bảng của News; chế độ khai thác thuộc nghiệp vụ thu thập |
| Gom cụm bằng embedding ngay GĐ1 | Phải chọn nhà cung cấp và gửi dữ liệu ra ngoài: đúng những câu hỏi Legal còn mở |
| Thư viện `SyndicationFeed` có sẵn của .NET làm parser duy nhất | Không đọc JSON Feed, chịu lỗi kém với feed thực tế (ngày sai định dạng, encoding lệch). Có thể dùng bên trong nếu bọc an toàn XML như mục 5; TL quyết khi code |

## Câu hỏi cần quyết

| # | Ai | Câu hỏi |
|---|---|---|
| A-1 | TL | Đồng ý worker là project riêng `Tinhte.Worker`, deploy riêng? |
| A-2 | TL + DBA | Đồng ý bảng `agg_source_profiles` 1–1 thay vì thêm cột vào `content_sources`? Thứ tự migration với ADR-0007 §1? |
| A-3 | PO + BA | Cấp `aggregation.sources_approve` cho ai? Có cần vai trò mới (sửa CHECK `ck_user_roles_role`)? |
| A-4 | Security | Duyệt danh sách dải IP bị chặn, giới hạn kích thước/timeout, cách xử lý `robots.txt` |
| A-5 | PO | Xếp `aggregation.radar` / FR-AGG-001…005 vào P1 hay giữ P2? |

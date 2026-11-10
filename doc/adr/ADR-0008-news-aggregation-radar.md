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
- **Chưa làm**: worker thu thập, `feed-tests`, luật chuyên mục, mục tin, cụm/Radar, thống kê, màn CMS (WBS-144).

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

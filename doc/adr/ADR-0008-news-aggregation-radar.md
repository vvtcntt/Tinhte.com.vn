---
id: ADR-0008
title: Module Aggregation — thu thập RSS và Radar tin (giai đoạn 1, chưa có AI viết)
status: PROPOSED — đã hiện thực GĐ1 trên nhánh; chủ dự án chốt A-1…A-5 và quy tắc quét ngày 10/11/2026 (mục "Kết quả chốt"); vẫn chờ TL/DBA/Security/Legal/Ban biên tập ký phiếu BA-01
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
  - *Sửa đổi 10/11/2026 (chủ dự án, BA-01 P-02):* GĐ1 được dùng AI cho **một việc duy nhất** — xác định hai tin có trùng chủ đề không — sau cờ riêng tắt mặc định; chỉ gửi tiêu đề + tóm tắt. Xem §7.
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
- ~~Bật feed lần đầu: chỉ nhận mục trong 24 giờ gần nhất (BR-14).~~ **Thay bằng (chủ dự án, 10/11/2026, BA-01 P-02):** worker **chỉ nhận tin trong ngày** — bỏ qua mục có thời điểm hiệu lực trước 00:00 hôm nay theo `Aggregation:NewsroomTimeZone`, ở **mọi** lần đọc (không chỉ lần đầu). Cấu hình `Aggregation:Intake:TodayOnly` (mặc định `true`; `false` = quy tắc BR-14 cũ).
- Chu kỳ đọc feed mặc định **30 phút** (BA-01 P-02); hợp đồng vẫn cho 5–1440 phút.
- Làm sạch HTML ở phía server; CMS chỉ nhận văn bản thuần (BR-16).
- GĐ1 **không lưu toàn văn** bài nguồn: chỉ tiêu đề, link, tóm tắt feed, tác giả, thời điểm, category của feed. Toàn văn chỉ cần cho GĐ2 và phụ thuộc chế độ M2/M3.

### 7. Phân loại và gom cụm (AI chỉ để xác định tin trùng chủ đề, cờ riêng, tắt mặc định)

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
  - cửa sổ 48 giờ, chỉ trong cùng site;
  - **chỉ so mục cùng chuyên mục** (chủ dự án xác nhận 10/11/2026, BA-01 P-02): mục có `category_ids` = chuyên mục chính + các chuyên mục khác cùng site đủ ngưỡng "cần xem" (ánh xạ category của feed hoặc từ khóa). Mục nhiều chuyên mục được so trong từng chuyên mục của nó; một mục chỉ thuộc một cụm (cụm giống nhất). Mục `OUT_OF_SCOPE` không vào cụm.
- **Giới hạn đã biết:** MinHash chưa gom được tin tiếng Anh và tiếng Việt về cùng sự kiện. BTV gộp tay được (BR-23). Bước AI dưới đây xử lý được khi bật.
- **AI xác định tin trùng chủ đề** — *sửa quyết định "GĐ1 không dùng AI"* theo chủ dự án 10/11/2026 (BA-01 P-02; thiết kế ở `doc/ba/BA-01_HUONG_DAN_CAI_DAT_NGUON_v0.1_DRAFT.md` mục 5):
  1. Máy lọc trước bằng cách hiện có (MinHash, cùng site, cùng chuyên mục, cùng ngày) để ra **cặp ứng viên** "có thể trùng" — không hỏi AI cho mọi cặp.
  2. Gửi cho AI **chỉ tiêu đề + tóm tắt** (trường công khai của RSS, đã làm sạch) của hai mục, qua module kết nối AI (YesScale — đang làm ở phiên/nhánh riêng `feat/ai-module`, **chưa phải quyết định nhà cung cấp**). AI trả "cùng sự kiện" / "khác sự kiện" + độ tin cậy.
  3. Cùng sự kiện ⇒ gộp cụm; khác ⇒ giữ riêng. Mỗi kết luận được ghi lại (nhà cung cấp, model, độ tin cậy) để BTV kiểm khi tách/gộp tay.
  4. AI lỗi, hết giờ, hết ngân sách hoặc cờ tắt ⇒ quay về MinHash; Radar vẫn chạy.
  5. Không gọi AI trong transaction đang giữ khóa tư vấn của site: bước AI chạy sau khi ghi mục (vd. trong chu kỳ bảo trì), rồi mới gộp/tách dưới khóa.
  - **Điểm nối đã có** (lát 6): interface `Tinhte.Platform.Ai.ISameEventJudge` + bản mặc định `NoAiSameEventJudge` (không gọi AI, luôn "không kết luận"), đăng ký `TryAddSingleton` trong module Aggregation để module AI thay. Cờ `aggregation.ai_dedup` (OFF, P2, không có endpoint). **Chưa nối vào gom cụm**, chưa có bảng ghi kết luận AI — làm khi module AI vào nhánh tích hợp.
  - **Chỉ bật sau:** Legal L-07 + Security S-03 (chấp nhận gửi tiêu đề/tóm tắt qua nhà cung cấp), Legal xác nhận so sánh nội bộ vẫn trong phạm vi M0, PM/Finance F-02 (ngân sách). Không merge nhánh module AI vào nhánh này.
- **Điểm ưu tiên** theo BR-32. Trọng số cấu hình `Aggregation:Scoring`, đổi có ghi lịch sử.

### 8. Cờ và quyền

- **Cờ mới `aggregation.radar`:**
  - production mặc định **OFF**, người duyệt bật: PO;
  - phạm vi ~~tạm ghi P2~~ **P1** — chủ dự án chốt 10/11/2026 (BA-01 P-01); migration 0346 đổi dòng `tt.feature_gates`. GĐ2 (AI viết lại, ADR-0009) giữ P2.
- **Cờ `aggregation.ai_dedup`** (10/11/2026): bước AI xác định tin trùng chủ đề (§7). OFF, **P2**, người duyệt PO + Legal + Security, không có endpoint; tắt độc lập với Radar.
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
  - **A-1** (TL): worker là project riêng `Tinhte.Worker`, deploy riêng như `Tinhte.DbMigrator`; chạy nhiều bản song song được. (Lát 4 thêm Dockerfile, nhịp tim, liveness, số đo — xem bên dưới; manifest nền tảng và cảnh báo vận hành vẫn chờ WBS-021.)
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

Lát 4 (WBS-144 + vận hành) — màn CMS, trang bot, dọn nhật ký, cảnh báo, triển khai/giám sát worker; vẫn PROPOSED:

- **CMS** (`frontend/apps/cms`, menu "Radar tin" và "Nguồn thu thập", quyền `aggregation.read`; nút ghi ẩn/hiện theo `aggregation.sources_manage` / `aggregation.sources_approve` nhưng API vẫn kiểm):
  - *Radar tin* (`/radar`): lọc site/hôm nay/24 giờ/chọn ngày/chuyên mục/trạng thái/điểm; thẻ cụm (điểm, nhãn BA-01 mục 9 gồm "Chỉ M0/M1", số nguồn, chuyên mục + "cần xem", người xử lý); thao tác nhanh theo dõi/nhận việc; dải số liệu (mục, cụm, tỉ lệ tải 24 giờ, worker hoạt động) + banner cảnh báo nguồn và "không có worker".
  - *Chi tiết cụm* (`/radar/{id}`): dòng thời gian mục tin (link nguồn mở tab mới, chỉ http(s), `rel=noopener noreferrer nofollow`), theo dõi/mở lại/bỏ qua (lý do)/nhận việc/đã đưa tin (mã bài)/tách (chọn mục, Idempotency-Key)/gộp (mã cụm đích), lịch sử thao tác. Không render HTML của nguồn.
  - *Nguồn thu thập* (`/aggregation`): danh sách + banner nguồn bị hệ thống tự đổi trạng thái; tạo hồ sơ (nhập mã nguồn trong danh mục — chưa có màn danh mục nguồn của ADR-0007); chi tiết: sửa hồ sơ, tạm dừng/tiếp tục/ngừng (lý do), duyệt chế độ khai thác (MFA), feed (sức khỏe, sửa, ánh xạ category → chuyên mục, thêm feed, *Thử feed*), lịch sử duyệt. *Luật chuyên mục* (`/aggregation/rules`).
  - Đã chạy thử đầu-cuối trên máy dev (API + PGlite + IdP dev + CMS, dữ liệu demo): đăng nhập, Radar, nhận việc, tách/gộp, thử feed (lỗi nguồn là dữ liệu, URL nội bộ ⇒ UNSAFE_URL), tiếp tục nguồn tự PAUSED, lưu luật; khổ 375 px không cuộn ngang.
- **Trang `/bot`** (`frontend/apps/web`, mọi site public-web; link ở chân trang): User-Agent dựng từ Site Registry (khớp `Aggregation:BotInfoPath`), bot đọc gì, tần suất, cách chặn bằng robots.txt, liên hệ. Chỉ mô tả hành vi đã hiện thực — không cam kết pháp lý thay Legal.
- **Dọn `agg_fetch_log`**: worker xóa bản ghi cũ hơn `Aggregation:FetchLogRetentionDays` (30) theo lô 5000 dòng mỗi chu kỳ bảo trì.
- **Thông báo nguồn tự đổi trạng thái** (BR-06): migration 0344 `agg_alerts` — worker ghi cảnh báo `SOURCE_DEGRADED` / `SOURCE_AUTO_PAUSED` trong **cùng transaction** đổi trạng thái (outbox, không mất); gửi ra webhook `Aggregation:Alerts:WebhookUrl` (https, JSON có `text` hợp Slack/Teams/Google Chat; tối đa 10 lần thử) nếu có cấu hình; CMS hiện banner. Hợp đồng (bổ sung, không phá vỡ): `AggregationSourceSummary.statusChangedAt/statusChangedBySystem/statusReason`, `AggregationStats.workers/alerts` + schema `AggregationWorkerStatus`, `AggregationAlert`.
- **Triển khai/giám sát worker** (A-1 vẫn chờ TL): `backend/src/Tinhte.Worker/Dockerfile` (context gốc repo, không mở cổng, `HEALTHCHECK` theo tệp liveness), compose profile `worker`, runbook `backend/src/Tinhte.Worker/README.md`; nhịp tim `agg_worker_heartbeats` (0344) hiện trên CMS; số đo `System.Diagnostics.Metrics` meter `Tinhte.Aggregation` (fetch theo kết quả, mục mới/cập nhật, thời gian, cảnh báo, số dòng dọn). **Image chưa build thử** (máy dựng không có Docker; CI chưa build image); exporter OpenTelemetry/Prometheus và cảnh báo trên nền tảng giám sát chờ WBS-021.
- **Giả định (lát 4)**: kênh thông báo ngoài là webhook chung (chưa có hệ thống thông báo/email trong repo); CMS chưa có màn danh mục nguồn/giấy phép (ADR-0007) nên nhập mã UUID; số liệu "worker hoạt động" = nhịp tim trong 5 phút.
- **Kiểm tra**: PGlite 345/345 (AG30–AG32); `[DbFact]` cảnh báo tự PAUSED (outbox, API nguồn, thống kê, gửi webhook thành công/thất bại), dọn nhật ký, nhịp tim; test webhook (handler giả); CMS `node:test` cho quy tắc thuần (thao tác theo trạng thái, query, kiểm nhập liệu, link an toàn).
- **Còn lại (sau lát 4)**: màn danh mục nguồn + giấy phép (ADR-0007), exporter giám sát + cảnh báo vận hành (WBS-021), build/scan image trong CI, hiệu chỉnh ngưỡng gom cụm và trọng số điểm bằng dữ liệu thật, "chủ đề nóng", GĐ2 (AI viết lại — ADR riêng).

Lát 5 (phần còn lại) — vẫn PROPOSED:

- **Danh mục nguồn/giấy phép**: ADR-0007 §1 có API + màn CMS (xem ADR-0007); màn Nguồn thu thập chọn nguồn và giấy phép từ danh mục.
- **Hiệu chỉnh gom cụm**: migration 0345 ghi `agg_cluster_actions.item_ids` (mục bị chuyển) và `other_item_ids` (phía bên kia) ở mỗi lần tách/gộp ⇒ cặp có nhãn của BTV (gộp = cùng sự kiện, tách = khác sự kiện) cố định tại thời điểm quyết định. Lệnh `Tinhte.Worker calibrate [--days 90]` (chỉ đọc) chấm lưới ngưỡng (precision/recall/F1) và đề xuất cấu hình khi đủ ≥ 30 cặp mỗi loại. **Chưa hiệu chỉnh được thật**: chưa có dữ liệu BTV thật (chưa chạy production); dữ liệu nhãn lệch về ca khó nên chỉ là gợi ý, đổi ngưỡng qua pull request. Trọng số điểm (BR-32) và "chủ đề nóng" vẫn cần dữ liệu/quyết định của Ban biên tập.
- **Giám sát** (WBS-021, chủ dự án chọn OTLP 10/11/2026): worker xuất số đo qua OpenTelemetry OTLP **chỉ khi** có `OTEL_EXPORTER_OTLP_ENDPOINT` / `Telemetry:Otlp:Endpoint` (gói OpenTelemetry 1.20.1, phát hành 20–21/10/2026): meter `Tinhte.Aggregation` + runtime + HttpClient; thêm gauge `aggregation.worker.last_cycle.time`, `aggregation.worker.radar_enabled`, `aggregation.worker.leased`. Luật cảnh báo mẫu (Prometheus) `infra/monitoring/aggregation-worker.rules.yml`: worker không chạy, mất số đo, tỉ lệ lỗi đọc feed, nguồn tự tạm dừng, 6 giờ không có tin mới. Nền tảng nhận OTLP chưa chọn; tên metric phía Prometheus là giả định theo quy tắc chuyển đổi chuẩn.
- **CI**: job `worker-image` — build image worker, chạy thử (thiếu chuỗi kết nối ⇒ thoát 1 với thông báo rõ), quét Trivy (CRITICAL/HIGH có bản vá ⇒ fail), `promtool check rules`. **Chưa chạy** trên GitHub Actions từ máy dựng (không có Docker).
- **GĐ2**: ADR-0009 (PROPOSED) + hợp đồng nháp riêng `doc/07_AI_REVIEW/v0.3/proposals/07_OPENAPI_AGGREGATION_GD2_PROPOSAL.yaml` (hợp lệ OpenAPI 3.1). Không có code, không đổi spec chính/`ErrorCatalog`/`rbac.json` — chờ Legal (Q-AGG-01/02/04/05/13/14) và TL chọn nhà cung cấp AI.

## Kết quả chốt ngày 10/11/2026 (chủ dự án) và lát 6

Nguồn: `doc/ba/BA-01_PHIEU_XIN_QUYET_DINH_v0.1_DRAFT.md` (mục "Kết quả chốt", ô Trả lời) và `doc/ba/BA-01_HUONG_DAN_CAI_DAT_NGUON_v0.1_DRAFT.md` mục 5. Các câu thuộc chuyên môn TL/DBA/Security/Legal/Ban biên tập vẫn chờ bên đó ký ở phiếu.

| # | Kết quả |
|---|---|
| A-1 | **Đồng ý** (T-01): worker là project riêng `Tinhte.Worker`, deploy riêng, chạy nhiều bản. |
| A-2 | **Đồng ý** (T-02): bảng `agg_source_profiles` 1–1, thứ tự migration 0340 → 0346. DBA còn trả lời việc đối chiếu schema 04 (stub schema 04 chỉ dùng ở Development). |
| A-3 | **Giữ ma trận đang cấp tạm, không thêm vai trò** (P-03): EDITOR đề xuất/sửa nguồn; APPROVER duyệt (MFA). Hai thành viên thí điểm được cấp **EDITOR + APPROVER** — là gán vai trò cho người dùng, **không sửa `rbac.json`**. M2/M3 vẫn bắt buộc người duyệt khác người đề xuất. Xem lại khi có người của Legal trực tiếp duyệt trong CMS. |
| A-4 | **Duyệt nguyên trạng kèm điều kiện hạ tầng** (S-01): worker chạy trong **mạng riêng**; tường lửa chỉ cho **ra Internet cổng 443** và **kết nối tới DB**; không mở cổng vào. Ghi ở runbook `backend/src/Tinhte.Worker/README.md`. S-02 (giá trị lỗi `UNSAFE_TARGET`) chưa trả lời, không chặn thí điểm. |
| A-5 | **P1** (P-01) cho cờ `aggregation.radar` và FR-AGG-001…005, WBS-140…145. Cờ vẫn OFF ở production. GĐ2 giữ P2. |

**Thí điểm (P-02, E-03):**
- site **`www`** (site public-web duy nhất của Site Registry; `cms`/`media` không nhận tin);
- **2 thành viên** quản lý dùng và gán nhãn (mỗi người 10–15 phút/ngày tách/gộp khi gom sai), **3 tuần**, trên **staging**;
- bắt đầu (ngày bật cờ `aggregation.radar` trên staging) khi đủ cả ba: (1) PR Radar đã merge vào `sprint-0/scaffold` và deploy staging **gồm worker**; (2) có **ít nhất 1 nguồn được duyệt**; (3) **Grafana Cloud** đã nhận số đo và **gửi được email cảnh báo**;
- hết 3 tuần xem số liệu (số tin/ngày, tỉ lệ gom đúng qua lệnh `calibrate`, góp ý BTV) rồi mới quyết định lên production.

**Nguồn (L-01, E-02, hướng dẫn cài đặt nguồn mục 1):** 10–20 nguồn thí điểm, tất cả M0, tối đa M1. Điều khoản RSS "miễn phí cho cá nhân, phi lợi nhuận" của VnExpress, Dân trí, Tuổi Trẻ: **xin phép trước khi duyệt** nguồn; trong lúc chờ có thể thí điểm với Thanh Niên, GenK. Không coi điều khoản/giấy phép nào là đã có.

**Hiện thực lát 6 (theo các quyết định trên):**
- **Migration 0346** (`aggregation_owner_decisions`): `aggregation.radar` P2 → P1; thêm cờ `aggregation.ai_dedup` (OFF, P2); `agg_feeds.schedule_minutes` mặc định 30; `agg_items.category_ids` (+ trigger giữ chuyên mục chính ở đầu, dữ liệu cũ điền từ `category_id`); index cho việc xóa mục lẻ. Bản down trả P2, gỡ cờ (từ chối nếu cờ AI đang bật), trả mặc định 15.
- **Hợp đồng** (spec v0.3): `x-feature-flags` (`aggregation.radar` P1, thêm `aggregation.ai_dedup`), `x-scope` P1 cho 24 thao tác `/admin/aggregation/*`, `AggregationFeedCreate.scheduleMinutes` mặc định 30; inventory, FR addendum (FR-AGG-001…005 gói P1), WBS-140…145 P1. `FeatureFlagKeys` 19 cờ.
- **P-02 quy tắc quét**: chu kỳ mặc định 30 phút (API + CMS); chỉ nhận tin trong ngày (§6); gom cụm chỉ trong cùng chuyên mục (§7); Radar CMS mở mặc định "Hôm nay" (đã là mặc định, nay thành hằng số có test).
- **L-03 thời gian lưu**: worker xóa mục tin **không thuộc cụm nào** có `first_seen_at` cũ hơn `Aggregation:UnclusteredItemRetentionDays` (**90**, cấu hình 7–3650) theo lô 5000 trong cùng chu kỳ bảo trì với việc dọn `agg_fetch_log` (vẫn 30 ngày). Mục trong cụm — kể cả cụm `COVERED` đã có bài — được giữ cùng cụm; mục đang là đại diện của một cụm cũng giữ. Mục trong cụm chưa có bài: chưa có quyết định, đang giữ.
- **E-01**: `config/aggregation/sensitive-topics.json` thêm 3 nhóm *pháp luật/vụ án*, *y tế/dịch bệnh*, *trẻ em* (giữ dấu tiếng Việt + tiếng Anh; tránh từ thông dụng của tin công nghệ như "trial", "học sinh"); file vẫn DRAFT. Trọng số điểm giữ nguyên trong thí điểm; "chủ đề nóng" và "ít nhất 2 nguồn" để sau thí điểm.
- **T-05 giám sát**: **Grafana Cloud**, cảnh báo qua **email** (contact point email của Grafana) cho 2 thành viên; webhook của worker (`Aggregation:Alerts:WebhookUrl`) thành tùy chọn. Runbook + `infra/monitoring/aggregation-worker.rules.yml` cập nhật.
- **AI trùng chủ đề**: chỉ điểm nối + cờ + thiết kế (§7); không gọi AI thật, không merge nhánh module AI.
- **Kiểm tra**: PGlite 362/362 (AG34–AG37 mới); test đơn vị mốc nhận tin theo giờ tòa soạn, chuyên mục phụ, mặc định cấu hình, chủ đề nhạy cảm mới, bộ so sánh mặc định; `[DbFact]` chỉ nhận tin trong ngày, gom cụm theo chuyên mục, xóa mục lẻ.

## Hệ quả

- **Được:**
  - tòa soạn có Radar tin mà không có rủi ro dữ liệu ra nước ngoài hay viết lại nội dung (khi bật `aggregation.ai_dedup`: tiêu đề + tóm tắt công khai đi qua nhà cung cấp AI — chỉ sau L-07/S-03/F-02);
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
| Gom cụm bằng embedding ngay GĐ1 | Phải chọn nhà cung cấp và gửi dữ liệu ra ngoài: đúng những câu hỏi Legal còn mở. *10/11/2026:* thay bằng AI so cặp ứng viên sau cờ riêng (§7) |
| Hỏi AI cho mọi cặp tin / thay MinHash bằng AI | Chi phí theo bình phương số tin, Radar dừng khi AI lỗi; MinHash lọc trước rồi AI chỉ xét cặp ứng viên, AI lỗi thì quay về MinHash |
| Thư viện `SyndicationFeed` có sẵn của .NET làm parser duy nhất | Không đọc JSON Feed, chịu lỗi kém với feed thực tế (ngày sai định dạng, encoding lệch). Có thể dùng bên trong nếu bọc an toàn XML như mục 5; TL quyết khi code |

## Câu hỏi cần quyết

| # | Ai | Câu hỏi | Kết quả (10/11/2026) |
|---|---|---|---|
| A-1 | TL | Đồng ý worker là project riêng `Tinhte.Worker`, deploy riêng? | Đồng ý |
| A-2 | TL + DBA | Đồng ý bảng `agg_source_profiles` 1–1 thay vì thêm cột vào `content_sources`? Thứ tự migration với ADR-0007 §1? | Đồng ý; DBA còn trả lời schema 04 |
| A-3 | PO + BA | Cấp `aggregation.sources_approve` cho ai? Có cần vai trò mới (sửa CHECK `ck_user_roles_role`)? | Giữ ma trận tạm, không vai trò mới |
| A-4 | Security | Duyệt danh sách dải IP bị chặn, giới hạn kích thước/timeout, cách xử lý `robots.txt` | Duyệt kèm điều kiện mạng riêng + tường lửa |
| A-5 | PO | Xếp `aggregation.radar` / FR-AGG-001…005 vào P1 hay giữ P2? | P1 |
| A-6 | Legal + Security + PM/Finance | Bật `aggregation.ai_dedup`: gửi tiêu đề/tóm tắt qua nhà cung cấp AI (L-07, S-03), so sánh nội bộ có nằm trong M0, ngân sách (F-02)? | Chưa trả lời — cờ OFF |

---
document_id: TINHTE-DB-MIGRATION-v0.3
project: Tinhte.com.vn
version: 0.3.0-draft
status: DRAFT — CHỜ DBA + TECH LEAD DUYỆT; CHƯA CHẠY TRÊN POSTGRESQL PRODUCTION
contract: ../07_OPENAPI_TINHTE_v0.3_DRAFT.yaml
created_at: 2026-11-09
---

# Migration SQL v0.3 (PostgreSQL)

Đưa database khớp hợp đồng OpenAPI v0.3 DRAFT, đồng thời xử lý các điểm DB trong review (AIR-001/002/004/005/006/008/012/013/017/018/019/023/024). Thay cho `07_DB_ADS_LITE_REFERENCE_v0.2.sql`, nhưng vẫn chạy đúng trên DB **đã** áp file v0.2 (kịch bản B).

## 1. Trạng thái kiểm chứng

| Hạng mục | Kết quả |
|---|---|
| Chạy thật trên PostgreSQL | **Có**, PostgreSQL 18.6 qua PGlite 0.5.10 (WASM), dùng stub schema 04 |
| Test tự động | **320/320 PASS** (chạy lại 10/11/2026 sau khi thêm 0340/0341 — nguồn/giấy phép + Radar tin GĐ1, 25 test hành vi AG1–AG25, log `tests/migration_test_run_2026-11-10.txt`; 283/283 ngày 09/11/2026 sau khi gộp 0322 — trang chủ, 0324/0325 — WBS-024, 0330–0333 — WBS-046, 0334/0335 — WBS-040; 179/179 sau 0320/0321; 157/157 sau 0313; log gốc `tests/migration_test_run_2026-11-09.txt` ghi lần 151/151), gồm 0312 site registry và 0313 staff access |
| Kịch bản A: DB sạch | up → up lần 2 không đổi schema → 50+ test hành vi → down toàn bộ → schema **trùng khớp baseline** → up lại |
| Kịch bản B: DB đã áp SQL v0.2 | SQL v0.2 chạy được (lần đầu được thử) → up → 0310 **dừng đúng** vì dữ liệu cũ → backfill → 0310 → down → schema và khóa cờ **trở về đúng v0.2** |
| Kịch bản C: thiếu bảng tiên quyết | migration dừng, kèm thông báo rõ bảng còn thiếu |
| Mutation test (bản sao) | Gỡ trigger chặn publish và làm hỏng một bản down → **8 FAIL được phát hiện**, nghĩa là bộ test thật sự bắt được lỗi |
| **Chưa kiểm** | Schema 04 thật (không có trong repo); khóa đồng thời giữa nhiều kết nối (PGlite chỉ có một kết nối); thời gian khóa và hiệu năng trên bảng lớn; phiên bản PostgreSQL production |

## 2. Danh sách migration

Mỗi file chạy trong **một transaction** do công cụ migration bọc (các file không tự chứa `BEGIN`/`COMMIT`). Thứ tự chạy theo số.

| # | File | Nội dung | Đề xuất |
|---|---|---|---|
| 0299 | `migration_support` | Schema `tt_migration`: ledger, helper (`add_column`/`add_constraint`/…/`rollback`); hàm dùng chung `raise_domain_error`, `tg_touch_row`, `tg_append_only` | AIR-024 |
| 0300 | `feature_gate_registry` | 17 khóa theo `x-feature-flags`: đổi tên khóa cũ, **giữ nguyên trạng thái**; cột duyệt (`approval_reference/approved_by/approved_at`), `version`; nhật ký `feature_gate_changes` chỉ thêm; CHECK bật cờ phải có duyệt | AIR-001, AIR-019 |
| 0301 | `revision_sponsorship` | `content_kind` + nhóm `sponsorship_*` trên `article_revisions`/`video_revisions`; CHECK đủ metadata, cửa sổ hiệu lực, cặp duyệt | AIR-012 |
| 0302 | `ad_placements` | Tạo mới hoặc nâng cấp từ v0.2: `width/height/alt_text/advertiser_name/created_by/updated_by`; CHECK https, kích thước, duyệt, đủ điều kiện phát; index phục vụ slot; tự tăng `row_version` | AIR-013 |
| 0303 | `takedown` | `takedown_requests` (không xóa được, kiểm chuyển trạng thái), `takedown_evidence`/`takedown_actions` (chỉ thêm), `takedown_propagation` (7 bước, tự sinh khi UNPUBLISH/RESTRICT); ACTIONED phải có action (kiểm hoãn tới COMMIT) | AIR-008, AIR-018 |
| 0304 | `moderation_cases` | `priority/violation_type/assignee_id/sla_due_at/row_version`; `moderation_case_history` chỉ thêm | AIR-006 |
| 0305 | `media_rights` | `media_type/visibility/rights_status/license_id/rights_expires_at`; PUBLIC bắt buộc CLEARED | AIR-018 |
| 0306 | `content_aggregates` | `working_revision_id/owner_id/assignee_id/rights_*/scheduled_at/updated_at/row_version` cho bài và video; CHECK `ContentStatus`, PUBLISHED phải có public revision và quyền CLEARED | AIR-004, AIR-005 |
| 0307 | `write_guards` | Trigger chặn publish revision SPONSORED khi cờ OFF/chưa duyệt/hết hạn/sai bài; chặn INSERT bình luận khi `community.video_comments` OFF; đọc cờ bằng `FOR SHARE` | AIR-002, AIR-019 |
| 0308 | `slug_redirects_idempotency` | `article_slug_redirects` tự sinh khi đổi slug và chặn bài khác chiếm slug cũ; `idempotency_keys` (24h) | FR-NEWS-002, AIR-023 |
| 0309 | `lookup_row_versions` | `row_version` + trigger cho `categories/topics/users` | AIR-004 |
| 0310 | `validate_constraints` | `VALIDATE CONSTRAINT` mọi CHECK NOT VALID. **Chạy riêng sau backfill** | AIR-024 |
| 0313 | `staff_access` | `user_identities` (issuer, subject → user), `user_roles` (CHECK 5 vai trò DRAFT), `user_role_history` chỉ thêm (trigger ghi GRANT/REVOKE, thu hồi bắt buộc `tt.reason` → `REASON_REQUIRED`), `staff_sessions` (sha256 token, csrf_hash, MFA, hạn tuyệt đối, thu hồi). Down từ chối nếu đã có lịch sử vai trò | ADR-0004, FR-ID-002/004/006/007 |
| 0314 | `user_status_history` | Lịch sử đình chỉ/kích hoạt tài khoản (chỉ thêm); trigger trên `tt.users.status` bắt buộc `tt.reason` → `REASON_REQUIRED`. Down từ chối nếu đã có lịch sử | WBS-115, FR-ID-004/007 |
| 0320 | `article_revisions` | Revision bài (WBS-022): `status` (DRAFT→IN_REVIEW→…→PUBLISHED→SUPERSEDED), `summary`, `category_id`, `updated_at/by`, `published_at/by`; `articles.published_at`; trigger đóng băng nội dung revision đã rời DRAFT hoặc đang public (`STATE_CONFLICT`), không xóa revision (`APPEND_ONLY`); CHECK slug/title/body. Backfill public revision → PUBLISHED. Down từ chối nếu đã có revision được xuất bản | ADR-0005, FR-NEWS-002/005 |
| 0321 | `validate_article_revisions` | `VALIDATE` các CHECK NOT VALID của 0320 (chạy sau backfill, như 0310) | AIR-024 |
| 0322 | `home_featured` | Tin nổi bật trang chủ theo site: `home_layouts` (row_version = ETag danh sách, chỉ site public-web), `home_featured_articles` (thứ tự 1..10, bài phải cùng site — `VALIDATION_FAILED`), `home_featured_history` (ảnh chụp sau mỗi lần đổi, người thao tác từ `tt.actor_id`, chỉ thêm). Down từ chối nếu đã có lịch sử | FR-DISC-003, WBS-027/051 |
| 0324 | `article_review_workflow` | Hồ sơ duyệt trên revision: `submitted_by/at`, `reviewed_by/at`, `review_note`; trigger chặn bước chuyển trạng thái sai (`STATE_CONFLICT`), trả bài/từ chối thiếu lý do (`REASON_REQUIRED`), sửa lại hồ sơ duyệt; index hàng đợi duyệt. Down từ chối nếu đã có lịch sử duyệt | WBS-024, FR-CMS-002/010, FR-NEWS-004 |
| 0325 | `validate_article_review` | `VALIDATE` các CHECK NOT VALID của 0324 | AIR-024 |
| 0330 | `media_uploads` | Tải lên media qua upload intent (WBS-046/036): cột tệp (`file_name`, `content_type`, `size_bytes`, `checksum_sha256`, `storage_key`), `generation`, `error_code`, `upload_token_hash` + `upload_expires_at` (chỉ khi UPLOADING, token không lưu thô), `created_by`, `uploaded_at`, `updated_at`, `row_version` + trigger touch; CHECK status theo `MediaStatus`, checksum hex, token, finalize đủ thông tin tệp; index thư viện + hàng đợi pipeline. Down từ chối khi đã có object trong kho lưu trữ | ADR-0006, FR-VID-005/006, FR-CMS-009 |
| 0331 | `video_revisions` | Revision video (WBS-046, nền WBS-040): `status`, `summary`, `author_id`, `updated_at/by`; trigger đóng băng như 0320 (`STATE_CONFLICT`), không xóa revision; index danh sách CMS + `videos.media_id`. Down từ chối khi có lịch sử workflow | ADR-0006, FR-VID-008 |
| 0332 | `validate_media_video` | `VALIDATE` các CHECK NOT VALID của 0330/0331 | AIR-024 |
| 0333 | `media_upload_quota` | Index cho hạn mức tải lên theo người tải `(created_by, created_at)` và dọn intent hết hạn `(upload_expires_at) WHERE status = 'UPLOADING'`. Chỉ index | ADR-0006, FR-VID-005 |
| 0334 | `video_review_publish_gate` | Duyệt/xuất bản/gỡ video (WBS-040): `video_revisions.media_id` (media là nội dung revision, đóng băng cùng nội dung), `approved_media_generation`, `submitted_*`, `reviewed_*`, `review_note`, `published_*`; `videos.unpublished_at/by`, `unpublish_reason`. Trigger: bước chuyển trạng thái revision, lý do trả/từ chối (`REASON_REQUIRED`), hồ sơ duyệt không sửa được; **publish gate** khi đổi publish pointer: media READY + VIDEO (`MEDIA_NOT_READY`), quyền media/video CLEARED còn hạn (`RIGHTS_INVALID`), thế hệ media khớp lúc duyệt. `videos.media_id` thành cột dẫn xuất = media của public revision (ghi thẳng bị chặn). Backfill media vào revision working/public. Down từ chối khi có lịch sử duyệt/xuất bản/gỡ | ADR-0006 §6, FR-VID-008/010 |
| 0335 | `validate_video_review` | `VALIDATE` các CHECK NOT VALID của 0334 | AIR-024 |
| 0340 | `content_sources_licenses` | Danh mục nguồn `content_sources` (`kind` ORIGINAL/AGENCY/PARTNER/PRESS_RELEASE/OTHER, tên không trùng) và giấy phép `licenses` (`code`, phạm vi sử dụng, hiệu lực, `evidence_ref`); hàm `tt.license_active(id, at)`. **Chỉ phần tối thiểu ADR-0008 cần**: chưa có API nguồn/giấy phép, metadata quyền trên bài, khóa ngoại `media_assets.license_id` (ADR-0007 §1–§4). Down từ chối khi đã có dữ liệu | ADR-0007 §1, ADR-0008 §2 |
| 0341 | `aggregation_radar` | Radar tin GĐ1: `agg_source_profiles` (1–1 với `content_sources`; PROPOSED/ENABLED/PAUSED/DEGRADED/RETIRED; M0–M3; M2/M3 phải có giấy phép; ENABLED phải có người duyệt; chế độ/giấy phép chỉ đổi kèm bản ghi duyệt khớp; RETIRED là cuối; không xóa; `site_keys` chỉ site public-web), `agg_source_approvals` (chỉ thêm; M2/M3 cần giấy phép hiệu lực → `VALIDATION_FAILED`, người đề xuất tự duyệt → `SOD_VIOLATION`), `agg_source_status_history` (trigger ghi; PAUSED/RETIRED cần `tt.reason` → `REASON_REQUIRED`), `agg_feeds` (https, URL không trùng, `next_fetch_at`/`lease_until`, sức khỏe; không xóa), `agg_fetch_log`, `agg_items` (khử trùng UNIQUE `(nguồn, guid)` và `(nguồn, url_hash)`; không đổi nguồn/feed), `agg_clusters`, `agg_cluster_actions` (chỉ thêm; bỏ qua cần lý do), `agg_category_rules`; cờ `aggregation.radar` (OFF, P2). Down từ chối khi có lịch sử duyệt/trạng thái/hành động cụm hoặc cờ đang bật | ADR-0008 §2–§9, FR-AGG-001…005 |
| 0312 | `site_registry` | `tt.sites` (đồng bộ từ `config/sites/sites.json` bằng `tools/sites/generate-db-sync.mjs`, không xóa, chỉ retired); `articles/videos.primary_site_key`, `categories.site_key` mặc định `www`; nội dung chỉ thuộc site public-web | ADR-0001 D8 |
| 0311 | `optional/privacy_requests.LEGAL_PENDING` | Lưu yêu cầu dữ liệu cá nhân không cần tài khoản (email mã hóa ở tầng ứng dụng). **Chỉ chạy khi Legal chọn kênh API** | AIR-017 |

## 3. Giả định về schema 04 (DBA phải đối chiếu trước khi chạy)

`tests/baseline_04_stub.sql` mô tả **tối thiểu** những gì migration cần. Mỗi migration kiểm tra điều kiện tiên quyết (`require_table`/`require_column`) và **dừng** nếu thiếu, thay vì đoán.

| Mã | Giả định | Dùng ở |
|---|---|---|
| A-01 | Schema `tt`, khóa chính uuid, PostgreSQL ≥ 13 (`gen_random_uuid()`) | tất cả |
| A-02 | `tt.users(id uuid)` | khóa ngoại người duyệt/người xử lý |
| A-03 | `tt.media_assets(id uuid)` | 0302, 0305 |
| A-04 | `tt.feature_gates(key PK, enabled)`; các cột NOT NULL khác (nếu có) phải có default | 0300 |
| A-05 | `tt.articles(id, slug, status, public_revision_id)`; `tt.article_revisions(id, article_id)` | 0301, 0306–0308 |
| A-06 | `tt.videos(id, status, public_revision_id)`; `tt.video_revisions(id, video_id)` | 0301, 0306, 0307 |
| A-07 | `tt.comments(...)`: không có thì guard bị bỏ qua kèm NOTICE | 0307 |
| A-08 | `tt.moderation_cases(id uuid, entity_type, status)` | 0304 |
| A-09 | `tt.categories`, `tt.topics`: không có thì bỏ qua | 0309 |
| A-10 | `tt.users(id, display_name, status)`; status `ACTIVE` = được đăng nhập. Nếu schema 04 đã có bảng vai trò/phiên → sửa 0313 dùng bảng sẵn có | 0313 |
| A-11 | `tt.article_revisions(title, body jsonb, author_id, created_at, UNIQUE(article_id, number))`, `tt.articles(category_id, created_at)`, `tt.categories(is_enabled)`. Ngày xuất bản thật của bài cũ: nếu schema 04 có cột riêng thì thay backfill `published_at = created_at` của 0320 | 0320 |
| A-12 | `tt.media_assets(id, status, created_at)` (status cũ phải map sang enum `MediaStatus` trước 0332), `tt.video_revisions(id, video_id, number, title, created_at, UNIQUE(video_id, number))`, `tt.videos(slug UNIQUE, media_id, created_at)`. Cột tệp của media cũ để NULL (không backfill) | 0330, 0331 |
| A-13 | `tt.videos.media_id` của schema 04 là media đang phát. Sau 0334 nó chỉ còn là media của public revision: video chưa public bị đặt NULL (media chuyển sang revision working). Nếu schema 04 có video UNPUBLISHED cũ, 0335 không đòi bản ghi gỡ (chỉ kiểm lý do khi có `unpublished_at`) | 0334 |
| A-14 | Schema 04 **chưa** có bảng nguồn/giấy phép (ADR-0007 Q4). Nếu đã có, sửa 0340 để dùng bảng sẵn có và 0341 trỏ khóa ngoại vào đó | 0340, 0341 |

Nếu schema 04 dùng **tên cột khác nhưng cùng nghĩa** (ví dụ `rights_state` thay cho `rights_status`), sửa migration để dùng cột có sẵn; đừng để tồn tại hai cột song song.

## 4. Quy trình chạy đề xuất

1. Snapshot/backup và thử rollback trên **bản sao production**.
2. Chạy 0299 → 0309 (mỗi file một transaction). CHECK mới ở dạng `NOT VALID`: áp ngay cho dòng mới/sửa, chưa quét dữ liệu cũ, nên không khóa bảng lâu.
3. Chạy `tools/report_not_valid_violations.sql` (chỉ đọc) để biết ràng buộc nào còn dữ liệu vi phạm và bao nhiêu dòng.
4. Backfill theo §5 cho tới khi báo cáo rỗng.
5. Chạy 0310 (`VALIDATE` chỉ lấy khóa SHARE UPDATE EXCLUSIVE, không chặn đọc/ghi). Nếu còn vi phạm, lệnh dừng và nêu tên ràng buộc; **không xóa ràng buộc để cho qua**.
6. Chạy 0312, rồi chạy SQL đồng bộ site do `node tools/sites/generate-db-sync.mjs` sinh ra. Mọi lần deploy về sau đều chạy lại bước đồng bộ này (idempotent).
7. 0311 chỉ chạy khi Legal quyết định.

Tích hợp công cụ: với EF Core, mỗi file thành một migration gọi `migrationBuilder.Sql(File.ReadAllText(...))` (up) và file `.down.sql` (down). Với DbUp/Flyway/grate, dùng nguyên file với chế độ một transaction mỗi script.

## 5. Backfill trước 0310 (dữ liệu có trước v0.3)

| Ràng buộc | Việc cần làm |
|---|---|
| `ck_feature_gates_enabled_requires_approval` | Cờ đang bật mà không có hồ sơ duyệt: bổ sung `approval_reference/approved_by/approved_at` (PO/Legal), hoặc tắt cờ |
| `ck_ad_placements_dimensions`, `ck_ad_placements_serving_complete`, `ck_ad_placements_approval` | Slot từ v0.2: điền `width/height`; slot APPROVED/ACTIVE cần `media_asset_id`, `alt_text`, `target_url`, người duyệt |
| `ck_moderation_cases_status`, `ck_moderation_cases_entity_type` | Ánh xạ trạng thái/loại cũ sang enum v0.3 (bảng ánh xạ do TL duyệt) |
| `ck_articles_status`, `ck_videos_status` | Ánh xạ trạng thái cũ sang 9 giá trị `ContentStatus` |
| `ck_articles_published_requires_cleared_rights` (và video) | Nội dung đang PUBLISHED phải được xác minh quyền và đặt `rights_status = 'CLEARED'`. Không xác minh được thì unpublish (FR-NEWS-006) |
| `ck_*_published_requires_public_revision`, `ck_*_scheduled_requires_time` | Sửa dữ liệu không nhất quán |
| `ck_media_assets_*` | Đặt `rights_status` cho media đang public |

## 6. Hợp đồng với ứng dụng (BE)

- **Mã lỗi từ DB:** trigger ném `SQLSTATE P0001` với `MESSAGE` là mã trong `x-error-catalog`. BE map như sau: `FEATURE_DISABLED` → 403; `SPONSORSHIP_LABEL_REQUIRED`/`SPONSORSHIP_EXPIRED` → 422; `STATE_CONFLICT` → 409. `APPEND_ONLY` không thuộc catalog: API không có thao tác sửa/xóa bằng chứng, nên lỗi này nghĩa là có bug hoặc truy cập trái phép → trả 500 và bắn cảnh báo bảo mật. Vi phạm CHECK (`23514`) và unique (`23505`) cũng phải map sang `VALIDATION_FAILED`/`IDEMPOTENCY_KEY_REUSED`, không trả 500.
- **Người thao tác:** mỗi transaction ghi cần `SET LOCAL tt.actor_id = '<uuid>'` để nhật ký cờ ghi nhận người bật/tắt.
- **Cờ trong transaction ghi:** dùng `tt.flag_enabled_for_write('<key>')` (đọc bằng `FOR SHARE`). Cache cờ ở tầng ứng dụng chỉ phục vụ UX, TTL ≤ 5 giây.
- **If-Match:** ETag = `W/"<row_version>"`. Trigger tự tăng `row_version`; câu UPDATE của BE vẫn phải có `WHERE row_version = @expected` để trả 412.
- **Idempotency:** INSERT khóa với `status_code = NULL` trước khi xử lý; trùng PK thì đọc lại để phát lại response, hoặc trả 409 nếu `request_hash` khác; cập nhật response khi xong. Job dọn: `DELETE FROM tt.idempotency_keys WHERE expires_at < now()`.
- **Takedown:** ghi action trước, đổi trạng thái `ACTIONED` sau, trong cùng transaction. Worker cập nhật `takedown_propagation` (DONE phải có `completed_at`) để đo SLA.

## 7. Chính sách rollback

- Mỗi `.down.sql` chỉ gỡ đúng các đối tượng bản up đã tạo, dựa vào ledger. Cột có từ schema 04 hoặc SQL v0.2 được giữ lại.
- **Từ chối rollback** nếu bảng chứa bằng chứng/audit có dữ liệu: `takedown_requests` (0303), `moderation_case_history` (0304), `article_slug_redirects` (0308), `privacy_requests` (0311), `licenses`/`content_sources` (0340), `agg_source_approvals`/`agg_source_status_history`/`agg_cluster_actions` (0341; 0341 down cũng từ chối khi cờ `aggregation.radar` đang bật). Muốn rollback phải xuất dữ liệu và có phê duyệt trước.
- 0300 down khôi phục khóa cờ cũ và trạng thái **tại thời điểm chạy 0300**; thay đổi cờ sau đó sẽ mất.
- Down 0301/0302/0305/0306 làm mất dữ liệu ghi vào cột mới (metadata tài trợ, kích thước slot, trạng thái quyền). Xuất dữ liệu trước nếu đã dùng thật.

## 8. Chạy test

```bash
cd doc/07_AI_REVIEW/v0.3/db/tests
npm install
npm test
```

PGlite được ghim ở `0.5.10` (phát hành ngày 14/10/2026). Thư mục `node_modules/` không commit.

## 9. Việc còn mở

- DBA đối chiếu A-01…A-10 với schema 04 thật, rồi chạy lại test trên PostgreSQL bản production (Docker/CI).
- Test đồng thời nhiều kết nối: tắt cờ trong lúc đang ghi bình luận/publish (cần PostgreSQL thật; PGlite không hỗ trợ).
- Đo thời gian `ALTER TABLE ... ADD COLUMN ... DEFAULT` và `VALIDATE` trên dữ liệu cỡ production.
- Guard cho like/report bình luận (bảng chưa rõ trong schema 04); khóa ngoại `media_assets.license_id` → `tt.licenses` (bảng đã có từ 0340; thêm NOT VALID + VALIDATE ở migration riêng khi DBA chốt ADR-0007 Q4).
- Radar tin (0341): job dọn `agg_fetch_log` quá 30 ngày; test khóa `FOR UPDATE SKIP LOCKED` của worker trên PostgreSQL thật (PGlite chỉ một kết nối).
- Job `rights-expiry` và job hết hạn tài trợ (BE) dùng các index `ix_*_rights_expiry`, `ix_*_sponsorship_expiry`.

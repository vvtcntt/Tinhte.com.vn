---
id: ADR-0007
title: Nguồn, giấy phép và xác nhận quyền nội dung của bài viết
status: PROPOSED — ĐỀ XUẤT, CHƯA HIỆN THỰC. Chờ TL (hợp đồng), PO/BA (vai trò, ma trận quyền), Legal (chính sách quyền), DBA (bảng giấy phép schema 04)
date: 2026-11-09
related: ADR-0004, ADR-0005, WBS-023, WBS-032, WBS-124, FR-NEWS-006, FR-NEWS-003, FR-VID-008, AIR-018
---

# ADR-0007 — Nguồn, giấy phép và xác nhận quyền nội dung của bài viết

## Bối cảnh

- FR-NEWS-006: bản tin có metadata nguồn / giấy phép / phạm vi sử dụng; kiểm tra policy trước khi publish.
  - Tiêu chí WBS-023: "Không publish thiếu rights bắt buộc".
  - Ca âm WBS-124: "Bài thiếu metadata rights/source không được publish".
- Đã có:
  - `tt.articles.rights_status` (UNVERIFIED/CLEARED/RESTRICTED/EXPIRED/REVOKED) và `rights_expires_at` (migration 0306).
  - CHECK "PUBLISHED phải CLEARED".
  - `publishArticle` trả 422 `RIGHTS_INVALID` khi quyền chưa CLEARED hoặc đã hết hạn (ADR-0005).
- **Khoảng trống của hợp đồng v0.3:**
  1. `ArticlePatchRequest` có `sourceId` và `licenseId`, nhưng spec không có schema hay endpoint nào cho danh mục nguồn / giấy phép. API hiện trả 422 cho hai trường này.
  2. Không có thao tác nào đặt `rightsStatus` của bài. Vì vậy **không bài nào xuất bản được qua API**: trên máy dev phải đặt bằng SQL, nay bằng lệnh dev (xem "Tạm thời").
  3. `media_assets.license_id` (0305) chưa có khóa ngoại, vì "bảng giấy phép thuộc schema 04 chưa xác định".
  4. Ma trận quyền chỉ có `taxonomy.manage` ("chuyên mục, tag, nguồn"); chưa có quyền xác nhận quyền nội dung.
- CLAUDE.md §3: **không coi giấy phép là đã có**; đổi ma trận quyền phải có BA/PO duyệt.

## Đề xuất (chưa hiện thực)

### 1. Danh mục nguồn và giấy phép

- `tt.content_sources`: `id`, `name`, `kind` (`ORIGINAL` bài tự viết · `AGENCY` hãng tin · `PARTNER` đối tác · `PRESS_RELEASE` thông cáo · `OTHER`), `url`, `is_enabled`, `row_version`.
- `tt.licenses`: `id`, `code`, `name`, `usage_scope` (mô tả phạm vi được dùng), `requires_attribution`, `valid_from` / `valid_to`, `evidence_ref` (hợp đồng trong kho tài liệu), `is_enabled`, `row_version`.
  - Nếu schema 04 đã có bảng giấy phép thì dùng bảng đó; khi ấy thêm khóa ngoại cho `media_assets.license_id`.
- API (thêm vào spec v0.3, theo kiểu `LookupSave` của chuyên mục):
  - `GET|POST /admin/sources`, `PATCH /admin/sources/{sourceId}`;
  - `GET|POST /admin/licenses`, `PATCH /admin/licenses/{licenseId}`.
  - Sửa nguồn cần `taxonomy.manage`; sửa giấy phép cần quyền do Legal quyết (Q2).

### 2. Metadata quyền trên bài

- Thêm cột `source_id`, `license_id`, `attribution` vào `tt.articles`, ở cấp bài như `rights_status`. Mỗi revision mang metadata riêng thì phức tạp hơn mà chưa có yêu cầu (Q5).
- PATCH nhận `sourceId`, `licenseId`, `rightsExpiresAt` và `attribution`. Đổi một trong các trường này thì trigger đặt lại `rights_status = UNVERIFIED`: quyền đã xác nhận không còn đúng khi nguồn đổi.

### 3. Xác nhận quyền (thao tác mới)

- `POST /admin/articles/{articleId}/rights-verification`, có If-Match và Idempotency-Key. Body:
  ```json
  { "decision": "CLEARED | RESTRICTED | REVOKED", "expiresAt": "...", "evidenceRef": "...", "note": "..." }
  ```
- Quyền mới `content.rights_verify` (cần MFA), cấp cho vai trò do PO/BA chọn (Q1).
- Phải có `source_id` và `license_id` mới cho phép CLEARED; giấy phép đã hết hạn thì không được CLEARED.
- Ghi lịch sử chỉ thêm vào `tt.article_rights_history`: ai, khi nào, quyết định gì, bằng chứng nào.
- Tách nhiệm vụ: tác giả không tự xác nhận quyền cho bài của mình → `SOD_VIOLATION` (Q3).

### 4. Kiểm khi publish (bổ sung vào publishArticle)

- Đã có: CLEARED và còn hạn.
- Thêm: phải có nguồn và giấy phép; giấy phép phải đang hiệu lực. Nếu không → 422 `RIGHTS_INVALID`.
- Bài đang công khai hết quyền thì trả 410 (đã có). Cần thêm job chuyển `rights_status = EXPIRED`; chọn WBS cho job này (Q6).

## Tạm thời (đã làm)

- `Tinhte.DbMigrator clear-rights --article <id|slug> [--expires <ISO>]` đặt CLEARED để thử luồng xuất bản trên máy dev.
- Chỉ chạy khi môi trường là `Development`; ngoài đó lệnh báo lỗi và thoát mã 1.
- Không ghi audit, không cấp quyền thật. Gỡ khi có mục 3.

## Hiện thực một phần (10/11/2026)

- Migration 0340 tạo `tt.content_sources` và `tt.licenses` đúng các cột ở §1 (thêm `created_at/updated_at`) và hàm `tt.license_active(id, at)`, vì ADR-0008 (Radar tin) cần danh mục nguồn và giấy phép cho chế độ M2/M3.
- **Chưa làm**: API `/admin/sources`, `/admin/licenses`; §2 metadata quyền trên bài; §3 xác nhận quyền; §4 kiểm khi publish; khóa ngoại `media_assets.license_id`.

## Hiện thực §1 (10/11/2026, vẫn PROPOSED)

- Spec v0.3 thêm 6 thao tác (tag CmsTaxonomy, FR-NEWS-006, scope P0 theo FR): `GET|POST /admin/sources`, `PATCH /admin/sources/{sourceId}`, `GET|POST /admin/licenses`, `PATCH /admin/licenses/{licenseId}` + inventory + truy vết FR-NEWS-006. Không thêm mã lỗi.
- Module News: `CatalogService`, `AdminSourcesController`, `AdminLicensesController`. Danh sách cursor (tên, id), lọc `q`/`isEnabled`; tạo có Idempotency-Key; sửa có If-Match; không xóa — chỉ tắt.
- Quyền: xem cả hai danh mục `content.read`; sửa nguồn `taxonomy.manage` (theo §1); sửa giấy phép quyền **mới** `licenses.manage` (MFA), cấp **tạm** cho APPROVER theo chỉ đạo chủ dự án 10/11/2026 — chờ Legal chốt Q2 (thêm vào `rbac.json` + `Permissions` + test ma trận).
- Giả định chặt hơn trong lúc chờ Q2: `evidenceRef` **bắt buộc** khi tạo giấy phép và không xóa được khi sửa; mã giấy phép không đổi; tên nguồn trùng (không phân biệt hoa thường) / mã trùng → 409.
- CMS: menu "Nguồn & giấy phép" (`/catalog`, `/catalog/licenses`); màn Nguồn thu thập (ADR-0008) chọn nguồn/giấy phép từ danh mục thay cho nhập UUID.
- Vẫn chưa làm: §2–§4 (metadata quyền trên bài — có thể đổi thành danh sách nguồn trên revision theo ADR-0009 §5, xác nhận quyền, cổng xuất bản), khóa ngoại `media_assets.license_id`, lịch sử thay đổi giấy phép (hiện chỉ có `row_version`/`updated_at`).
- Có bản ghi giấy phép **không** có nghĩa là đã được phép khai thác: Legal nhập và chịu trách nhiệm (CLAUDE.md §3).

## Câu hỏi cần quyết

| # | Ai | Câu hỏi |
|---|---|---|
| Q1 | PO + BA | Vai trò nào có `content.rights_verify`: APPROVER, hay vai trò mới (ví dụ `RIGHTS_MANAGER`, sẽ phải sửa CHECK `ck_user_roles_role`)? |
| Q2 | Legal | Ai được tạo/sửa giấy phép? Có cần bằng chứng (`evidenceRef`) bắt buộc không? |
| Q3 | Legal + PO | Bài tự viết (`ORIGINAL`) có cần bước xác nhận quyền không, hay CLEARED mặc định khi chọn nguồn ORIGINAL? (Đề xuất: vẫn cần xác nhận, vì ảnh trong bài có thể không phải của Tinhte.) |
| Q4 | DBA | Schema 04 có bảng nguồn / giấy phép chưa? Tên cột? |
| Q5 | TL | Metadata quyền ở cấp bài hay cấp revision? |
| Q6 | PO + TL | Job hết hạn quyền thuộc WBS nào? Bài hết quyền thì UNPUBLISHED, hay chỉ ẩn và giữ trạng thái? |

## Hệ quả

- Khi được duyệt:
  - một migration mới (dải 0326+);
  - sửa spec v0.3 (thêm 7 thao tác) và cập nhật inventory, CHANGELOG;
  - thêm quyền vào `config/rbac/rbac.json` + `Permissions`;
  - màn CMS nguồn / giấy phép / xác nhận quyền (WBS-032).
- Trước khi duyệt: không bài nào xuất bản được ngoài máy dev. Đây là chủ ý: fail-closed theo FR-NEWS-006.

## Phương án đã loại

- **Cho `PATCH rightsStatus` trực tiếp:** quyền thành một trường như mọi trường khác, không có lịch sử, không tách quyền, dễ bị đặt CLEARED nhầm.
- **Tự CLEARED khi chọn nguồn và giấy phép:** chọn giấy phép không chứng minh được nội dung thật sự nằm trong phạm vi giấy phép cho phép.

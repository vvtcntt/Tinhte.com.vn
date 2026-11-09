---
id: ADR-0005
title: Bài viết theo revision + publish pointer; truy cập DB bằng Npgsql thuần
status: PROPOSED (Sprint 0) — chờ Tech Lead + DBA duyệt
date: 2026-11-09
related: ADR-0001 (D8), ADR-0003, WBS-022, WBS-023, WBS-024, WBS-025, FR-NEWS-001/002/004/005, FR-CMS-001/004/006
---

# ADR-0005 — Bài viết theo revision + publish pointer; Npgsql thuần

## Bối cảnh

- WBS-022 là module đầu tiên đọc/ghi nghiệp vụ. ADR-0003 hoãn chọn Dapper hay EF Core tới module này.
- FR-NEWS-005: sửa bài đã công bố bằng revision, không thay bản công khai trước khi đủ điều kiện, lưu người sửa/thời điểm/lý do.
- Hợp đồng v0.3 đã có `workingRevisionId`/`publicRevisionId` (0306) và trigger chặn publish SPONSORED (0307), nhưng revision chưa có trạng thái, sapo, chuyên mục và chưa có gì chặn sửa revision đã public.

## Quyết định

### 1. Truy cập DB: Npgsql thuần, không thêm Dapper/EF

- SQL viết tay trong `Infrastructure/ArticleSql.cs` + service; tham số luôn có kiểu (`NpgsqlDbType`), `timestamptz` luôn đổi về UTC.
- Lý do: schema do migration SQL quản lý (contract-first), truy vấn ít và cần kiểm soát `FOR UPDATE`/keyset; thêm phụ thuộc chưa đáng. Xem lại khi số module có SQL ≥ 3 hoặc mapping lặp lại rõ (Dapper ghim phiên bản ≥ 2 tuần tuổi).

### 2. Mô hình revision (migration 0320/0321)

- `tt.article_revisions.status`: `DRAFT → IN_REVIEW → CHANGES_REQUESTED | REJECTED | APPROVED → PUBLISHED → SUPERSEDED`. Bảng chuyển trạng thái chi tiết thuộc WBS-024.
- **Đóng băng ở DB** (trigger `trg_article_revisions_frozen`): nội dung revision chỉ sửa được khi `DRAFT` **và** chưa là public revision; đã rời `DRAFT` thì không quay lại `DRAFT`; revision không xóa được. Cột trạng thái/duyệt tài trợ/xuất bản vẫn đổi được để workflow chạy.
- `summary`, `category_id` nằm trên revision → đổi sapo/chuyên mục của bài đang public không lộ trước khi publish. Publish chép `category_id` sang `tt.articles`.
- PATCH: working revision còn `DRAFT` → sửa tại chỗ; ngược lại tạo revision mới (số kế tiếp, `author_id` = người sửa, `change_reason`) và trỏ `working_revision_id` vào đó. `slug`, `rightsExpiresAt` là thuộc tính của bài (sửa ngay).
- PATCH bị từ chối (409) khi bài `IN_REVIEW`/`APPROVED`/`SCHEDULED`/`RETRACTED` để không âm thầm vô hiệu một quyết định duyệt.

### 3. Publish pointer

- `POST /admin/articles/{id}/publish` chỉ nhận revision `APPROVED`, quyền `CLEARED` còn hạn (`RIGHTS_INVALID`), If-Match + Idempotency-Key, quyền `content.publish` (MFA). Một transaction: đổi `public_revision_id` (trigger 0307 kiểm SPONSORED: cờ, duyệt tài trợ, cửa sổ hiệu lực) → revision cũ `SUPERSEDED`, revision mới `PUBLISHED` + `published_at/by`.
- **Hiển thị công khai dựa vào con trỏ, không dựa vào `status = PUBLISHED`**: bài có public revision, không `UNPUBLISHED`/`RETRACTED`, quyền còn hạn → hiển thị bản public trong khi bản nháp mới đi duyệt. Từng public rồi bị gỡ/hết quyền → 410 `CONTENT_WITHDRAWN`.
- SPONSORED chỉ hiển thị khi cờ `ads.sponsored_publication` ON (đọc `tt.feature_gates`, cùng nguồn với trigger), tài trợ đã duyệt, trong cửa sổ và đã cấu hình `News:SponsoredLabel` (câu chữ Legal duyệt). Thiếu một điều kiện → 404 (fail-closed).

### 4. Đa subdomain

- Bài thuộc site của chuyên mục (`primary_site_key = categories.site_key`). API công khai lọc theo site của Host; redirect slug cũ trả `Location` tương đối, không viết cứng host.

## Còn thiếu (không thuộc WBS-022)

| Việc | WBS |
|---|---|
| Gửi duyệt / duyệt / từ chối / yêu cầu sửa, SoD (người duyệt ≠ tác giả), đặt `APPROVED` | WBS-024 |
| Nguồn/giấy phép, đặt `rights_status = CLEARED`; PATCH `sourceId`/`licenseId` hiện trả 422 | WBS-023 |
| Lên lịch, preview token | WBS-025 |
| Sự kiện `ContentPublished` qua outbox trong transaction publish; audit chung | WBS-020, WBS-019 |
| Feed công khai `GET /articles`, ảnh đại diện, video liên quan | FR-DISC-004/005 |

## Hệ quả

- **Được:** bất biến "bản công khai không đổi trước khi publish" được giữ ở DB, không chỉ ở service; workflow sau chỉ cần đổi `status` của revision.
- **Mất:** bài có bản nháp đang chờ có hai revision cần hiển thị (working vs public) — CMS (WBS-030) phải thể hiện rõ. Đổi `rights_status` qua SQL/workflow làm tăng `row_version` (ETag) của bài.

## Phương án đã loại

- **Sửa tại chỗ revision public rồi "đánh dấu" thay đổi:** bản công khai đổi ngay, trái FR-NEWS-005.
- **Hiển thị theo `status = PUBLISHED`:** bài biến mất khỏi site khi bản nháp mới chuyển `IN_REVIEW`.
- **Dapper/EF Core ngay từ module đầu:** chưa có lặp lại đủ để bù phụ thuộc; EF không dùng để migrate (contract-first).

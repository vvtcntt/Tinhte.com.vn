---
id: ADR-0003
title: Truy cập PostgreSQL, migration runner và hợp đồng API dùng chung
status: PROPOSED (Sprint 0) — chờ Tech Lead + DBA duyệt
date: 2026-11-09
related: ADR-0001, ADR-0002, WBS-013, WBS-014, AIR-004, AIR-023, AIR-024
---

# ADR-0003 — Truy cập PostgreSQL, migration runner và hợp đồng API dùng chung

## Bối cảnh

- Backend đã có khung Web API dạng controller (ADR-0002) nhưng chưa nối DB.
- Migration v0.3 (`doc/07_AI_REVIEW/v0.3/db/migrations`, 0299–0312) mới chỉ chạy trong bộ test Node/PGlite.
- Hợp đồng v0.3 quy định nhiều cơ chế dùng chung cho mọi module:
  - cursor ký HMAC;
  - `If-Match`/`ETag` theo `row_version`;
  - `Idempotency-Key` 24 giờ;
  - lỗi từ trigger DB phải thành `Problem.code`.
- Mỗi module tự làm lại các cơ chế này thì sẽ lệch nhau.

## Quyết định

### 1. Kết nối PostgreSQL (WBS-013)

- Một `NpgsqlDataSource` cho cả tiến trình, bọc trong `ITinhteDatabase`. Chuỗi kết nối lấy từ `ConnectionStrings:Tinhte`, cấp qua biến môi trường hoặc secret manager, không đưa vào file cấu hình.
- Transaction ghi dùng `InTransactionAsync(actorId, …)`. Hàm này luôn chạy `set_config('tt.actor_id', …, true)` để trigger và nhật ký cờ ghi nhận người thao tác (db/README §6).
- **Chưa chọn ORM.** Platform dùng Npgsql thuần. Chọn Dapper hay EF Core khi làm module đầu tiên (WBS-022), bằng ADR riêng. Lý do: schema do migration SQL quản lý (contract-first), nên EF chỉ dùng để truy vấn, không dùng để migrate.
- Chưa cấu hình DB thì API vẫn khởi động. `/health/ready` trả 200 ở Development; ở môi trường khác trả 503 (`missing`/`unreachable`).

### 2. Migration runner (`Tinhte.DbMigrator`)

- Là một **bước deploy riêng**, không chạy lúc API khởi động: `dotnet run --project backend/src/Tinhte.DbMigrator -- up|status`.
- Mỗi file chạy trong một transaction. Trong transaction đó runner lấy `pg_advisory_xact_lock` và kiểm tra lại nhật ký, nên hai tiến trình chạy cùng lúc không áp trùng.
- Nhật ký nằm ở `tt_meta.schema_history`: version, checksum sha256 tính sau khi chuẩn hóa xuống dòng LF, thời gian chạy. Nhật ký không đặt trong `tt_migration` vì bản down của 0299 xóa schema đó.
- File đã chạy mà nội dung bị sửa (checksum lệch) thì runner dừng. Muốn thay đổi thì viết migration mới.
- Rollback (`.down.sql`) **không tự động**. Chạy tay theo db/README §7 khi có phê duyệt.
- Sau `up`, runner chạy SQL đồng bộ `tt.sites` sinh từ Site Registry (`SiteDbSync`, bản C# của `tools/sites/generate-db-sync.mjs`). Test so khớp hai bản từng dòng.
- **Nguồn migration tạm trỏ vào `doc/07_AI_REVIEW/v0.3/db/migrations`**, vì đó là bản DRAFT chờ DBA/TL duyệt; chép ra chỗ khác sẽ sinh hai bản lệch nhau. Khi v0.3 được duyệt, chuyển thư mục sang `db/migrations` và đổi `Database:Migrations:Directory`.
- **Stub schema 04** (`tests/baseline_04_stub.sql`) được áp như version `0000`, **chỉ ở Development**; migrator từ chối áp nó ở môi trường khác. Staging/production phải dùng schema 04 thật do DBA cung cấp (db/README §3, A-01…A-09).

### 3. Ánh xạ lỗi (WBS-014)

- `DomainProblemException(code, …)` là cách duy nhất service báo lỗi nghiệp vụ. Mã không có trong catalog bị từ chối ngay tại chỗ ném.
- `DomainExceptionFilter` (toàn cục) đổi ngoại lệ thành `application/problem+json`:
  - `PostgresException` P0001 do `tt.raise_domain_error` ném → `MESSAGE` chính là mã lỗi, `DETAIL` gửi cho client.
  - CHECK/NOT NULL/FK/unique/sai kiểu → 422 `VALIDATION_FAILED`, chỉ nêu tên ràng buộc, không lộ giá trị hay câu SQL.
  - `APPEND_ONLY` và các mã ngoài catalog → 500, kèm log `Critical` cảnh báo bảo mật.
  - Mất kết nối DB → 503 `DEPENDENCY_UNAVAILABLE`.
- Npgsql bật `IncludeErrorDetail`, vì cần `DETAIL` của trigger. Hệ quả: log ngoại lệ chưa xử lý có thể chứa giá trị dữ liệu. Cần lọc khi gắn hệ thống log tập trung (WBS-021).

### 4. Phân trang cursor

- Tham số `[FromQuery] PageRequest`: `limit` 1–100, mặc định 20; `cursor` ≤ 512 ký tự. Phản hồi `CursorPage<T>` có dạng `{ items, nextCursor }`.
- Cursor có dạng `base64url(payload).base64url(HMAC-SHA256)`, với payload gồm version, scope và vị trí (khóa sắp xếp + id).
- Scope = `{siteKey}:{list}[:{hash bộ lọc}]`, nên cursor không dùng chéo được giữa danh sách, bộ lọc hay subdomain (ADR-0001).
- Cursor bị sửa, sai scope hoặc ký bằng khóa khác → 400 `INVALID_CURSOR`.
- Khóa ký đọc từ `Pagination:CursorSigningKey` (base64, tối thiểu 32 byte, mọi instance dùng chung một khóa) và **bắt buộc ngoài Development**: thiếu khóa thì API không khởi động. Development dùng khóa ngẫu nhiên theo tiến trình.
- Tham số query/route/header sai (ví dụ `limit=0`) → 400 `BAD_REQUEST`. Body đúng cú pháp nhưng vi phạm ràng buộc → 422 `VALIDATION_FAILED`.

### 5. Optimistic concurrency

- ETag = `W/"<row_version>"`, gắn bằng `SetETag(rowVersion)`.
- Action gọi `RequireIfMatch()` **sau khi đã nạp tài nguyên**, vì catalog đánh giá 404 trước 428/412:
  - thiếu `If-Match` → 428 `PRECONDITION_REQUIRED`;
  - không khớp, gửi `*`, gửi nhiều giá trị hoặc giá trị không phải ETag → 412 `REVISION_CONFLICT`.
- Câu UPDATE vẫn phải có `WHERE row_version = @expected`. 0 dòng bị ảnh hưởng mà tài nguyên còn tồn tại → `ETags.RevisionConflict()`.

### 6. Idempotency

- Gắn `[Idempotent]` cho action. Đây là resource filter chạy trước model binding, lưu vào `tt.idempotency_keys` (migration 0308).
- Phạm vi khóa: actor (claim `sub`) + method + `"{siteKey} {route template}"`.
- Hash request gồm method, path, query và body JSON đã chuẩn hóa (sắp xếp khóa). Vì vậy khác thứ tự trường vẫn tính là cùng request, còn khác đường dẫn (ví dụ comment vào video khác) là request khác.
- Hành vi:
  - thiếu khóa hoặc độ dài ngoài 8–200 → 400;
  - chưa đăng nhập → 401;
  - gửi lại đúng request → phát lại status, body, `ETag`, `Location`, kèm `Idempotent-Replayed: true`;
  - cùng khóa nhưng request khác → 409 `IDEMPOTENCY_KEY_REUSED`.
- **Chỉ lưu response 2xx/3xx.** Lỗi 4xx/5xx không để lại tác dụng phụ (transaction đã rollback), nên khóa được giải phóng để client sửa request rồi gửi lại.
- Bản ghi "đang xử lý" treo quá 60 giây (tiến trình chết giữa chừng) được nhận lại.
- **Khoảng hở đã biết:** ghi nghiệp vụ và lưu response idempotency nằm ở hai transaction khác nhau. Nếu tiến trình chết giữa hai bước, lần thử lại sau 60 giây sẽ chạy action lần nữa. Muốn đóng hoàn toàn thì service phải ghi idempotency trong cùng transaction; làm khi có endpoint thật (WBS-020, outbox).

## Đề xuất bổ sung hợp đồng (chờ TL/PO duyệt, chưa sửa spec)

| # | Vấn đề | Đề xuất |
|---|---|---|
| C-1 | Request cùng `Idempotency-Key` **đang được xử lý**: catalog chưa có mã riêng | Tạm dùng 409 `STATE_CONFLICT` + `Retry-After: 1`. Đề xuất thêm `IDEMPOTENCY_IN_PROGRESS` (409) |
| C-2 | Lỗi 500: catalog chưa có mã (đã nêu ở ADR-0002) | `INTERNAL_ERROR` (500) |
| C-3 | `limit`: spec ghi "Example bounds; final limit TBD performance/PO" | Giữ 1–100, mặc định 20 cho tới khi PO chốt |

## Kiểm chứng (09/11/2026)

- `dotnet test`:
  - **97/97 PASS** khi có DB (PostgreSQL 18.6 qua `tools/pglite-server`);
  - 84 PASS và 13 SKIP khi không có `TT_TEST_PG`.
- Test DB:
  - migrate từ đầu rồi chạy lại (không đổi gì), phát hiện checksum lệch, đồng bộ `tt.sites`, cờ production chỉ `community.moderation` ON;
  - trigger 0307 → 403 `FEATURE_DISABLED`, CHECK → 422, transaction đặt actor và rollback đúng;
  - idempotency: phát lại, khác payload, tách theo actor, giải phóng khóa khi lỗi, request đang chạy.
- `Tinhte.DbMigrator up` chạy hai lần trên PGlite: lần đầu áp 14 file, lần hai không đổi gì. API thật có DB: `/health/ready` → `database: ok`.
- **Chưa kiểm:**
  - PostgreSQL bản production và nhiều kết nối đồng thời (PGlite chỉ có một phiên). CI có job PostgreSQL 17, nhưng chưa chạy trên GitHub.
  - Hiệu năng truy vấn idempotency, khóa advisory khi nhiều instance cùng deploy.

## Hệ quả

- **Được:**
  - module mới chỉ cần gắn `[Idempotent]`, gọi `RequireIfMatch()`, trả `CursorPage<T>` và ném `DomainProblemException`; hợp đồng được giữ ở một chỗ;
  - lỗi trigger DB tự thành đúng mã của catalog.
- **Mất:**
  - thêm một bước deploy (migrator);
  - test DB cần PostgreSQL. Máy dev không có Docker dùng PGlite, phải `Pooling=false` và không kiểm được đồng thời.

## Phương án đã loại

- **Chạy migration lúc API khởi động:** nhiều instance cùng migrate, và API cần quyền DDL. Bị loại.
- **EF Core Migrations hoặc DbUp:** EF sinh schema từ code, trái với contract-first SQL. DbUp được nhưng thêm phụ thuộc, trong khi runner tự viết dưới 200 dòng và có test. Có thể đổi sau mà không đổi file SQL.
- **Cursor dạng offset hoặc id trần:** không ổn định khi có bài mới chèn vào, lại dễ bị dò. Hợp đồng yêu cầu ký HMAC.
- **Lưu cả response 4xx cho idempotency:** client không thử lại được sau khi sửa lỗi, trong khi 4xx không có tác dụng phụ cần bảo vệ.

# Tinhte.com.vn — quy tắc cho Claude / AI agent và lập trình viên

## 1. Đa subdomain — BẮT BUỘC (ADR-0001, yêu cầu chủ dự án 09/11/2026)

`tinhte.com.vn` sẽ có **nhiều subdomain**. Mọi thiết kế phải giữ được nguyên tắc: thêm subdomain = thêm một mục trong Site Registry.

- **Nguồn sự thật duy nhất:** `config/sites/sites.json` (schema `sites.schema.json`). Mỗi site có `key` ổn định, dùng làm `site_key` trong DB, analytics, log và thư mục FE.
- **Không viết cứng hostname/domain** trong code, cấu hình app hay test fixture của src. Host luôn suy ra qua `@tinhte/sites` (TS) hoặc `SiteRegistry` (C#). CI chặn bằng `node tools/check-hardcoded-hosts.mjs`.
- **API cùng origin theo site:** `https://{host-của-site}/api/v1/*`. Không thêm CORS giữa các subdomain.
- **Cookie:** chỉ dùng cookie host-only với tiền tố `__Host-`. **Cấm** `Domain=.tinhte.com.vn`. SSO giữa các site qua OIDC.
- **CMS:** app riêng (`frontend/apps/cms`). Bề mặt API `admin` chỉ phục vụ trên site có `apiSurfaces: ["admin"]`.
- **Phân giải host tồn tại ở 2 nơi:** `frontend/packages/sites/src/index.ts` và `backend/src/Tinhte.Platform/Sites/SiteRegistry.cs`. Sửa một bên thì phải sửa bên kia và cập nhật `config/sites/resolution-cases.json`, vì cả hai bộ test đều chạy file này.
- **Dữ liệu:** `articles/videos.primary_site_key`, `categories.site_key` (migration 0312); `tt.sites` được đồng bộ bằng `node tools/sites/generate-db-sync.mjs`. Không xóa site, chỉ chuyển `retired`.
- **Hạ tầng:** DNS/TLS/edge lấy từ `node tools/sites/plan.mjs <env>`; Caddyfile local sinh bằng `node tools/sites/generate-caddyfile.mjs`. Không sửa tay `infra/local/Caddyfile`.
- **Runbook thêm subdomain:** `doc/adr/ADR-0001-multi-subdomain-site-registry.md` mục "Thêm một subdomain mới".

## 2. Backend: ASP.NET Core Web API dạng Controller (ADR-0002)

- Endpoint mới **luôn** là controller: kế thừa `TinhteControllerBase`, `sealed`, đặt trong `Controllers/` của module (hoặc `Tinhte.Api/Controllers/` cho endpoint nền tảng). **Không** dùng Minimal API (`MapGet`/`MapPost`).
- Route tường minh theo đường dẫn hợp đồng: `[Route(ApiRoutes.Prefix + "/...")]`. Controller mỏng gọi service; response là DTO `record` có kiểu.
- Lỗi nghiệp vụ dùng `Problem(code, title, detail)` với mã trong `x-error-catalog`. Không đặt `[Produces("application/json")]` ở base. Attribute validation của record đặt trên tham số.

## 2b. DB và hợp đồng dùng chung (ADR-0003)

- Truy cập DB qua `ITinhteDatabase`; ghi dùng `InTransactionAsync(actorId, …)` (đặt `tt.actor_id`).
- Service ném `DomainProblemException(code, …)`. Lỗi trigger/CHECK được tự đổi thành Problem: **không** tự bắt `PostgresException` rồi trả 500.
- Danh sách: `[FromQuery] PageRequest` → `CursorPage<T>`, scope cursor lấy từ `CursorScope("tên-danh-sách", bộ-lọc)`.
- Sửa có `If-Match`: nạp tài nguyên (404 trước) → `RequireIfMatch()` → UPDATE `WHERE row_version = @expected` → `SetETag(...)`.
- POST mà hợp đồng yêu cầu `IdempotencyKey`: gắn `[Idempotent]`.
- Migration áp bằng `Tinhte.DbMigrator` (bước deploy riêng, không chạy lúc API khởi động). **Không sửa migration đã áp** (checksum lệch sẽ chặn), viết migration mới. Stub schema 04 chỉ dùng ở Development.
- Test cần DB dùng `[DbFact]` và biến `TT_TEST_PG`. Máy không có Docker: `tools/pglite-server` với `Pooling=false`.

## 2c. Xác thực staff + RBAC (ADR-0004)

- Endpoint admin: `[RequirePermission(Permissions.X)]`. Chỉ cần đăng nhập: `[RequireStaffSession]`. Quyết định phụ thuộc dữ liệu: `User.RequirePermission(...)` trong service. **Kiểm quyền, không kiểm tên vai trò.**
- Vai trò/quyền mới: sửa `StaffRoles`/`Permissions`/`PermissionMatrix` **và** CHECK `ck_user_roles_role` bằng migration mới. Ma trận là DRAFT: đổi phải có BA/PO duyệt.
- Không tin vai trò từ IdP; không dùng cookie auth/JWT cho CMS; không đặt cookie có `Domain`.
- Request ghi bằng cookie phiên phải có `X-CSRF-Token` (do `CsrfMiddleware` kiểm) — đừng tắt cho endpoint riêng.

## 3. Hợp đồng, phạm vi, pháp lý

- **Tài liệu gốc** ở `doc/` (gói 07 v0.2): **không sửa**. Đề xuất v0.3 (OpenAPI, migration, truy vết) ở `doc/07_AI_REVIEW/v0.3/` là DRAFT chờ TL/PO/Legal duyệt.
- **Contract-first:** đổi API thì sửa spec v0.3 trước.
  - Mã lỗi theo `x-error-catalog`; cờ theo `x-feature-flags`.
  - `ErrorCatalog`/`FeatureFlagKeys` trong C# có test khớp spec.
- **Không được tự ý:**
  - bật cờ public (production mặc định OFF);
  - nâng P1/P2 lên P0;
  - coi giấy phép là đã có;
  - tự cộng effort (phải có PM/Finance ký).
- **Migration:**
  - mỗi file một transaction;
  - dùng helper `tt_migration.*` để bản down chỉ gỡ đúng phần đã tạo;
  - CHECK trên bảng cũ thêm ở dạng `NOT VALID`, rồi VALIDATE ở 0310.

## 4. Lệnh kiểm tra trước khi xong việc

```bash
node tools/sites/validate.mjs && node tools/check-hardcoded-hosts.mjs && node tools/sites/generate-caddyfile.mjs --check
node --test "frontend/packages/sites/test/*.test.ts"
dotnet test backend/Tinhte.slnx      # đặt TT_TEST_PG để chạy cả test DB
cd frontend && npm test && npm run build && npm run typecheck
```

Contract tĩnh v0.3 và migration (PGlite): xem `.github/workflows/ci.yml`, job `contract` và `db-migrations`.

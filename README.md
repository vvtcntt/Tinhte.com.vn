# Tinhte.com.vn

Nền tảng tin tức công nghệ: News, Shorts video, CMS biên tập, SEO; cộng đồng (P0*) và quảng cáo tài trợ (P0-ADS) nằm sau release gate.
Stack: **ASP.NET Core 10 Web API dạng Controller** (modular monolith, [ADR-0002](doc/adr/ADR-0002-aspnetcore-controller-web-api.md)) · **Next.js 16** · **PostgreSQL**.

> **Trạng thái:** khung Sprint 0, DRAFT. Kiến trúc **đa subdomain** theo [ADR-0001](doc/adr/ADR-0001-multi-subdomain-site-registry.md). Quy tắc làm việc: [CLAUDE.md](CLAUDE.md).

## Cấu trúc

```
config/sites/        Site Registry — nguồn sự thật cho mọi subdomain (+ schema, ca kiểm thử phân giải dùng chung)
config/rbac/         Ma trận vai trò → quyền (DRAFT) — dùng chung cho backend (nhúng lúc build) và CMS
backend/             ASP.NET Core 10 Web API (Controllers): Tinhte.Api (host + Controllers/), Tinhte.Platform (site, lỗi, cờ, base controller, DB, phân trang/ETag/idempotency), Tinhte.DbMigrator (áp migration), Modules/* (News, Video, Community, Ads, Identity — mỗi module có Controllers/)
frontend/            npm workspaces: apps/web (site công khai, đa subdomain), apps/cms (CMS), packages/sites (@tinhte/sites)
tools/               Script sinh từ registry: validate, plan (DNS/TLS/edge), db-sync, Caddyfile; chặn hostname viết cứng; pglite-server (PostgreSQL dev không cần Docker)
infra/local/         Caddyfile sinh tự động (edge local)
doc/                 Tài liệu dự án (gói 07 v0.2 — không sửa), ADR, AI review + đề xuất v0.3 (OpenAPI, migration, truy vết)
.github/workflows/   CI: sites, backend, frontend, contract, db-migrations
```

## Chạy local

Yêu cầu: .NET SDK 10, Node ≥ 22.18 (khuyến nghị 24 LTS), Docker (tùy chọn, cho PostgreSQL + edge).

```bash
node tools/sites/validate.mjs                      # kiểm registry
dotnet run --project backend/src/Tinhte.Api        # API: http://localhost:5080 (môi trường site "local")
cd frontend && npm install
npm run dev -w @tinhte/web                         # http://www.tinhte.localhost:3000
npm run dev -w @tinhte/cms                         # http://cms.tinhte.localhost:3001
docker compose up -d                               # tùy chọn: PostgreSQL + edge http://www.tinhte.localhost:8080
```

### Database (WBS-013, [ADR-0003](doc/adr/ADR-0003-data-access-and-common-api-contracts.md))

Có Docker: `docker compose up -d postgres`. Không có Docker: PGlite (PostgreSQL 18 chạy WASM, **một phiên**, chỉ cho dev/test):

```bash
cd tools/pglite-server && npm ci && npm start      # 127.0.0.1:54329, dữ liệu trong bộ nhớ (PGLITE_DATA_DIR=./.data để giữ lại)
```

Áp migration (stub schema 04 + v0.3) rồi chạy API có DB (PowerShell: `$env:NAME = "..."`):

```bash
export DOTNET_ENVIRONMENT=Development ConnectionStrings__Tinhte="Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres;Pooling=false"
dotnet run --project backend/src/Tinhte.DbMigrator -- up        # hoặc: status
dotnet run --project backend/src/Tinhte.Api                     # /health/ready → database: ok
TT_TEST_PG="$ConnectionStrings__Tinhte" dotnet test backend/Tinhte.slnx   # gồm test DB (không có TT_TEST_PG thì bỏ qua)
```

`Pooling=false` chỉ cần với PGlite. Stub schema 04 chỉ áp ở Development; staging/production dùng schema 04 thật của DBA.
Ngoài Development, API cần `Pagination:CursorSigningKey` (base64 ≥ 32 byte, từ secret manager) — thiếu thì không khởi động.

### Đăng nhập staff + RBAC (WBS-017/018, [ADR-0004](doc/adr/ADR-0004-staff-auth-session-rbac.md))

CMS đăng nhập qua OIDC với IdP bất kỳ (chưa chọn IdP). Cấu hình `Auth:Staff:Authority`, `Auth:Staff:ClientId`, `Auth__Staff__ClientSecret` (secret); ngoài Development thiếu thì API không khởi động. Redirect URI cần đăng ký ở IdP: `https://{host cms}/api/v1/auth/staff/oidc/callback`.

Vai trò lưu trong DB, không lấy từ IdP. Cấp ADMIN đầu tiên:

```bash
dotnet run --project backend/src/Tinhte.DbMigrator -- grant-role --issuer https://idp.example --subject <sub-của-người-đó> --role ADMIN --reason "bootstrap"
```

CMS gọi API admin bằng cookie `__Host-tt_cms_session`; request ghi gửi kèm header `X-CSRF-Token` lấy từ cookie `__Host-tt_cms_csrf` (`frontend/apps/cms/src/lib/api.ts`).

**Đăng nhập CMS trên máy dev** (chưa có IdP thật): `tools/dev-oidc` là IdP giả, **chỉ dùng local**, chỉ nghe 127.0.0.1.

```bash
node tools/dev-oidc/server.mjs                                   # http://127.0.0.1:5090
dotnet run --project backend/src/Tinhte.DbMigrator -- grant-role --issuer http://127.0.0.1:5090 --subject dev-admin --role APPROVER --reason "dev"
cd backend/src/Tinhte.Api && Auth__Staff__Authority=http://127.0.0.1:5090 Auth__Staff__ClientId=tinhte-cms-dev Auth__Staff__ClientSecret=dev-only-secret Auth__Staff__RequireHttpsMetadata=false dotnet run
npm run dev -w @tinhte/cms                                       # http://cms.tinhte.localhost:3001 → Đăng nhập
```

Chạy API từ thư mục `backend/src/Tinhte.Api` (hoặc `dotnet run --project`) để nạp `appsettings.Development.json`; chạy dll từ gốc repo sẽ thiếu cấu hình tin `X-Forwarded-Host` → API trả 400 "Host header does not match".

Trình duyệt tự trỏ `*.localhost` về 127.0.0.1, nên không cần sửa file hosts. Khi chạy Next trực tiếp, `/api/v1/*` được rewrite sang API (`TT_API_ORIGIN`). Qua edge (Caddy hoặc CDN) thì edge route `/api/*`.

### Bài viết: revision + publish pointer (WBS-022, [ADR-0005](doc/adr/ADR-0005-news-revisions-publish-pointer.md))

- CMS (site `cms`): `GET/POST /admin/articles`, `GET/PATCH /admin/articles/{id}` (If-Match), `GET /admin/articles/{id}/revisions[/{revisionId}?compareTo=]`, `POST /admin/articles/{id}/publish` (`content.publish` + MFA, Idempotency-Key).
- Công khai (site `public-web`): `GET /articles/{slug}` — chỉ public revision; slug cũ → 308; đã gỡ/hết quyền → 410.
- Revision rời `DRAFT` hoặc đang public bị DB đóng băng (migration 0320): sửa bài đang public tạo revision mới, bản công khai giữ nguyên tới lần publish sau.
- Duyệt (WBS-024): `POST /admin/articles/{id}/submit|approve|request-changes|reject` — tách nhiệm vụ (người viết/sửa revision không tự duyệt hay xuất bản: 403 `SOD_VIOLATION`), trả bài/từ chối bắt buộc lý do (422 `REASON_REQUIRED`), người gửi duyệt thành người phụ trách tiếp theo; migration 0324/0325 chặn ở DB bước chuyển sai và sửa hồ sơ duyệt. CMS: nút Gửi duyệt / Duyệt / Yêu cầu sửa / Từ chối, banner lý do cho tác giả.
- Chưa có: rights/nguồn (WBS-023 — publish cần quyền `CLEARED`), lịch/preview (WBS-025), outbox (WBS-020). Bài tài trợ chỉ hiển thị khi cờ `ads.sponsored_publication` ON **và** đã cấu hình `News:SponsoredLabel` (câu chữ Legal duyệt).

**Màn Bài viết trên CMS** (WBS-030, phần danh sách của WBS-031): `/articles` có danh sách (lọc trạng thái, "bài của tôi", tìm, xem thêm) và nút "Bài mới"; `/articles/{id}` gồm:
- trình soạn tạm: đoạn cách bằng dòng trống, `##` / `###` là tiêu đề, `>` là trích dẫn, có nút xem trước;
- lưu bằng If-Match; khi người khác vừa lưu thì báo xung đột kèm nút tải lại;
- lịch sử phiên bản, so sánh với phiên bản trước;
- nút xuất bản (`content.publish` + MFA). Chỉ bật khi bài đã được duyệt. Hợp đồng chưa có cách xác nhận quyền nội dung (WBS-023, đề xuất [ADR-0007](doc/adr/ADR-0007-content-sources-licenses-rights.md) chờ TL/PO/Legal/DBA), nên trên máy dev dùng lệnh `dotnet run --project backend/src/Tinhte.DbMigrator -- clear-rights --article <id|slug>` (chỉ chạy ở Development).

Form chọn chuyên mục dùng `GET /admin/categories` (chỉ đọc; tạo/sửa chuyên mục thuộc WBS-023/032). Trên máy dev, thêm chuyên mục bằng SQL trong pgAdmin (DB `tinhte`):

```sql
INSERT INTO tt.categories (slug, name) VALUES ('cong-nghe', 'Công nghệ'), ('danh-gia', 'Đánh giá'), ('thu-thuat', 'Thủ thuật');
```

### Video + media trong CMS (WBS-046, [ADR-0006](doc/adr/ADR-0006-media-upload-storage-adapter.md))

- API (site `cms`): `POST /admin/media/upload-intents` → PUT tệp lên `uploadUrl` → `POST /admin/media/{id}/finalize` → `GET /admin/media/{id}` (trạng thái), `GET /admin/media` (thư viện); `GET/POST /admin/videos`, `GET/PATCH /admin/videos/{id}` (If-Match, gắn `mediaId`). Migration 0330–0333.
- CMS: `/videos` (danh sách, lọc, tìm, xem thêm), `/videos/new` (tải lên có tiến độ, checksum, thử lại, trạng thái tự cập nhật), `/videos/{id}` (sửa + xử lý 412, đổi/chọn media), `/videos/media` (thư viện).
- **Dev:** `Media:StorageProvider` mặc định `Local` ở Development — tệp lưu ở `Media:LocalRoot` (mặc định `%TEMP%/tinhte-media`), xử lý **giả lập** (chỉ kiểm định dạng, không transcode; `Media:SimulatedStepDelay`). Ngoài Development mặc định `None` → tải lên trả 503 cho tới khi chọn vendor (WBS-035/039). Media mới luôn PRIVATE + UNVERIFIED.
- Hạn mức tải lên theo người (429 + Retry-After) và dọn intent hết hạn (`UPLOAD_EXPIRED`) — `Media:MaxIntentsPerWindow`, `Media:MaxPendingUploadBytes`, `Media:ExpiredUploadGrace` (DRAFT, chờ PO).
- **Duyệt/xuất bản/gỡ (WBS-040, ADR-0006 §6, migration 0334/0335):** `POST /admin/videos/{id}/submit|approve|request-changes|reject|publish|unpublish` theo quy ước duyệt bài WBS-024 (SoD, MFA, lý do). Media là nội dung revision: đổi media của video đã public = revision mới. Publish chỉ khi media READY và quyền media + video CLEARED còn hạn (422 `MEDIA_NOT_READY`/`RIGHTS_INVALID`) — quyền CLEARED do WBS-023 đặt (dev: SQL). CMS: khối "Duyệt & xuất bản" trên `/videos/{id}`.
- Chưa có: lên lịch video (WBS-025), lan truyền gỡ grant/CDN/feed (WBS-039/128), transcode/quét mã độc/CDN (WBS-036…039), xem trước/phát trong CMS.

## Đa subdomain (tóm tắt ADR-0001)

- Mỗi subdomain là một mục trong `config/sites/sites.json`. Routing (Next `proxy.ts`, middleware ASP.NET), CSP, robots/sitemap, tên cookie, bề mặt API, bảng `tt.sites`, DNS/TLS/edge đều **suy ra** từ đó.
- **Thêm subdomain:** thêm mục `status: "planned"` → CI kiểm → deploy đồng bộ `tt.sites` → `plan.mjs` liệt kê DNS → PO/Legal duyệt → `status: "active"`. Không phải sửa code.
- Host lạ trả 400, alias trả 308 về host chính, app không phục vụ site đó trả 404. Cookie `__Host-` chỉ thuộc host. Admin API chỉ chạy trên `cms`.

## Kiểm chứng Sprint 0 (09/11/2026)

| Hạng mục | Kết quả |
|---|---|
| `@tinhte/sites` (node:test) | 11/11 PASS, gồm test "thêm subdomain chỉ bằng registry" và bộ ca dùng chung với C# |
| Backend `dotnet test` | PASS với DB (PGlite): registry, định tuyến theo host, chặn Origin, khớp spec, quy ước controller, Problem 400/422/409/500, phân trang cursor, ETag/If-Match, idempotency, migration runner, trigger DB → Problem, RBAC + CSRF, đăng nhập OIDC đầu-cuối với IdP giả, redirect lỗi callback cho trình duyệt. 163/163 với DB; không có DB: 136 PASS + 27 SKIP. Build 0 cảnh báo |
| CMS (`@tinhte/cms`) | 9/9 test thư viện (RBAC, CSRF cookie, returnTo, Problem, thông điệp lỗi đăng nhập khớp spec); chạy thật trên trình duyệt: đăng nhập qua dev IdP → tổng quan → điều hướng theo quyền → màn không đủ quyền / cần MFA → đăng xuất; lỗi đăng nhập (chưa có vai trò, bị từ chối) về `/login` có thông báo và giữ `returnTo`; 375px không cuộn ngang |
| Next.js `web`, `cms` | `next build` OK; chạy production và kiểm HTTP theo Host: 200/308/400/404, header bảo mật, robots/sitemap theo môi trường |
| Script registry | validate, plan, db-sync, Caddyfile `--check`, chặn hostname viết cứng: OK |
| Contract tĩnh v0.3 | 34/34 PASS |
| Migration v0.3 + 0312 (PGlite, PostgreSQL 18.6) | 151/151 PASS; sau 0313, 0314 (WBS-115), 0320/0321 (WBS-022): 185/185 PASS |
| `Tinhte.DbMigrator` | `up` hai lần trên PGlite: 14 file rồi không đổi; API thật có DB: `/health/ready` OK |
| **PostgreSQL 18 thật** (máy dev, Windows) | `Tinhte.DbMigrator up`: 15 migration (0000 stub + 0299…0313) + đồng bộ `tt.sites`; `dotnet test` **163/163 PASS** với `TT_TEST_PG` trỏ DB `tinhte_test` |
| **Chưa chạy** | Docker Compose, CI trên GitHub (job backend có PostgreSQL 17), schema 04 thật, kiểm thử đồng thời nhiều kết nối, E2E trình duyệt tự động |

## Sprint 0 so với WBS (G10)

| WBS | Hạng mục | Trạng thái |
|---|---|---|
| 009 | Repo, cấu trúc, quy ước | Có khung (chưa `git init`/branch protection: chờ quyết định repo) |
| 010 | IaC | Kế hoạch DNS/TLS/edge sinh từ registry; Terraform/CDN thật: TBD vendor |
| 011 | CI/CD | Workflow CI (chưa chạy trên GitHub); CD: TBD |
| 012 | Khung ASP.NET Core Web API (Controller) modular | Có |
| 013 | DB schema/migration | `Tinhte.DbMigrator` + `ITinhteDatabase` (Npgsql); migration draft v0.3 + 0312 tại `doc/07_AI_REVIEW/v0.3/db` (chờ DBA; schema 04 thật chưa có, dev dùng stub) |
| 014 | API conventions | Có: problem+json, error catalog, lỗi DB → Problem, cursor HMAC, ETag/If-Match, Idempotency-Key, phân giải site, chặn Origin |
| 015 | Khung Next.js SSR | Có (web + cms, đa site) |
| 016 | Design system | Chưa (chờ UX 03) |
| 017–018 | Staff auth, RBAC | Có: OIDC (IdP bất kỳ) + PKCE, phiên phía server thu hồi được, MFA theo amr/acr, CSRF double-submit, ma trận quyền (DRAFT chờ BA/PO); migration 0313; UI CMS: đăng nhập, khung, menu theo quyền, cảnh báo MFA, đăng xuất. Chưa: IdP thật, màn hình nghiệp vụ |
| 115 | Quản trị tài khoản | Có: `GET /admin/users` (lọc trạng thái/vai trò/tìm, cursor), chi tiết + ETag, đình chỉ/kích hoạt lại (lý do, Idempotency-Key, thu hồi phiên), `PUT …/roles` (If-Match, lý do, chống tự bỏ ADMIN); lịch sử trạng thái (migration 0314) + vai trò (0313); màn CMS Tài khoản |
| 019 | Audit | Lịch sử cấp/thu hồi vai trò (0313); audit chung + `GET /admin/audit-logs`: chưa |
| 020–021 | Outbox, observability | Chưa; đã có `SiteKey` trong log scope và `X-TT-Site` |
| 030 | CMS editor bài viết | Có (tạm): danh sách, tạo nháp, soạn/sửa với If-Match + xung đột, xem trước, lịch sử + so sánh, nút xuất bản; `GET /admin/categories`. Chưa: editor rich-text theo UX-CMS-02 (ảnh/nhúng), autosave/offline |
| 031 | CMS dashboard + hàng đợi duyệt/trạng thái | Có: `/review` với tab theo vai trò (Chờ duyệt, Cần tôi sửa, Chờ xuất bản, Tôi đã gửi; chờ lâu nhất trước, cảnh báo chờ > 24 giờ — SLA chờ PO chốt), số việc trên menu + thẻ "Việc của bạn" ở Tổng quan, cập nhật sau mỗi thao tác duyệt/xuất bản. Chưa: hàng đợi video (sau khi gộp WBS-040) |
| 022 | Content type, article revision, publish pointer | Có (API, [ADR-0005](doc/adr/ADR-0005-news-revisions-publish-pointer.md)): tạo/sửa/đọc/danh sách bài CMS, lịch sử + diff revision, publish pointer, đọc công khai theo slug + redirect 308; migration 0320/0321. `dotnet test` sau khi gộp với WBS-115: 194/194 PASS với DB (PGlite), 151 PASS + 43 SKIP không DB. Chưa: duyệt (024), rights (023), lịch (025), outbox (020), UI CMS (030) |
| 024 | Draft/review/approve/reject state machine | Có (API + nút CMS): gửi duyệt, duyệt (MFA), yêu cầu sửa, từ chối; SoD cả khi xuất bản; lý do bắt buộc; migration 0324/0325. `dotnet test` 203/203 với DB; đã chạy trên trình duyệt với 2 người dùng (tác giả gửi → người duyệt trả bài kèm lý do → tác giả thấy lý do). Chưa: hàng đợi duyệt `GET /admin/work-queue` (WBS-031), thông báo cho người phụ trách |
| 046 | Video upload/status/editor CMS + media library | Có (Sprint 0, [ADR-0006](doc/adr/ADR-0006-media-upload-storage-adapter.md)): API upload intent/finalize/trạng thái/thư viện + video CMS (cursor, ETag/If-Match, Idempotency-Key), kho Local + xử lý giả lập chỉ ở dev; hạn mức tải lên (429) + dọn intent hết hạn; migration 0330–0333; màn CMS Video/thư viện media. `dotnet test` 221/221 PASS với DB (PGlite), 169 PASS + 52 SKIP không DB; migration PGlite 226/226. Chưa: vendor lưu trữ/transcode (035–039), workflow publish video (040) |
| 040 | Video revision, media refs, rights/publish gate | Có (Sprint 0, [ADR-0006 §6](doc/adr/ADR-0006-media-upload-storage-adapter.md)): gửi duyệt/duyệt/yêu cầu sửa/từ chối/xuất bản/gỡ (If-Match, Idempotency-Key, SoD, MFA); media theo revision; publish gate READY + CLEARED ở service và trigger 0334; migration 0334/0335; CMS nút duyệt/xuất bản/gỡ. `dotnet test` 238/238 PASS với DB (PGlite), 180 PASS + 58 SKIP không DB; migration PGlite 253/253. Chưa: lên lịch (025), lan truyền gỡ (039/128) |

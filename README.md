# Tinhte.com.vn

Nền tảng tin tức công nghệ: News, Shorts video, CMS biên tập, SEO; cộng đồng (P0*) và quảng cáo tài trợ (P0-ADS) nằm sau release gate.
Stack: **ASP.NET Core 10 Web API dạng Controller** (modular monolith, [ADR-0002](doc/adr/ADR-0002-aspnetcore-controller-web-api.md)) · **Next.js 16** · **PostgreSQL**.

> **Trạng thái:** khung Sprint 0, DRAFT. Kiến trúc **đa subdomain** theo [ADR-0001](doc/adr/ADR-0001-multi-subdomain-site-registry.md). Quy tắc làm việc: [CLAUDE.md](CLAUDE.md).

## Cấu trúc

```
config/sites/        Site Registry — nguồn sự thật cho mọi subdomain (+ schema, ca kiểm thử phân giải dùng chung)
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

Trình duyệt tự trỏ `*.localhost` về 127.0.0.1, nên không cần sửa file hosts. Khi chạy Next trực tiếp, `/api/v1/*` được rewrite sang API (`TT_API_ORIGIN`). Qua edge (Caddy hoặc CDN) thì edge route `/api/*`.

## Đa subdomain (tóm tắt ADR-0001)

- Mỗi subdomain là một mục trong `config/sites/sites.json`. Routing (Next `proxy.ts`, middleware ASP.NET), CSP, robots/sitemap, tên cookie, bề mặt API, bảng `tt.sites`, DNS/TLS/edge đều **suy ra** từ đó.
- **Thêm subdomain:** thêm mục `status: "planned"` → CI kiểm → deploy đồng bộ `tt.sites` → `plan.mjs` liệt kê DNS → PO/Legal duyệt → `status: "active"`. Không phải sửa code.
- Host lạ trả 400, alias trả 308 về host chính, app không phục vụ site đó trả 404. Cookie `__Host-` chỉ thuộc host. Admin API chỉ chạy trên `cms`.

## Kiểm chứng Sprint 0 (09/11/2026)

| Hạng mục | Kết quả |
|---|---|
| `@tinhte/sites` (node:test) | 11/11 PASS, gồm test "thêm subdomain chỉ bằng registry" và bộ ca dùng chung với C# |
| Backend `dotnet test` | 97/97 PASS với DB (PGlite): registry, định tuyến theo host, chặn Origin, khớp spec, quy ước controller, Problem 400/422/409/500, phân trang cursor, ETag/If-Match, idempotency, migration runner, trigger DB → Problem. Không có DB: 84 PASS + 13 SKIP. Build 0 cảnh báo |
| Next.js `web`, `cms` | `next build` OK; chạy production và kiểm HTTP theo Host: 200/308/400/404, header bảo mật, robots/sitemap theo môi trường |
| Script registry | validate, plan, db-sync, Caddyfile `--check`, chặn hostname viết cứng: OK |
| Contract tĩnh v0.3 | 34/34 PASS |
| Migration v0.3 + 0312 (PGlite, PostgreSQL 18.6) | 151/151 PASS |
| `Tinhte.DbMigrator` | `up` hai lần trên PGlite: 14 file rồi không đổi; API thật có DB: `/health/ready` OK |
| **Chưa chạy** | Docker Compose, CI trên GitHub (job backend nay có PostgreSQL 17), PostgreSQL bản production, kiểm thử đồng thời, E2E trình duyệt |

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
| 017–019 | Staff auth, RBAC, audit | Chưa (chờ chọn IdP); audit DB có trong migration |
| 020–021 | Outbox, observability | Chưa; đã có `SiteKey` trong log scope và `X-TT-Site` |

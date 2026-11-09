# Tinhte.com.vn

Nền tảng tin tức công nghệ: News, Shorts video, CMS biên tập, SEO; cộng đồng (P0*) và quảng cáo tài trợ (P0-ADS) nằm sau release gate.
Stack: **ASP.NET Core 10 Web API dạng Controller** (modular monolith, [ADR-0002](doc/adr/ADR-0002-aspnetcore-controller-web-api.md)) · **Next.js 16** · **PostgreSQL**.

> **Trạng thái:** khung Sprint 0, DRAFT. Kiến trúc **đa subdomain** theo [ADR-0001](doc/adr/ADR-0001-multi-subdomain-site-registry.md). Quy tắc làm việc: [CLAUDE.md](CLAUDE.md).

## Cấu trúc

```
config/sites/        Site Registry — nguồn sự thật cho mọi subdomain (+ schema, ca kiểm thử phân giải dùng chung)
backend/             ASP.NET Core 10 Web API (Controllers): Tinhte.Api (host + Controllers/), Tinhte.Platform (site, lỗi, cờ, base controller), Modules/* (News, Video, Community, Ads, Identity — mỗi module có Controllers/)
frontend/            npm workspaces: apps/web (site công khai, đa subdomain), apps/cms (CMS), packages/sites (@tinhte/sites)
tools/               Script sinh từ registry: validate, plan (DNS/TLS/edge), db-sync, Caddyfile; chặn hostname viết cứng
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

Trình duyệt tự trỏ `*.localhost` về 127.0.0.1, nên không cần sửa file hosts. Khi chạy Next trực tiếp, `/api/v1/*` được rewrite sang API (`TT_API_ORIGIN`). Qua edge (Caddy hoặc CDN) thì edge route `/api/*`.

## Đa subdomain (tóm tắt ADR-0001)

- Mỗi subdomain là một mục trong `config/sites/sites.json`. Routing (Next `proxy.ts`, middleware ASP.NET), CSP, robots/sitemap, tên cookie, bề mặt API, bảng `tt.sites`, DNS/TLS/edge đều **suy ra** từ đó.
- **Thêm subdomain:** thêm mục `status: "planned"` → CI kiểm → deploy đồng bộ `tt.sites` → `plan.mjs` liệt kê DNS → PO/Legal duyệt → `status: "active"`. Không phải sửa code.
- Host lạ trả 400, alias trả 308 về host chính, app không phục vụ site đó trả 404. Cookie `__Host-` chỉ thuộc host. Admin API chỉ chạy trên `cms`.

## Kiểm chứng Sprint 0 (09/11/2026)

| Hạng mục | Kết quả |
|---|---|
| `@tinhte/sites` (node:test) | 11/11 PASS, gồm test "thêm subdomain chỉ bằng registry" và bộ ca dùng chung với C# |
| Backend `dotnet test` | 38/38 PASS (registry, định tuyến theo host, chặn Origin, khớp spec, ranh giới module, quy ước controller, 400/422/409/500 theo Problem, OpenAPI dev); build 0 cảnh báo (warnings-as-errors) |
| Next.js `web`, `cms` | `next build` OK; chạy production và kiểm HTTP theo Host: 200/308/400/404, header bảo mật, robots/sitemap theo môi trường |
| Script registry | validate, plan, db-sync, Caddyfile `--check`, chặn hostname viết cứng: OK |
| Contract tĩnh v0.3 | 34/34 PASS |
| Migration v0.3 + 0312 (PGlite, PostgreSQL 18.6) | 151/151 PASS |
| **Chưa chạy** | Docker Compose, CI trên GitHub, PostgreSQL thật, kiểm thử đồng thời, E2E trình duyệt |

## Sprint 0 so với WBS (G10)

| WBS | Hạng mục | Trạng thái |
|---|---|---|
| 009 | Repo, cấu trúc, quy ước | Có khung (chưa `git init`/branch protection: chờ quyết định repo) |
| 010 | IaC | Kế hoạch DNS/TLS/edge sinh từ registry; Terraform/CDN thật: TBD vendor |
| 011 | CI/CD | Workflow CI (chưa chạy trên GitHub); CD: TBD |
| 012 | Khung ASP.NET Core Web API (Controller) modular | Có |
| 013 | DB schema/migration | Draft v0.3 + 0312 tại `doc/07_AI_REVIEW/v0.3/db` (chờ DBA, chưa có migrator chạy trong app) |
| 014 | API conventions | Một phần: problem+json, error catalog, phân giải site, chặn Origin |
| 015 | Khung Next.js SSR | Có (web + cms, đa site) |
| 016 | Design system | Chưa (chờ UX 03) |
| 017–019 | Staff auth, RBAC, audit | Chưa (chờ chọn IdP); audit DB có trong migration |
| 020–021 | Outbox, observability | Chưa; đã có `SiteKey` trong log scope và `X-TT-Site` |

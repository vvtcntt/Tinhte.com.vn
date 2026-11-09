---
id: ADR-0001
title: Kiến trúc đa subdomain dựa trên Site Registry
status: PROPOSED (Sprint 0) — chờ Tech Lead, PO, Security duyệt
date: 2026-11-09
related: WBS-009..016, WBS-052, AIR-001, AIR-016, contract 07_OPENAPI_TINHTE_v0.3_DRAFT.yaml
---

# ADR-0001 — Kiến trúc đa subdomain dựa trên Site Registry

## Bối cảnh

`tinhte.com.vn` sẽ có **nhiều subdomain** theo thời gian: trang tin, CMS, media, và có thể thêm các mảng nội dung hoặc dịch vụ mới. Nếu tên host bị viết cứng rải rác trong code, cấu hình cookie/CORS/CSP, SEO và hạ tầng, thì mỗi lần thêm subdomain sẽ phải sửa nhiều nơi, dễ sót, dễ tạo lỗ hổng. Hai lỗ hổng điển hình: cookie đặt cho domain cha bị subdomain khác ghi đè (cookie tossing), và CORS mở quá rộng.

Yêu cầu của chủ dự án (09/11/2026): **thiết kế đồng bộ để sau này thêm subdomain dễ dàng.**

## Quyết định

**D1. Site Registry là nguồn sự thật duy nhất.**
`config/sites/sites.json` (schema `config/sites/sites.schema.json`) liệt kê mọi site. Mỗi site có `key` ổn định (`^[a-z][a-z0-9-]{1,30}$`), dùng chung cho: `site_key` trong DB, chiều phân tích analytics, log, thư mục giao diện riêng của FE (nếu có), và tên route ở edge.
**Không viết cứng hostname trong code**: mọi nơi đều suy ra host từ registry. CI chặn bằng `tools/check-hardcoded-hosts.mjs`.

**D2. Phân giải theo Host ở mọi tầng.**
Edge (CDN/reverse proxy), Next.js (`proxy.ts`) và ASP.NET Core (`SiteResolutionMiddleware`) dùng cùng một luật:
- Host không có trong registry → `400 BAD_REQUEST` (RFC 9110: Host không hợp lệ).
- Host là alias → `308` về host chính tắc (ví dụ apex `tinhte.com.vn` → `www.tinhte.com.vn`).
- Site không phục vụ app/bề mặt API được yêu cầu → `404`.

**D3. API cùng origin theo từng site.**
API được phục vụ tại `https://{host-của-site}/api/v1/*`; edge chuyển tiếp tới dịch vụ API. API biết site qua Host, nên **không cần CORS** giữa các subdomain, cookie chỉ thuộc host đó, và analytics có `site_key` do server suy ra.
Một host API riêng dùng token (cho mobile/đối tác) chỉ thêm khi thật sự cần, dưới dạng một site `kind: api`.

**D4. Cookie chỉ thuộc host, tiền tố `__Host-`.**
- Tuyệt đối không đặt `Domain=.tinhte.com.vn`. Lý do: chống cookie tossing, cô lập CMS khỏi site công khai.
- Tên cookie suy ra từ registry: `session: member` → `__Host-tt_member_session`; `session: staff` → `__Host-tt_cms_session`.
- SSO giữa các site công khai dùng OIDC (`prompt=none`) với IdP, không dùng cookie domain cha.
- CSRF có hai lớp: (1) `Origin` phải trùng host của site (đã có trong Sprint 0); (2) double-submit `X-CSRF-Token` theo từng host (làm cùng WBS-017).

**D5. CMS tách subdomain và tách ứng dụng.**
`cms.` dùng app Next.js riêng (`frontend/apps/cms`). Bề mặt API `admin` (`/api/v1/admin/*`, `/api/v1/auth/staff/*`) **chỉ** phục vụ trên site có `apiSurfaces` chứa `admin`. Luôn `noindex`.

**D6. Media trên host không cookie.**
`media.` phục vụ ảnh/HLS qua URL có chữ ký; CSP của các site cho phép `img-src`/`media-src` từ host media suy ra từ registry.

**D7. SEO theo site.**
- Mỗi site có `robots.txt` và `sitemap.xml` riêng.
- Staging/local **luôn** `noindex` (cờ `indexable` của môi trường).
- Nội dung có `primary_site_key`, canonical trỏ về site chính. Khi đăng chéo (giai đoạn 2), canonical vẫn về site chính.

**D8. Dữ liệu sẵn sàng cho nhiều site.**
- Migration `0312_site_registry` tạo `tt.sites`, được **đồng bộ từ registry** bằng SQL sinh tự động (`tools/sites/generate-db-sync.mjs`, chạy khi deploy). Site không bao giờ bị xóa, chỉ chuyển `retired`.
- Thêm `articles.primary_site_key`, `videos.primary_site_key`, `categories.site_key`, mặc định `www`.
- API công khai lọc theo site của Host, không cần đổi hợp đồng.
- API admin sẽ thêm `siteKey` (thay đổi **bổ sung**, không phá vỡ) khi xuất hiện site nội dung thứ hai.

**D9. Môi trường suy ra từ hậu tố domain.**
- Host = `<subdomain>.<domainSuffix>`, hoặc `<domainSuffix>` khi subdomain là `@`.
- Hậu tố: production `tinhte.com.vn`; staging `staging.tinhte.com.vn`; local `tinhte.localhost`. Trình duyệt tự trỏ `*.localhost` về 127.0.0.1, nên dev nhiều subdomain không cần sửa file hosts.
- `resolveStatuses` của môi trường quyết định site `planned` có được phân giải hay không (staging/local: có; production: không).

**D10. Hạ tầng sinh từ registry.**
- `tools/sites/plan.mjs <env>` xuất kế hoạch DNS/TLS/edge route để IaC dùng.
- `tools/sites/generate-caddyfile.mjs` sinh `infra/local/Caddyfile` cho edge local.
- TLS: wildcard `*.tinhte.com.vn` + apex (và `*.staging.tinhte.com.vn`), ưu tiên chứng chỉ do CDN quản lý.

**D11. Cờ tính năng.**
Trong MVP, cờ là toàn cục (registry cờ ở OpenAPI `x-feature-flags`). Nếu cần bật theo site, thêm cột `site_key` vào `tt.feature_gates` (thay đổi bổ sung); khóa cờ giữ nguyên.

**D12. Quan sát.**
Mỗi request API có `SiteKey` trong log scope và header phản hồi `X-TT-Site`. Analytics lấy site từ Host, không tin giá trị client gửi.

## Thêm một subdomain mới (runbook)

1. Thêm một mục vào `config/sites/sites.json` với `status: "planned"`. CI tự kiểm schema, trùng key/host và hostname viết cứng.
2. Nếu là site công khai có giao diện riêng, tạo `frontend/apps/web/src/sites/<key>/` (tùy chọn); mặc định dùng layout chung.
3. Deploy: pipeline chạy `node tools/sites/generate-db-sync.mjs` để upsert vào `tt.sites`.
4. Hạ tầng: `node tools/sites/plan.mjs production` liệt kê DNS/route cần thêm. Wildcard TLS đã bao phủ.
5. PO/Legal duyệt nếu là site công khai, rồi đổi `status` sang `"active"`.
6. **Không cần** sửa cookie, CORS, CSP, robots hay route API: tất cả suy ra từ registry.

## Hệ quả

- **Được:** thêm site rẻ và ít rủi ro; chính sách bảo mật đồng nhất; không có CORS; CMS cô lập.
- **Mất:** edge phải route `/api/*` cho mọi site công khai; SSO qua OIDC phức tạp hơn cookie domain cha; logic phân giải tồn tại ở hai ngôn ngữ (TS và C#), được giữ đồng bộ bằng test dùng chung một registry.
- **Hợp đồng v0.3:** `servers` đổi thành `https://{siteHost}/api/v1`; tên cookie thêm tiền tố `__Host-`.

## Phương án đã loại

| Phương án | Lý do loại |
|---|---|
| Cookie `Domain=.tinhte.com.vn` để SSO | Mọi subdomain đọc/ghi được cookie (cookie tossing); không cô lập được CMS |
| Một host `api.` dùng chung + CORS cho mọi subdomain | Danh sách origin phải bảo trì; cookie cross-site gặp SameSite và ITP; dễ cấu hình sai |
| Một app Next.js chứa cả CMS | CSP/phiên/bề mặt tấn công lẫn với site công khai |
| Danh sách host theo từng môi trường viết trong code | Thêm site phải sửa nhiều nơi; staging dễ lộ index |

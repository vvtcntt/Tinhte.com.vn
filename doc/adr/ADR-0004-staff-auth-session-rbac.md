---
id: ADR-0004
title: Đăng nhập staff qua OIDC, phiên phía server và RBAC theo ma trận quyền
status: PROPOSED (Sprint 0) — chờ Tech Lead + Security duyệt; ma trận vai trò chờ BA/PO duyệt
date: 2026-11-09
related: ADR-0001 (D4/D5), ADR-0003, WBS-017, WBS-018, FR-ID-002, FR-ID-004, FR-ID-006, FR-ID-007
---

# ADR-0004 — Đăng nhập staff qua OIDC, phiên phía server và RBAC theo ma trận quyền

## Bối cảnh

- Hợp đồng v0.3 quy định:
  - `cmsSession` = cookie `__Host-tt_cms_session`, chỉ có trên site cms;
  - `/auth/staff/oidc/start|callback` (state, nonce, PKCE; MFA cho quyền cao), `/auth/staff/logout`, `/admin/auth/session` (StaffSession);
  - `X-CSRF-Token` cho request ghi dùng cookie. Spec ghi rõ: "final scheme depends on auth ADR".
- **IdP chưa được chọn.**
- **Chưa có danh mục vai trò chính thức.** FR-ID-002 chỉ nêu tách "author/editor/approver/moderator/admin"; guide 07 §52 ghi "chuẩn hóa RBAC role ids" là việc còn mở.
- Schema 04 thật không có trong repo, nên không biết đã có bảng vai trò/phiên hay chưa.

## Quyết định

### 1. OIDC chuẩn, không phụ thuộc IdP

- Dùng handler `OpenIdConnect` chuẩn của ASP.NET Core: authorization code + PKCE, `response_mode=query`. State, nonce và PKCE do handler sinh và kiểm, lưu trong cookie `__Host-tt_cms_oidc_*` (Path=/, Secure, HttpOnly, SameSite=Lax).
- Cấu hình nằm ở `Auth:Staff`: `Authority`, `ClientId`, `ClientSecret` (lấy từ secret manager), `AcrValues`, `MfaAmrValues`, `MfaAcrValues`, `IdleTimeout` (30 phút), `AbsoluteLifetime` (12 giờ). Đổi IdP chỉ cần đổi cấu hình.
- **Ngoài Development thiếu cấu hình IdP thì API không khởi động** (`ValidateOnStart`). Development thiếu cấu hình thì `oidc/start` trả 503 `DEPENDENCY_UNAVAILABLE`.
- `returnTo` chỉ nhận đường dẫn tương đối trên chính host cms (`^/(?!/)[^\s\\]*$`, tối đa 500 ký tự) để chặn open redirect. Giá trị sai → 400.
- Kết quả callback:

  | Tình huống | Phản hồi |
  |---|---|
  | state/nonce/PKCE sai | 400 `OIDC_STATE_INVALID` |
  | IdP trả lỗi | 400 `BAD_REQUEST` |
  | IdP từ chối (`access_denied`) | 403 `FORBIDDEN` |
  | User chưa có vai trò hoặc bị khóa | 403 `FORBIDDEN` |

- **Trình duyệt** (`Accept` ưu tiên `text/html`) không nhận các Problem trên. Thay vào đó API trả 302 về `/login?error={code}&reason={reason}[&returnTo=]` để CMS hiện thông báo dễ hiểu (A-4, đã đưa vào spec v0.3 ở `x-browser-error-redirect`, CHANGELOG §10).
  - Giá trị `reason`: `state_invalid`, `idp_error`, `denied`, `no_role`, `inactive`, `unavailable`.
  - Thiếu cấu hình IdP, IdP không truy cập được hoặc DB phiên không dùng được → 503 `DEPENDENCY_UNAVAILABLE` (trình duyệt: `reason=unavailable`). `oidc/start` kiểm trước cả ba thứ để báo lỗi ngay, không để người dùng đăng nhập ở IdP xong mới lỗi.
  - `returnTo` chỉ được gắn khi state đã xác minh được.

### 2. Vai trò lấy từ DB, không lấy từ IdP

- Đăng nhập lần đầu: tạo `tt.users` và liên kết `(issuer, subject)` trong `tt.user_identities`.
- **Chưa có vai trò thì không cấp phiên.** User vẫn được tạo để admin thấy và cấp quyền.
- Vai trò nằm ở `tt.user_roles` và được đọc lại ở **mỗi request**, nên thu hồi có hiệu lực ngay.
- Mọi lần cấp/thu hồi đều ghi vào `tt.user_role_history` qua trigger. Bảng này chỉ cho thêm dòng. Thu hồi bắt buộc có lý do (`SET LOCAL tt.reason`), thiếu thì DB báo `REASON_REQUIRED` (FR-ID-007).
- Admin đầu tiên tạo bằng `Tinhte.DbMigrator grant-role --issuer … --subject … --role ADMIN --reason …`. Lệnh này cũng ghi lịch sử, với actor = hệ thống.

### 3. Phiên phía server (không dùng cookie auth của ASP.NET)

- Cookie `__Host-tt_cms_session` chứa token ngẫu nhiên 256 bit. DB chỉ lưu sha256 (`tt.staff_sessions.id_hash`), nên lộ DB không lộ cookie dùng được.
- Thuộc tính cookie: HttpOnly, Secure, SameSite=Lax, Path=/, **không Domain** (ADR-0001 D4).
- Phiên hết hiệu lực khi gặp bất kỳ điều kiện nào:
  - quá 30 phút không hoạt động (`last_seen_at` được gia hạn tối đa mỗi phút một lần);
  - quá hạn tuyệt đối 12 giờ;
  - bị thu hồi khi đăng xuất, đăng nhập lại (phiên cũ thành `ROTATED`), hoặc user bị đình chỉ (`RevokeAllForUserAsync`, dùng cho WBS-115);
  - user không còn `ACTIVE`.
- Handler phiên chỉ xác thực trên site có `session: staff` (cms). Cookie có lọt sang site khác cũng bị bỏ qua (ADR-0001 D5).

### 4. CSRF lớp 2: double-submit gắn với phiên

- Lúc đăng nhập, server cấp token CSRF ngẫu nhiên qua cookie `__Host-tt_cms_csrf`. Cookie này **không HttpOnly** để JS của CMS đọc được, SameSite=Strict. DB lưu sha256 của token trong `staff_sessions.csrf_hash`.
- Mọi request ghi được xác thực bằng phiên phải gửi `X-CSRF-Token` khớp với hash đó (so sánh thời gian hằng). Thiếu hoặc sai → 403 `CSRF_INVALID`.
- `CsrfMiddleware` chạy sau authentication và trước authorization, đúng thứ tự lỗi của catalog: 401 → 403 `CSRF_INVALID` → 403 `FORBIDDEN`.
- Lớp 1 (`Origin` trùng site) vẫn giữ nguyên như Sprint 0.

### 5. RBAC: ma trận vai trò → quyền

- Có 5 vai trò (DRAFT): `AUTHOR`, `EDITOR`, `APPROVER`, `MODERATOR`, `ADMIN`. Danh sách khớp CHECK `ck_user_roles_role`; test giữ hai bên khớp nhau.
- **Nguồn sự thật của ma trận: `config/rbac/rbac.json`** (cập nhật 09/11/2026 khi làm UI CMS). Backend nhúng file vào assembly lúc build (`PermissionMatrix`); CMS import cùng file để ẩn/hiện giao diện. Test C# và test CMS giữ file, hằng số và CHECK của 0313 khớp nhau.
- Code kiểm **quyền**, không kiểm tên vai trò:
  - Controller: `[RequirePermission(Permissions.X)]`, dựa trên cơ chế authorization chuẩn (`IAuthorizationRequirementData`).
  - Service: `User.RequirePermission(Permissions.X)`, đáp ứng yêu cầu "RBAC enforced in the domain layer".
  - Endpoint chỉ cần đăng nhập (đăng xuất, xem phiên): `[RequireStaffSession]`.
- Lỗi trả Problem đúng catalog: chưa đăng nhập → 401 `UNAUTHENTICATED`; thiếu quyền → 403 `FORBIDDEN`.
- **MFA:** quyền cao (approve, publish, takedown, moderation, quản lý user, xem audit) chỉ hiệu lực khi phiên đã qua MFA. Điều kiện MFA: `amr` có một trong `MfaAmrValues`, hoặc `acr` nằm trong `MfaAcrValues`. Phiên không MFA vẫn dùng được quyền thường. Thiếu MFA → 403 với title "MFA required".
- **Tách nhiệm vụ (SoD)** — không tự duyệt bài của mình — kiểm ở domain service (WBS-024), không nằm trong ma trận.

| Quyền | AUTHOR | EDITOR | APPROVER | MODERATOR | ADMIN | Cần MFA |
|---|:-:|:-:|:-:|:-:|:-:|:-:|
| content.read | ✓ | ✓ | ✓ | | | |
| content.write_own | ✓ | ✓ | | | | |
| content.write_any | | ✓ | | | | |
| content.review | | ✓ | ✓ | | | |
| content.approve | | | ✓ | | | ✓ |
| content.publish | | | ✓ | | | ✓ |
| content.takedown | | | ✓ | | | ✓ |
| media.upload | ✓ | ✓ | | | | |
| taxonomy.manage | | ✓ | | | | |
| moderation.act | | | | ✓ | | ✓ |
| users.read | | | | | ✓ | |
| users.manage | | | | | ✓ | ✓ |
| audit.read | | | | | ✓ | ✓ |

Muốn nhiều quyền thì cấp nhiều vai trò, ví dụ EDITOR + APPROVER. ADMIN cố ý **không** có quyền nội dung (tách quản trị khỏi biên tập).

### 6. Migration 0313 (DRAFT, chờ DBA)

- Thêm `user_identities`, `user_roles`, `user_role_history` (chỉ thêm dòng), `staff_sessions`, kèm trigger ghi lịch sử. Viết theo helper `tt_migration.*`; bản down từ chối chạy nếu đã có lịch sử vai trò.
- Giả định thêm **A-10**: schema 04 có `tt.users(id, display_name, status)`. Nếu schema 04 thật đã có bảng vai trò/phiên, DBA đối chiếu và sửa 0313 để dùng bảng có sẵn, tránh tồn tại hai bảng song song.

### 7. Quản trị tài khoản (WBS-115, bổ sung 09/11/2026)

- `/admin/users*` theo đúng hợp đồng v0.3. Xem cần `users.read`; đình chỉ, kích hoạt lại, đổi vai trò cần `users.manage` (có MFA). Service kiểm lại quyền bằng `User.RequirePermission`.
- Mỗi thay đổi chạy trong **một transaction có khóa dòng user** (`FOR UPDATE`), đặt `tt.actor_id` và `tt.reason`. Trigger tự ghi lịch sử: 0313 cho vai trò, 0314 cho trạng thái. Đổi trạng thái hay thu hồi vai trò mà thiếu lý do thì DB từ chối với `REASON_REQUIRED`, kể cả khi sửa thẳng bằng SQL.
- **Lý do** (`ActionReason`): thiếu hoặc rỗng → 422 `REASON_REQUIRED`; ngoài 5–2000 ký tự → 422 `VALIDATION_FAILED`.
- **Đình chỉ:** chỉ từ ACTIVE và thu hồi mọi phiên staff ngay (`revoked_reason = SUSPENDED`). **Kích hoạt lại:** chỉ từ SUSPENDED. Sai trạng thái → 409 `STATE_CONFLICT`. Cả hai bắt buộc `Idempotency-Key`.
- **Đổi vai trò:** bắt buộc `If-Match` theo `row_version` của user. Nếu có thay đổi thì tăng `row_version`, nên ETag cũ không dùng lại được.
- **Chống tự khóa:** admin không tự đình chỉ mình, không tự bỏ vai trò ADMIN của mình → 409 `STATE_CONFLICT`.
- `verificationStatus` của `AdminUser` để trống, vì xác minh thành viên thuộc WBS-059.

## Đề xuất bổ sung hợp đồng (chờ TL/PO duyệt, chưa sửa spec)

| # | Vấn đề | Đề xuất |
|---|---|---|
| A-1 | `XCsrfToken` ghi "final scheme depends on auth ADR" | Ghi rõ: token lấy từ cookie `__Host-tt_cms_csrf`, cấp lúc đăng nhập, gắn với phiên |
| A-2 | Chưa có enum vai trò (`RoleAssignment.roles`, `StaffSession.roles`) | Thêm enum `StaffRole` gồm 5 giá trị trên sau khi BA/PO duyệt ma trận |
| A-3 | Callback khi user chưa có vai trò | Ghi rõ 403 `FORBIDDEN` (đã có trong `x-error-codes`) và lý do |
| A-4 | Callback luôn do trình duyệt điều hướng tới, nên lỗi (chưa có vai trò, state sai, IdP từ chối) hiện ra dưới dạng JSON thô | **Đã sửa spec v0.3 và làm (09/11/2026):** khi `Accept` ưu tiên `text/html` → 302 về CMS `/login?error=&reason=[&returnTo=]`; client API vẫn nhận Problem. Spec vẫn là DRAFT, chờ TL/PO duyệt |

## Kiểm chứng (09/11/2026)

- `dotnet test`: **152/152 PASS** khi có DB (PGlite); 153/153 sau khi chuyển ma trận sang `config/rbac/rbac.json`.
- **UI CMS chạy thật trên trình duyệt** với `tools/dev-oidc`: đăng nhập (cookie `__Host-` được nhận trên `*.localhost`), tổng quan, menu theo quyền, màn "không có quyền" / "cần MFA", đăng nhập không MFA (banner + khóa), quay lại đúng `returnTo`, đăng xuất (204, gửi CSRF, cookie bị xóa).
- `AuthRbacTests` (không cần DB):
  - ma trận quyền, MFA, danh mục vai trò khớp migration;
  - 401/403 qua attribute và qua domain layer;
  - CSRF và thứ tự lỗi;
  - chặn open redirect của `returnTo`;
  - bề mặt admin không có trên www;
  - production thiếu IdP thì không khởi động.
- `StaffAuthFlowTests` (IdP giả trong bộ nhớ, PostgreSQL):
  - **luồng OIDC thật từ đầu tới cuối**: discovery, JWKS, PKCE S256, chữ ký id_token, nonce, at_hash;
  - thuộc tính cookie `__Host-` đúng; MFA theo `amr`;
  - user chưa có vai trò → 403 nhưng vẫn được tạo;
  - state bị sửa hoặc thiếu cookie correlation → 400 `OIDC_STATE_INVALID`; IdP từ chối → 403;
  - cookie phiên bị bỏ qua ở www;
  - đăng xuất cần CSRF và thu hồi phiên; đăng nhập lại xoay phiên;
  - hết hạn idle, đình chỉ user và thu hồi vai trò có hiệu lực ngay;
  - lịch sử vai trò chỉ thêm dòng và bắt buộc có lý do.
- Migration PGlite: 157/157 PASS, gồm 0313 up, chạy lại up, down.
- `Tinhte.DbMigrator grant-role`: chạy thử trên PGlite đúng (cấp lần đầu, gọi lại không đổi, vai trò sai bị từ chối).
- **Chưa kiểm:**
  - IdP thật, vì chưa chọn IdP;
  - giao diện CMS (nút đăng nhập, gửi `X-CSRF-Token`);
  - rate limit cho đăng xuất (`RATE_LIMITED`);
  - data protection key ring dùng chung giữa nhiều instance (state OIDC). Phải cấu hình khi deploy nhiều bản.

## Hệ quả

- **Được:**
  - đổi IdP chỉ cần đổi cấu hình;
  - thu hồi phiên/vai trò có hiệu lực tức thì;
  - module mới chỉ cần `[RequirePermission]`;
  - thứ tự lỗi và mã lỗi khớp catalog.
- **Mất:**
  - mỗi request admin tốn thêm một truy vấn phiên + vai trò (có index; sau này có thể cache vài giây);
  - phải tự quản lý bảng phiên và dọn phiên hết hạn (job, WBS-020).

## Phương án đã loại

- **Cookie auth của ASP.NET (ticket mã hóa trong cookie):** không thu hồi tức thì được, vai trò bị "đóng băng" trong cookie đến khi hết hạn.
- **Lấy vai trò từ claim của IdP:** phụ thuộc IdP chưa chọn, và FR-ID-004 yêu cầu admin của Tinhte cấp/thu hồi kèm lý do và audit.
- **Token CSRF ký HMAC (không lưu DB):** thêm một khóa bí mật phải quản lý, trong khi phiên đã nằm sẵn trong DB.
- **JWT bearer cho CMS:** token nằm trong JS dễ bị XSS lấy mất; hợp đồng đã chọn cookie host-only.

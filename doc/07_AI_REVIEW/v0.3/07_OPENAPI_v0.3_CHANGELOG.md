---
document_id: TINHTE-OPENAPI-CHANGELOG-v0.3
project: Tinhte.com.vn
version: 0.3.0-draft
status: DRAFT — CHỜ TECH LEAD / PO / LEGAL DUYỆT
based_on: 07_OPENAPI_TINHTE_EXPANDED_v0.2.yaml (không bị sửa)
created_at: 2026-11-09
---

# OpenAPI v0.3 DRAFT — Changelog

## 1. Trạng thái và cách dùng

- File `07_OPENAPI_TINHTE_v0.3_DRAFT.yaml` là **bản nháp đề xuất**: áp các đề xuất `AIR-xxx` trong `../07_AI_TECH_REVIEW_PROPOSALS.md` lên hợp đồng v0.2.
- **v0.2 giữ nguyên.** v0.3 chỉ thay thế v0.2 sau khi Tech Lead, PO và Legal ký duyệt.
- Không bật cờ nào: mọi cờ public vẫn mặc định **OFF** ở production, riêng `community.moderation` là công cụ nội bộ cho staff nên mặc định **ON**.
- Chưa hiện thực, chưa chạy mock hay sinh SDK; server URL vẫn là placeholder `.invalid`.

| Chỉ số | v0.2 | v0.3 |
|---|---:|---:|
| Paths | 93 | 100 |
| Operations | 106 | 116 |
| Schemas | 86 | 109 |
| Operations mới / thay đổi | — | 10 mới / 106 thay đổi |
| Phân bổ scope | P0 69 · P0* 16 · P0-ADS 3 · P1 14 · P2 4 | P0 75 · P0* 18 · P0-ADS 5 · P1 14 · P2 4 |

**Kiểm chứng đã chạy (2026-11-09):**

- `openapi-spec-validator`: **VALID** (OpenAPI 3.1).
- `contract-tests/test_static_contract.py` (34 test) trên toàn bộ bản v0.3 (spec + inventory + truy vết `trace/`): **34/34 PASS**. T30 chạy thử 17 trường hợp cho các quy tắc nhãn/metadata tài trợ, URL https, grant hết hạn, mã lỗi. Nếu dùng spec v0.3 với truy vết v0.2 thì các test truy vết T23/T24/T25/T33 FAIL; đó là việc của BA, đã xử lý trong `trace/`. Log: `../contract-tests/static_run_v0.3_2026-11-09.txt`.
- Không xóa thao tác nào của v0.2 (T02).
- `contract-tests/test_http_contract.py`: 16 test, tất cả SKIP vì chưa có server.

## 2. Đối chiếu đề xuất → thay đổi trong v0.3

Trạng thái: **ĐÃ ÁP DỤNG** (spec đã đổi, có test tĩnh) · **MỘT PHẦN** (phần hợp đồng đã đổi, còn việc ngoài spec) · **NGOÀI HỢP ĐỒNG** (thuộc DB/BA/PM/QA/ADR) · **CHƯA ÁP DỤNG** (chờ quyết định).

| Mã | Mức | Trạng thái | Thay đổi chính trong v0.3 | Kiểm chứng |
|---|---|---|---|---|
| AIR-001 | Blocker | ĐÃ ÁP DỤNG | Thêm registry `x-feature-flags` gồm 17 khóa, mỗi khóa có `legacyKeys` ánh xạ tên cũ ở DB/cấu hình/SRS. Đổi tên gate: `community.comments` → `community.video_comments`, `member.bookmark` → `member.bookmarks`. Thêm gate cho 4 endpoint P1. `SitePublicConfig` sinh từ registry. Thao tác ghi bị gate được kiểm cờ trong transaction | T11 |
| AIR-002 | Blocker | ĐÃ ÁP DỤNG | `x-conditional-gate` (`ads.sponsored_publication` khi revision SPONSORED) trên publish/schedule bài và video; thêm mã `SPONSORSHIP_LABEL_REQUIRED`, `SPONSORSHIP_EXPIRED`, `RIGHTS_INVALID` | T10b |
| AIR-004 | Blocker | ĐÃ ÁP DỤNG | `If-Match` bắt buộc trên 26 thao tác, tùy chọn trên unpublish (gỡ khẩn cấp); thêm `428 PRECONDITION_REQUIRED`; header `ETag` trên GET chi tiết và 200 sau ghi; `rowVersion` int64 ở mọi DTO | T04 T05 T18 |
| AIR-003 | High | ĐÃ ÁP DỤNG | 19 list nhận `cursor`/`limit` (cursor ký HMAC); list CMS có filter trạng thái/owner/assignee/thời gian/q/sort; analytics bắt buộc `from`/`to` | T13 |
| AIR-005 | High | MỘT PHẦN | `RollbackRequest.targetRevisionId`; `PublishRequest`/`ApproveRequest` cho cả bài và video; `ScheduleRequest.revisionId`; enum `ContentStatus` dùng chung; `workingRevisionId`. **Còn:** phụ lục bảng chuyển trạng thái (TL) | T28 |
| AIR-006 | High | ĐÃ ÁP DỤNG | Một schema `ModerationActionRequest` (APPROVE/REJECT/HIDE/REMOVE/RESTORE/LOCK/DISMISS/ESCALATE); `ModerationCase` có priority/assignee/SLA/history; thêm `POST …/assign`; có filter | T15 |
| AIR-007 | High | ĐÃ ÁP DỤNG (chờ PO/Legal xác nhận) | 5 endpoint moderation/pin dùng gate `community.moderation` (mặc định ON, chỉ Security tắt được); cấu hình placement của admin không bị gate bởi cờ public | — |
| AIR-008 | High | ĐÃ ÁP DỤNG | Vòng đời takedown: `TakedownRecord`, GET/PATCH chi tiết, thêm evidence (chỉ thêm, không xóa), actions; GET list trả đúng `TakedownRecordList` | T27 |
| AIR-009 | High | ĐÃ ÁP DỤNG | Chuẩn hóa ma trận status cho mọi thao tác; `x-error-catalog` (27 mã, mỗi mã một HTTP status); `Problem.code` thành enum; `x-error-codes` trên từng thao tác; header `Retry-After` | T06 T07 T08 T10 |
| AIR-010 | Medium | ĐÃ ÁP DỤNG | Bỏ 409/412/422 khỏi mọi GET; GET public không còn 401 | — |
| AIR-011 | High | MỘT PHẦN | `contentKind` bắt buộc và quy tắc `SponsoredLabelRule` (if/then) trên `ArticleSummary`, `Video`, `SearchHit`; `PublicAdSlot.rel = "sponsored noopener"`. **Còn:** quy ước nhãn trong sitemap/RSS (do SSR sinh, không qua REST API) | T14 |
| AIR-012 | High | MỘT PHẦN | Object `Sponsorship` (sponsorName, sponsorReference, evidenceRef, validFrom/To, approvedBy/At chỉ đọc) trên revision và DTO ghi; `SponsorshipRequiredRule`. **Còn:** migration DB (AIR-024) | — |
| AIR-013 | High | ĐÃ ÁP DỤNG | `AdPlacement` có width/height/altText/advertiserName/mediaAssetId, `targetUrl` bắt buộc `^https://`; thêm POST tạo và GET chi tiết; `PublicAdSlot` rút gọn; `placementCodes`; cờ OFF thì trả `200` danh sách rỗng | T29 |
| AIR-014 | High | NGOÀI HỢP ĐỒNG (đã soạn truy vết v0.3) | FR84/WBS v0.3 DRAFT trong `trace/`: WD-05…09, WD-15…18, khớp hai chiều | T23 T25 T33 PASS với `trace/` |
| AIR-015 | High | NGOÀI HỢP ĐỒNG (một phần) | Coverage của child đã quyết định (39 trong cha, 1 cần ước lượng, 2 P1 backlog); effort đề xuất chờ PM/Finance ký | T24 PASS với `trace/` |
| AIR-016 | High | ĐÃ ÁP DỤNG | OIDC start/callback trả `302`; callback nhận `code`/`state`/`error`; `returnTo` chỉ chấp nhận đường dẫn tương đối; state/nonce/PKCE và yêu cầu MFA ghi trong mô tả; logout trả `204` | — |
| AIR-017 | High | MỘT PHẦN | `POST /privacy/requests` công khai (`x-legal-pending`); `/me` chuyển sang P0* với gate `community.member`; FR-ID-002 chuyển sang `/admin/auth/session`. **Còn:** Legal chọn kênh | — |
| AIR-018 | High | MỘT PHẦN | `expiresAt` bắt buộc ở mọi playback grant; `MediaStatus` có rightsStatus/visibility/license/rightsExpiresAt; takedown có `propagation[]` để đo SLA. **Còn:** job rights-expiry và bảng propagation (BE/DB) | — |
| AIR-019 | Medium | NGOÀI HỢP ĐỒNG | ADR kiểm cờ trong transaction (TL). Spec ghi "enforced inside the write transaction" | — |
| AIR-020 | Medium | MỘT PHẦN | Tham số `sort=newest|top`; `Comment` có authorDisplayName/likeCount/likedByMe/pinned/replyCount. **Còn:** PO quyết định bình luận bài là P0* hay P1 (cờ đang ở dạng reserved) | — |
| AIR-021 | Medium | ĐÃ ÁP DỤNG | `AdminArticle`/`AdminVideo` mở rộng; `WorkQueueItem` gồm cả bài và video; `GET …/revisions/{revisionId}?compareTo=`; `GET /preview/articles/{token}` (noindex, no-store); `308` khi đổi slug; gán FR cho 20 thao tác | T16 T19 |
| AIR-022 | Medium | ĐÃ ÁP DỤNG | `Event` thêm contentType/sourceContentId/sessionId/consentState/schemaVersion; trả `202`; thêm `413` | — |
| AIR-023 | Medium | ĐÃ ÁP DỤNG | `Idempotency-Key` chỉ dùng cho POST không idempotent (thêm cho `POST /admin/articles`); bỏ khỏi PUT/PATCH/logout; quy định lưu khóa 24h và header `Idempotent-Replayed` | T12 |
| AIR-024 | Medium | NGOÀI HỢP ĐỒNG (đã soạn migration) | Migration DB 0299–0311 trong `db/`: up/down theo ledger, CHECK NOT VALID → VALIDATE, trigger chặn publish tài trợ và ghi bình luận khi cờ OFF | `db/tests` 140/140 |
| AIR-025 | Medium | NGOÀI HỢP ĐỒNG | QA viết TC. `x-error-codes` từng thao tác dùng làm oracle | — |
| AIR-026 | Low | CHƯA ÁP DỤNG | Chờ quyết định canonical watch URL/SEO | — |
| AIR-027 | Low | ĐÃ ÁP DỤNG | `commentsEnabled` có `default: false`; xóa component mồ côi; mọi thao tác có mô tả quyền/cờ/concurrency | T16 T17 |
| AIR-028 | Low | NGOÀI HỢP ĐỒNG | Sửa tài liệu và dependencies (BA/PM) | — |

## 3. Thay đổi phá vỡ tương thích so với v0.2 (FE/BE cần biết trước khi sinh mã)

1. **Khóa cờ đổi tên:** `community.comments` → `community.video_comments`; moderation/pin chuyển sang `community.moderation`; `member.bookmark` → `member.bookmarks`.
2. **`SitePublicConfig`:** `communityCommentsEnabled` → `videoCommentsEnabled`; thêm `memberLoginEnabled`, `flagsVersion`.
3. **If-Match bắt buộc** trên 26 thao tác ghi. Thiếu thì `428`, lệch phiên bản thì `412`.
4. **`Idempotency-Key`:** không còn trên PUT/PATCH/logout; bắt buộc thêm cho `POST /admin/articles`.
5. **PATCH/POST bài và video:** bỏ `sponsorName` phẳng, thay bằng object `sponsorship`. PATCH bài thêm `slug` và `changeReason`.
6. **Approve/publish video** bắt buộc body `revisionId`; **schedule** (bài và video) bắt buộc `revisionId`.
7. **Rollback:** body đổi thành `{targetRevisionId, reason}`.
8. **Moderation action:** enum mới; `ModerationCase` có thêm các trường bắt buộc `priority`, `createdAt`, `rowVersion`.
9. **Takedown:** GET list trả `TakedownRecordList`; POST trả `TakedownRecord` (bỏ `TakedownTicket`); `requesterType` bắt buộc.
10. **`GET /ads/placements`:** bắt buộc `placementCodes`; trả `PublicAdSlotList` thay vì `AdPlacementList`.
11. **OIDC:** start/callback trả `302` (bỏ `OidcStart`, `BooleanStatus` ở callback); logout trả `204`.
12. **`/analytics/events`:** trả `202`; `Event` bắt buộc `sessionId`, `consentState`.
13. **`/me`:** chuyển sang P0* với gate `community.member`.
14. **`contentKind` bắt buộc** trong `ArticleSummary`, `Video`, `SearchHit`; `sponsorLabel` bắt buộc khi SPONSORED.
15. **`AdminVideo`:** `rowVersion` string → int64; `rightsValid` → `rightsStatus`. `AdminArticle` bắt buộc thêm `title`, `contentKind`, `updatedAt`.
16. **`Comment`:** bắt buộc `likeCount`, `pinned`. `Category`/`Topic`/`AdminUser`/`MemberProfile` bắt buộc `rowVersion`.

## 4. Endpoint mới (10)

| Method | Path | operationId | Scope | Đề xuất |
|---|---|---|---|---|
| POST | `/admin/moderation/cases/{caseId}/assign` | `assignModerationCase` | P0* | AIR-006 |
| GET | `/admin/articles/{articleId}/revisions/{revisionId}` | `getArticleRevision` | P0 | AIR-021 |
| GET | `/preview/articles/{token}` | `getArticlePreviewByToken` | P0 | AIR-021 |
| GET | `/admin/takedown-requests/{takedownId}` | `getTakedownRequest` | P0 | AIR-008; AIR-018 |
| PATCH | `/admin/takedown-requests/{takedownId}` | `patchTakedownRequest` | P0 | AIR-008 |
| POST | `/admin/takedown-requests/{takedownId}/evidence` | `appendTakedownEvidence` | P0 | AIR-008 |
| POST | `/admin/takedown-requests/{takedownId}/actions` | `actOnTakedownRequest` | P0 | AIR-008; AIR-018 |
| POST | `/privacy/requests` | `createPublicPrivacyRequest` | P0 | AIR-017 |
| POST | `/admin/ad-placements` | `createAdPlacement` | P0-ADS | AIR-013 |
| GET | `/admin/ad-placements/{placementId}` | `getAdPlacement` | P0-ADS | AIR-013 |

## 5. Registry cờ tính năng (`x-feature-flags`)

| Khóa | Scope | Mặc định production | Người duyệt bật | Khóa cũ được ánh xạ | Trường public | Ghi chú |
|---|---|---|---|---|---|---|
| `community.video_comments` | P0* | OFF | Legal, PO | `video_comments_enabled`, `community.comments.enabled`, `community.comments` | `videoCommentsEnabled` | Public read/write of video comments, replies, likes and reports. |
| `community.article_comments` | P0* | OFF | Legal, PO | `article_comments_enabled` | — | Reserved. No endpoints in v0.3; PO decides P0* vs P1 (AIR-020). |
| `community.member` | P0* | OFF | Legal, PO | `community.member` | `memberLoginEnabled` | Member OIDC login, eligibility, verification and /me endpoints. |
| `community.moderation` | P0* | ON | Security | — | — | Staff moderation tooling. Stays ON while public community flags are OFF so existing content can be cleaned up (AIR-007). Only Security may turn it off. |
| `community.ugc` | P1 | OFF | Legal, PO | `community.ugc.enabled`, `user_upload_enabled`, `public_creator_publish_enabled` | — | Reserved: user uploads and creator publishing. No endpoints. |
| `community.appeals` | P1 | OFF | Legal, PO | `community.appeals` | — | Member appeals against moderation decisions. |
| `member.bookmarks` | P1 | OFF | PO | `member.bookmark` | — | Saved videos for members. |
| `ads.sponsored_publication` | P0-ADS | OFF | Legal, PO, Commercial | `sponsored_content_publication`, `ads.sponsored_publication.enabled` | `sponsoredContentEnabled` | Allows publishing revisions with contentKind=SPONSORED. Checked inside the publish transaction and again by scheduled publish jobs (AIR-002). |
| `ads.display_slots` | P0-ADS | OFF | Legal, PO, Commercial | `display_ad_slots`, `ads.display_slots` | `adSlotsEnabled` | Public static ad slots. When OFF, GET /ads/placements returns 200 with empty items (no layout gap). |
| `ads.shorts_instream` | P1 | OFF | Legal, PO, Commercial | `shorts_instream_ads` | `shortsInstreamAdsEnabled` | In-stream ads inside Shorts. OFF in MVP. |
| `ads.campaign_advanced` | P1 | OFF | PO, Commercial | `ads.campaign_advanced` | — | Campaign management and ad reports. |
| `discovery.series` | P1 | OFF | PO | — | — | Series pages (FR-NEWS-009). |
| `discovery.search_suggestions` | P1 | OFF | PO | — | — | Search suggestions (FR-DISC-006). |
| `cms.related_suggestions` | P1 | OFF | PO | — | — | Related content suggestions in CMS (FR-CMS-008). |
| `analytics.operations` | P1 | OFF | PO | — | — | Editorial/moderation operations metrics (FR-ANL-006). |
| `ai.assistance` | P2 | OFF | PO, Legal | `ai.assistance` | — | AI metadata/transcript assistance. |
| `ai.search` | P2 | OFF | PO, Legal | `ai.search` | — | AI search with sources. |

Bật bất kỳ cờ nào ở production phải có `approval_reference`, `approved_by`, `approved_at` và bản ghi audit; không được bật bằng biến môi trường không truy vết được.

## 6. Danh mục mã lỗi (`x-error-catalog`)

Thứ tự đánh giá: 400 cú pháp → 401 → 403 `CSRF_INVALID` → 403 `FEATURE_DISABLED` → 403 quyền/eligibility/SoD → 404 → 428/412 → 422 → 409 → 429 (gateway có thể trả 429 sớm hơn).

| Mã | HTTP | Ý nghĩa |
|---|---:|---|
| `BAD_REQUEST` | 400 | Malformed request, unknown or invalid parameter. |
| `INVALID_CURSOR` | 400 | Cursor is not a valid signed cursor for this list. |
| `OIDC_STATE_INVALID` | 400 | OIDC state/nonce/PKCE verification failed. |
| `UNAUTHENTICATED` | 401 | No valid session. |
| `CSRF_INVALID` | 403 | Missing or invalid X-CSRF-Token on a cookie-authenticated mutation. |
| `FEATURE_DISABLED` | 403 | Required feature flag is OFF (checked server-side; flag flips are re-checked inside write transactions). |
| `FORBIDDEN` | 403 | Authenticated but the role/permission does not allow the action. |
| `ELIGIBILITY_REQUIRED` | 403 | Member does not satisfy the approved eligibility/verification policy. |
| `SOD_VIOLATION` | 403 | Separation of duties: actor cannot approve/publish their own revision. |
| `RIGHTS_EXPIRED` | 403 | Media rights expired or revoked; no playback grant is issued. |
| `NOT_FOUND` | 404 | Resource does not exist or is not visible to the caller. |
| `STATE_CONFLICT` | 409 | Transition not allowed from the current state. |
| `IDEMPOTENCY_KEY_REUSED` | 409 | Same Idempotency-Key used with a different payload. |
| `CONTENT_LOCKED` | 409 | Target content is locked, removed or no longer accepts interactions. |
| `CONTENT_WITHDRAWN` | 410 | Content was withdrawn/taken down. |
| `REVISION_CONFLICT` | 412 | If-Match does not match the current ETag. |
| `PAYLOAD_TOO_LARGE` | 413 | Request or upload exceeds the allowed size. |
| `VALIDATION_FAILED` | 422 | Domain validation failed; see errors map. |
| `REASON_REQUIRED` | 422 | A reason is mandatory for this action. |
| `SPONSORSHIP_LABEL_REQUIRED` | 422 | contentKind=SPONSORED without complete sponsorship metadata. |
| `SPONSORSHIP_EXPIRED` | 422 | Sponsorship validity window has ended or not started. |
| `RIGHTS_INVALID` | 422 | Source/licence/rights metadata missing or expired at publish time. |
| `MEDIA_NOT_READY` | 422 | Media is not READY (still processing, failed, quarantined). |
| `UNSAFE_URL` | 422 | URL is not https or is on a blocked list. |
| `PRECONDITION_REQUIRED` | 428 | If-Match header is required for this operation. |
| `RATE_LIMITED` | 429 | Rate limit exceeded; see Retry-After. |
| `DEPENDENCY_UNAVAILABLE` | 503 | A downstream dependency (media/CDN/IdP) is unavailable. |

## 7. Việc còn lại ngoài hợp đồng

| Owner | Việc | Liên quan |
|---|---|---|
| BE (DBA) | **Đã soạn ở `db/` (DRAFT, 140/140 test PASS trên PostgreSQL 18.6/PGlite với stub schema 04).** Còn: đối chiếu giả định A-01…A-09 với schema 04 thật; chạy lại trên PostgreSQL bản production; test đồng thời nhiều kết nối; backfill trước 0310 (xem `db/README.md`) | AIR-001/012/013/018/024 |
| TL | ADR kiểm cờ trong transaction; phụ lục bảng chuyển trạng thái; quản lý khóa HMAC cho cursor và preview token | AIR-019, AIR-005, AIR-003 |
| BA | **Đã soạn `trace/` (DRAFT, 34/34 test tĩnh PASS khi dùng toàn bộ file v0.3).** Còn: duyệt bản truy vết; đưa cột "gói phát hành" vào SRS v0.3 (không đổi Priority gốc) | AIR-014 |
| PM + Finance | Ký bảng tái ước lượng | AIR-015 |
| Legal | Kênh yêu cầu dữ liệu cá nhân; câu chữ nhãn tài trợ; xác nhận `community.moderation` ON khi cờ public OFF | AIR-017, AIR-011, AIR-007 |
| PO | Bình luận bài P0* hay P1; canonical watch URL | AIR-020, AIR-026 |
| FE + SEO | Quy ước nhãn trong sitemap/RSS/structured data | AIR-011 |
| QA | TC cho 84 FR dựa trên `x-error-codes`; chạy `test_http_contract.py` khi có staging | AIR-025 |

## 8. Kiểm lại

```bash
cd doc/07_AI_REVIEW/contract-tests
pip install pyyaml openapi-spec-validator
TT_SPEC_FILE=../v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml TT_INVENTORY_FILE=../v0.3/07_API_ENDPOINT_INVENTORY_v0.3_DRAFT.csv python -m unittest -v test_static_contract.py
```

Inventory v0.3 (`07_API_ENDPOINT_INVENTORY_v0.3_DRAFT.csv`) có thêm các cột `ConditionalGate`, `Concurrency`, `Idempotency`, `ErrorCodes`, `ChangeV03`, `ReviewRefs` cho từng thao tác.

## 9. Cập nhật Sprint 0 — đa subdomain (ADR-0001, 2026-11-09)

- `servers`: đổi thành `https://{siteHost}/api/v1` (API cùng origin theo từng site). Danh sách `siteHost` **sinh từ** `config/sites/sites.json`, gồm các site có `apiSurfaces`.
- Bề mặt `admin` (`/admin/*`, `/auth/staff/*`) chỉ phục vụ trên site `cms`; các thao tác công khai chỉ phục vụ trên site `public-web`.
- Cookie: `__Host-tt_cms_session`, `__Host-tt_member_session`, chỉ thuộc host, không bao giờ đặt `Domain` cha. **Thay đổi phá vỡ** so với tên cookie trước đó.
- Không đổi đường dẫn, schema hay mã lỗi. Kiểm chứng lại: 34/34 test tĩnh PASS; test backend `ContractConsistencyTests` PASS.

## 10. Cập nhật Sprint 0 — lỗi đăng nhập staff hiển thị trong CMS (ADR-0004 A-4, 2026-11-09)

- `GET /auth/staff/oidc/callback`: trình duyệt tự điều hướng tới URL này, nên khi `Accept` ưu tiên `text/html`, lỗi trả về **302 tới trang CMS** `/login?error={code}&reason={reason}[&returnTo={path}]` thay vì body Problem. Client khác (`Accept: application/json` hoặc `application/problem+json`) vẫn nhận Problem 400/403 như trước.
- Mã lỗi giữ nguyên theo catalog (`OIDC_STATE_INVALID`, `BAD_REQUEST`, `FORBIDDEN`). Thêm `reason` để CMS hiển thị đúng thông điệp: `state_invalid`, `idp_error`, `denied`, `no_role`, `inactive`. Lỗi không tạo phiên, không đặt cookie phiên.
- `returnTo` chỉ được gắn khi đã xác minh state (biết `returnTo` gốc) và theo cùng mẫu của tham số `ReturnTo`.
- Mô tả máy đọc được ở extension `x-browser-error-redirect` của operation. Không đổi đường dẫn, schema, mã lỗi hay status. Kiểm lại: 34/34 test tĩnh PASS.
- (bổ sung) `GET /auth/staff/oidc/start` và `/callback` thêm **503 `DEPENDENCY_UNAVAILABLE`** khi chưa cấu hình đăng nhập staff, IdP không truy cập được hoặc DB phiên không dùng được (trước đó rơi thành 500). Trình duyệt nhận 302 về `/login?error=DEPENDENCY_UNAVAILABLE&reason=unavailable`. Inventory cập nhật status/mã lỗi. Test tĩnh 34/34 PASS.

## 11. Cập nhật Sprint 0 — revision bài viết (WBS-022, ADR-0005, 2026-11-09)

- `ArticleRevision` (dùng cho `getAdminArticlesArticleidRevisions`, `getArticleRevision` / `RevisionDetail`): **thêm trường tùy chọn** `summary`, `categoryId`, `updatedAt`, `publishedAt`, `publishedBy`; mô tả giá trị của `status` (DRAFT, IN_REVIEW, CHANGES_REQUESTED, REJECTED, APPROVED, PUBLISHED, SUPERSEDED). Lý do: sapo và chuyên mục nằm trên revision (bản công khai không đổi trước khi publish), CMS cần chúng để mở bản nháp. Chỉ bổ sung, không phá vỡ.
- Không đổi đường dẫn, operationId, mã lỗi hay status. Hành vi đã hiện thực (ghi rõ để TL/PO duyệt):
  - `patchAdminArticlesArticleid` trả 409 `STATE_CONFLICT` khi bài `IN_REVIEW`/`APPROVED`/`SCHEDULED`/`RETRACTED`; `sourceId`/`licenseId` trả 422 cho tới khi có taxonomy (WBS-023).
  - `publishArticle` chỉ nhận revision `APPROVED`; quyền không `CLEARED` hoặc hết hạn → 422 `RIGHTS_INVALID`; revision không thuộc bài → 422 `VALIDATION_FAILED`.
  - `getPublicArticle`: ETag = `W/"<publicRevisionId>"`; bài từng public nhưng `UNPUBLISHED`/`RETRACTED`/hết quyền → 410 `CONTENT_WITHDRAWN`; slug cũ → 308 với `Location` tương đối.
- Đề xuất (chưa sửa spec, chờ TL/PO): `summary` và `bodyBlocks` chưa có giới hạn độ dài/số phần tử; `sponsorLabel` cần câu chữ Legal duyệt (đang đọc từ cấu hình `News:SponsoredLabel`, thiếu thì không phục vụ bài tài trợ).
- Kiểm lại: 34/34 test tĩnh PASS.

## 12. Cập nhật Sprint 0 — video + media trong CMS (WBS-046, ADR-0006, 2026-11-09)

- `MediaStatus` (dùng cho `getAdminMedia`, `listAdminMediaLibrary`): **thêm trường tùy chọn** `fileName`, `contentType`, `sizeBytes`, `createdBy`, `updatedAt`. Lý do: thư viện media (FR-CMS-009) cần hiển thị tệp nào, dung lượng, ai tải lên. Chỉ bổ sung, không phá vỡ.
- `AdminVideo`: **thêm trường tùy chọn** `summary` (sapo của working revision, không có bản nháp thì của public revision). Lý do: màn sửa video cần giá trị hiện tại để sửa. Chỉ bổ sung.
- `createUploadIntent`, `finalizeUpload`: **thêm 503 `DEPENDENCY_UNAVAILABLE`** khi kho lưu trữ media chưa cấu hình/không truy cập được (mặc định ngoài Development cho tới khi chọn vendor — WBS-035). Inventory cập nhật status/mã lỗi.
- Không đổi đường dẫn, operationId hay schema request. Hành vi đã hiện thực (ghi rõ để TL/PO duyệt):
  - `createUploadIntent`: MIME ngoài danh sách DRAFT (MP4/MOV/WebM, JPEG/PNG/WebP, WebVTT) → 422; vượt giới hạn theo loại (DRAFT: video 2 GiB, ảnh 20 MiB, phụ đề 1 MiB) → 413 `PAYLOAD_TOO_LARGE`; `fileName` bỏ phần thư mục; intent hạn 15 phút; `Location` → `getAdminMedia`.
  - `finalizeUpload`: chưa có tệp → 422 `MEDIA_NOT_READY`; sai `uploadToken` hoặc tệp khác `sizeBytes`/`checksumSha256` → 422 `VALIDATION_FAILED` (tệp sai bị xóa, tải lại lên cùng URL); intent hết hạn / media đã có kết quả → 409 `STATE_CONFLICT`; gọi lại khi đang trong pipeline → 202 với trạng thái hiện tại.
  - `errorCode` của `MediaStatus`: xem mục bổ sung bên dưới.
  - `postAdminVideos`/`patchAdminVideosVideoid`: `rightsSource` → 422 cho tới khi có taxonomy nguồn/giấy phép (WBS-023); media phải là VIDEO, chưa `FAILED/QUARANTINED/REJECTED/RESTRICTED`, quyền không `RESTRICTED/EXPIRED/REVOKED` (422), chưa gắn video khác (409); đổi media của video đã từng public → 409 (WBS-040); PATCH khi video `IN_REVIEW`/`APPROVED`/`SCHEDULED`/`RETRACTED` → 409 như bài viết.
- (bổ sung) `createUploadIntent`: **429 `RATE_LIMITED` + `Retry-After`** đã hiện thực (đã có trong spec) — hạn mức theo người tải: số intent trong cửa sổ 10 phút và tổng dung lượng intent chưa finalize (DRAFT, chờ PO). Intent hết hạn quá 1 giờ được dọn: `status = FAILED`, `errorCode = UPLOAD_EXPIRED`. `MediaStatus.errorCode` **thêm mô tả** liệt kê giá trị hiện có (`UNSUPPORTED_FORMAT`, `OBJECT_MISSING`, `UPLOAD_EXPIRED`) và quy ước mã lạ = lỗi chung. Không đổi kiểu/status. Kiểm lại 34/34 PASS.
- **Ngoài hợp đồng, chỉ dev:** `PUT /api/v1/admin/media/{mediaId}/dev-upload?expires=&signature=` là đích `uploadUrl` của kho Local (ADR-0006); production client PUT thẳng lên URL ký sẵn của kho. Không thêm vào spec.
- Đề xuất (chưa sửa spec, chờ TL/PO): `UpdateVideoRequest.mediaId` không nullable → chưa gỡ được media khỏi video; `AdminVideo` chưa có `slug`/`createdAt`; chưa có endpoint xem trước media private trong CMS (cần grant qua site `media`).
- Kiểm lại: 34/34 test tĩnh PASS.

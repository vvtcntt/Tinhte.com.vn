---
document_id: TINHTE-AI-TECH-REVIEW-07
project: Tinhte.com.vn
reviewed_package: Remediation 07 v0.2 (2026-11-07)
version: 0.1
status: OPEN — PROPOSALS ONLY, KHÔNG THAY BASELINE
created_at: 2026-11-09
reviewer: Claude (AI review theo 07_CODEX_CLAUDE_REVIEW_PROMPT_v0.2.md)
---

# 07 — Đề xuất kỹ thuật từ AI review (Remediation v0.2)

> **Phạm vi và nguyên tắc.** Tài liệu chỉ **đề xuất**: không sửa file nguồn 07 v0.2 hay baseline v0.1, không nâng P1/P2 lên P0, không bật cờ public, không coi giấy phép là đã có. Mọi đề xuất ở trạng thái **OPEN**, chờ owner xử lý và người duyệt ký.

## 1. Phương pháp và giới hạn

| Hạng mục | Nội dung |
|---|---|
| Đã đọc | Toàn bộ 17 file trong `doc/` (gói 07 v0.2) |
| **Không có để đối chiếu** | Tài liệu nguồn **01 BRD, 02 SRS, 03 UX, 04 ARCH/API + `04_DATABASE_SCHEMA_REFERENCE_v0.1.sql`, 05 WBS Excel, 06 Cross-review**. Mọi kết luận cần các tài liệu này được đánh dấu **TBD** |
| Công cụ đã chạy | `openapi-spec-validator` (OpenAPI 3.1): **VALID**; bộ test tĩnh `contract-tests/test_static_contract.py`: **30 test, 8 PASS, 22 FAIL** (log: `contract-tests/static_run_2026-11-09.txt`); bộ HTTP `contract-tests/test_http_contract.py`: **15 test, 15 SKIP** vì chưa có server |
| Chưa chạy | Mock server, SDK sinh mã, HTTP contract test thật, migration PostgreSQL, test tải, test bảo mật, E2E trình duyệt |

**Mức bằng chứng dùng trong tài liệu:**

- `TOOL`: chứng minh bằng script/test, có mã test `Txx`, lặp lại được.
- `ĐỌC`: đối chiếu trực tiếp giữa các file, có thể kiểm tra lại từng dòng.
- `SUY LUẬN`: đánh giá kiến trúc hoặc nghiệp vụ, cần owner xác nhận.
- `TBD`: cần tài liệu 01–06 hoặc quyết định PO/Legal.

**Thang mức độ:**

- **Blocker**: phải xử lý trước khi đóng băng hợp đồng v0.3, sinh mã, hoặc mở phạm vi liên quan.
- **High**: sai hoặc thiếu hành vi của FR hay bất biến P0/P0*/P0-ADS; phải sửa trước khi code module đó.
- **Medium**: không nhất quán hoặc ảnh hưởng chất lượng; sửa trước sign-off.
- **Low**: tài liệu, thẩm mỹ.

## 2. Danh mục đề xuất

| Mã | Mức độ | Chủ đề | Bằng chứng | Owner | Người duyệt |
|---|---|---|---|---|---|
| AIR-001 | **Blocker** | Registry cờ tính năng thống nhất | TOOL T11 | TL | PO + Legal |
| AIR-002 | **Blocker** (P0-ADS) | Publish không kiểm tra cờ/nhãn sponsored | TOOL T10b | BE | TL + Legal |
| AIR-004 | **Blocker** | Optimistic concurrency thiếu If-Match/ETag | TOOL T04 T05 T18 | BE | TL |
| AIR-003 | High | 14 list thiếu cursor/limit/filter | TOOL T13 | BE | TL |
| AIR-005 | High | Bất biến revision: rollback/publish video/enum trạng thái | TOOL T28 | BE | TL |
| AIR-006 | High | Hợp đồng moderation lệch enum, thiếu assign/priority | TOOL T15 | BE | TL + PO |
| AIR-007 | High | Kill switch cộng đồng khóa luôn moderation | SUY LUẬN | TL | PO + Legal |
| AIR-008 | High | Takedown request sai schema, thiếu vòng đời | TOOL T27 | BE | Legal + TL |
| AIR-009 | High | Ma trận 401/404/429/Retry-After và danh mục mã lỗi | TOOL T06–T08 T10 | BE | TL + QA |
| AIR-011 | High | Nhãn tài trợ chưa lan tới mọi kênh | TOOL T14 | BE + FE | Legal + PO |
| AIR-012 | High | Metadata tài trợ không đủ theo quyết định ads | ĐỌC | BE | Legal + Commercial |
| AIR-013 | High | Hợp đồng ad placement thiếu tạo mới/kích thước/an toàn URL | TOOL T29 | BE | TL + Commercial |
| AIR-014 | High | Truy vết ads/scope lệch giữa FR84, WBS và quyết định | TOOL T23 T25 | BA | PO + PM |
| AIR-015 | High | Đếm trùng/thiếu effort (WBS-085 so với 131–135; child ngoài trace cha) | TOOL T24 + SUY LUẬN | PM | PM + Finance + TL |
| AIR-016 | High | Luồng OIDC callback/PKCE/state | ĐỌC | BE + SEC | TL + SEC |
| AIR-017 | High | Kênh yêu cầu dữ liệu cá nhân phụ thuộc đăng nhập P0* | SUY LUẬN | BA | Legal + PO |
| AIR-018 | High | Hết hạn quyền và lan truyền takedown | ĐỌC | BE + MEDIA | TL + Legal |
| AIR-010 | Medium | Danh sách status mẫu trên 44 GET | TOOL | BE | TL |
| AIR-019 | Medium | ADR: cờ tắt giữa request (FR-COM-009) | SUY LUẬN | TL | TL + SEC |
| AIR-020 | Medium | Hợp đồng comment thiếu sort/like/pinned | ĐỌC | BE | PO + TL |
| AIR-021 | Medium | DTO CMS, preview, redirect slug, 20 thao tác không có FR | TOOL T19 | BE + BA | TL + PO |
| AIR-022 | Medium | Hợp đồng sự kiện analytics (attribution/consent/abuse) | ĐỌC | DATA | TL + Legal |
| AIR-023 | Medium | Chính sách Idempotency-Key | TOOL T12 | BE | TL |
| AIR-024 | Medium | Gia cố SQL addendum P0-ADS | ĐỌC | BE (DBA) | TL + DBA |
| AIR-025 | Medium | Chất lượng test design và mã TC | ĐỌC | QA | QA Lead + BA |
| AIR-026 | Low | Watch URL video và video sitemap | SUY LUẬN | FE | TL + SEO |
| AIR-027 | Low | Lỗi nhỏ trong schema | TOOL T16 T17 | BE | TL |
| AIR-028 | Low | Lỗi định dạng tài liệu/dependencies | ĐỌC | BA + PM | PM |

**Tổng: 3 Blocker · 14 High · 8 Medium · 3 Low = 28 đề xuất.** Chi tiết theo từng endpoint ở `07_API_CONTRACT_ISSUES.csv` (105 dòng `ACI-xxx`); điều chỉnh effort ở `07_WBS_REESTIMATION_DIFF.csv` (15 dòng `WD-xx`).

---

## 3. Chi tiết đề xuất

### AIR-001 — Registry cờ tính năng thống nhất · **Blocker**

| Trường | Nội dung |
|---|---|
| Nguồn | `07_PARALLEL_WORKSTREAM_RELEASE_GATES` §3; `07_SRS_WBS_TRACEABILITY_ADDENDUM` §A.4; `07_OPENAPI` `x-feature-gate`, `SitePublicConfig`; `07_DB_ADS_LITE_REFERENCE.sql` |
| Bằng chứng | **TOOL T11**: 8/8 khóa `x-feature-gate` không có trong registry gộp từ SQL và cấu hình production |
| Phân tích | Cùng một cờ đang có tới 5 kiểu tên. Ví dụ cờ bình luận: `video_comments_enabled` (cấu hình production), `community.comments.enabled` (SRS §A), `community.comments` (API gate), `communityCommentsEnabled` (DTO). Cờ quảng cáo: `display_ad_slots` (SQL/production) và `ads.display_slots` (API). Ngoài ra, `article_comments_enabled`, `user_upload_enabled`, `public_creator_publish_enabled` có cờ nhưng không có API, còn `community.member`, `member.bookmark`, `ai.*` có trong API mà không có ở DB/cấu hình. Cờ chính là **kill switch pháp lý**: nếu tên lệch, tắt cờ trong DB có thể không chặn API. |
| Phương án | (1) Tạo `docs/feature-flags.md` làm registry: `key`, `scope`, mặc định production, owner duyệt, endpoint bị gate, khóa DTO public. (2) Dùng một quy ước, đề xuất dạng `domain.capability` (`community.video_comments`, `ads.sponsored_publication`, `ads.display_slots`, `ads.shorts_instream`). (3) `x-feature-gate` chỉ nhận khóa có trong registry; `SitePublicConfig` sinh từ registry. (4) Bảng `feature_gates` có `approval_reference`, `approved_by`, `approved_at` và audit đúng như §3 tài liệu song hành (cần đối chiếu schema 04, **TBD**). |
| Liên quan | FR-COM-009, FR-ADS-004 · WBS-135, WBS-020, WBS-059 · API: 33 thao tác có gate + `GET /site/config` · DB: `tt.feature_gates` |
| Effort | Khoảng 2 PD (TL 0.5, BE 1, DEVOPS 0.5), nằm trong WBS-135/WBS-020; không tạo WBS mới |
| Test chứng minh | T11 PASS; test tích hợp: tắt cờ trong DB thì API trả `403 FEATURE_DISABLED` trong vòng TTL cache đã duyệt |
| Owner / Duyệt / Trạng thái | TL / PO + Legal / **OPEN** |

### AIR-002 — Publish không kiểm tra cờ và nhãn sponsored · **Blocker (phạm vi P0-ADS)**

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI` publishArticle, `POST /admin/videos/{videoId}/publish`; `07_API_IMPLEMENTATION_GUIDE` §5; `07_ADS_MVP_DECISION` |
| Bằng chứng | **TOOL T10b**: cả hai thao tác publish không nhắc tới sponsored; không thao tác nào mang gate `sponsored_content_publication` |
| Phân tích | Guide quy định "reject publish khi thiếu sponsor/approval" và cờ `sponsored_content_publication=false` ở production, nhưng hợp đồng publish không có điều kiện này. Mã `SPONSORSHIP_LABEL_REQUIRED` chỉ là ví dụ trong mô tả `Problem.code`. Nếu thiếu, nội dung tài trợ có thể lên public không nhãn, rủi ro trực tiếp với yêu cầu nhận diện quảng cáo (Luật Quảng cáo sửa đổi, NĐ 342/2025, Legal cần xác nhận). |
| Phương án | Publish bài/video: khi revision có `contentKind=SPONSORED` thì kiểm tra trong cùng transaction đổi `publicRevisionId`: (a) cờ `ads.sponsored_publication` ON, nếu không thì `403 FEATURE_DISABLED`; (b) đủ metadata tài trợ (AIR-012), nếu không thì `422 SPONSORSHIP_LABEL_REQUIRED`; (c) hiệu lực tài trợ còn hạn, nếu không thì `422 SPONSORSHIP_EXPIRED`. Thêm `x-conditional-gate` và mô tả vào hai thao tác, đồng thời áp dụng cho `schedule` (job publish theo lịch phải kiểm tra lại lúc chạy). |
| Liên quan | FR-NEWS-008, FR-ADS-001 · WBS-132, WBS-125 · API: publish/schedule bài và video · DB: `article_revisions`, `video_revisions` |
| Effort | Nằm trong WBS-132 (đã tăng ở WD-03) |
| Test chứng minh | T10b PASS; `test_sponsored_publish_blocked_when_flag_off`; `test_sponsored_without_sponsor_name_rejected`; job lịch publish khi cờ OFF thì bản ghi giữ `SCHEDULED` và có cảnh báo |
| Owner / Duyệt / Trạng thái | BE / TL + Legal / **OPEN** |

### AIR-003 — 14 list thiếu cursor/limit/filter · High

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI` |
| Bằng chứng | **TOOL T13**: 13 GET trả `nextCursor` nhưng không nhận `cursor`/`limit`, gồm `/admin/articles`, `/admin/videos`, `/admin/media`, `/admin/users`, `/admin/work-queue`, `/admin/moderation/cases`, `/admin/takedown-requests`, `/categories/{slug}/articles`, `/topics/{slug}/articles`, các related, `/series/{slug}`; thêm `GET /admin/analytics/overview` không có `from`/`to` |
| Phân tích | Không có tham số thì FE không phân trang được, và FR-CMS-006 (lọc theo trạng thái, owner, người duyệt, thời gian), FR-MOD-001, FR-ANL-005 không thể triển khai theo hợp đồng. Sinh mã bây giờ sẽ ra client sai. |
| Phương án | Thêm `Cursor` + `Limit` cho mọi list. List CMS thêm `status`, `ownerId`, `assigneeId`, `updatedFrom`, `updatedTo`, `q`, `sort`. Analytics thêm `from`, `to` (bắt buộc), `contentType`, `source`, `timezone`. Chốt cursor có chữ ký (HMAC) và quy tắc ổn định thứ tự (`publishedAt desc, id desc`). |
| Liên quan | FR-CMS-006, FR-DISC-002, FR-MOD-001, FR-ANL-005, FR-COM-005 · WBS-014, WBS-031, WBS-041 · 14 dòng ACI |
| Effort | Nằm trong WBS-014/WBS-041 (quy ước chung); không tăng |
| Test chứng minh | T13 PASS; contract: cursor giả mạo trả `400`; trang kế tiếp không trùng hay sót item khi có bản ghi mới chèn vào |
| Owner / Duyệt / Trạng thái | BE / TL / **OPEN** |

### AIR-004 — Optimistic concurrency thiếu If-Match/ETag · **Blocker**

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI`; FR-CMS-007; guide §3 (`412 stale rowVersion`) |
| Bằng chứng | **TOOL T04**: 9 PATCH/PUT thiếu `If-Match`, gồm cả `PATCH /admin/articles/{articleId}` (summary ghi "optimistic concurrency") và `PATCH /admin/videos/{videoId}`. **T05**: GET admin bài/video không trả `ETag`. **T18**: `rowVersion` là integer ở AdminArticle nhưng là string ở AdminVideo |
| Phân tích | Chỉ 4 thao tác có `If-Match` (submit/approve/publish bài, moderation action). Thao tác ghi quan trọng nhất (sửa nháp) lại không có, nên FR-CMS-007 (chống ghi đè khi hai người cùng sửa) không đạt ở mức hợp đồng; response liệt kê `412` nhưng client không có cách gửi phiên bản. |
| Phương án | (1) `If-Match` bắt buộc cho mọi PATCH/PUT tài nguyên có phiên bản, gồm bài, video, subtitles, related-articles, categories, topics, roles, ad-placements, profile. (2) Thiếu header thì `428 Precondition Required`; sai phiên bản thì `412` với `code=REVISION_CONFLICT`. (3) GET chi tiết trả `ETag: W/"<rowVersion>"`; mọi 200 sau khi ghi trả ETag mới. (4) Thống nhất `rowVersion` là int64. (5) Các PUT dạng bật/tắt (like, bookmark, pin) không cần If-Match. |
| Liên quan | FR-CMS-007, FR-CMS-004, FR-NEWS-005 · WBS-026, WBS-014 · 11 dòng ACI |
| Effort | Nằm trong WBS-026; không tăng |
| Test chứng minh | T04, T05, T18 PASS; `test_patch_article_with_stale_if_match_returns_412`; test hai phiên sửa song song: phiên thứ hai nhận 412 và không mất nội dung phiên đầu |
| Owner / Duyệt / Trạng thái | BE / TL / **OPEN** |

### AIR-005 — Bất biến revision: rollback, publish video, enum trạng thái · High

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI` rollback, publish video, `AdminVideo`, `AdminArticle`; `07_PARALLEL…` §5 (`working_revision/public_revision`) |
| Bằng chứng | **TOOL T28**: rollback chỉ có `reason`, không có revision đích; publish video không có body `revisionId` và không có `If-Match` (khác publish bài). **ĐỌC**: `AdminVideo.status` thiếu `REJECTED`, `UNPUBLISHED` dù có endpoint reject/unpublish; cả hai DTO thiếu `workingRevisionId` |
| Phân tích | Bất biến "bài/video đã publish là revision bất biến" cần mọi thao tác đổi `publicRevisionId` chỉ rõ revision. Hiện tại server phải đoán ("revision mới nhất?"), dễ publish nhầm bản đang sửa dở. Enum trạng thái bài và video không khớp sẽ làm state machine dùng chung (WBS-024) bị tách nhánh. |
| Phương án | Rollback nhận `{targetRevisionId, reason}` và tạo working revision mới, **không** đổi `publicRevisionId`. Publish video nhận `{revisionId}` + `If-Match`, lỗi `422 MEDIA_NOT_READY / RIGHTS_EXPIRED`. Thêm `workingRevisionId` vào DTO admin. Đồng bộ enum: `DRAFT, IN_REVIEW, CHANGES_REQUESTED, REJECTED, APPROVED, SCHEDULED, PUBLISHED, UNPUBLISHED, RETRACTED`. Viết bảng chuyển trạng thái (state × action × role → state/lỗi) làm phụ lục hợp đồng. |
| Liên quan | FR-CMS-004, FR-NEWS-004/005, FR-VID-008/010 · WBS-022, WBS-024, WBS-040 · 3 dòng ACI |
| Effort | Nằm trong WBS-022/040 |
| Test chứng minh | T28 PASS; domain test: sửa nháp sau publish thì `publicRevisionId` không đổi; rollback tạo revision mới; publish revision không phải APPROVED trả `409` |
| Owner / Duyệt / Trạng thái | BE / TL / **OPEN** |

### AIR-006 — Hợp đồng moderation lệch enum, thiếu assign/priority · High (P0*)

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI` `addModerationAction`, `ModerationCase`, `ModerationActionRequest` |
| Bằng chứng | **TOOL T15**: enum inline `HIDE/REMOVE/RESTORE/DISMISS/ESCALATE` khác schema `ModerationActionRequest` (`APPROVE/REJECT/HIDE/REMOVE/RESTORE/LOCK`, không được dùng; **T16**); thiếu `APPROVE`, `LOCK` mà FR-COM-007 yêu cầu |
| Phân tích | FR-COM-007 cần "ẩn/gỡ/phê duyệt/khôi phục/khóa". FR-MOD-001 cần "trạng thái, loại vi phạm, mức ưu tiên, assignee, lịch sử", nhưng `ModerationCase` chỉ có `reasonCode` và `status` dạng chuỗi tự do. Không có endpoint giao việc, cũng không có filter. |
| Phương án | Dùng một schema `ModerationActionRequest` với enum hợp nhất theo policy đã duyệt. `ModerationCase` thêm `priority`, `assigneeId`, `slaDueAt`, `violationType` (enum), `history[]`, và đổi `status` thành enum. Thêm `POST /admin/moderation/cases/{caseId}/assign`; list có filter `status/priority/assignee`. |
| Liên quan | FR-COM-007, FR-MOD-001/002 · WBS-062, WBS-065, WBS-107, WBS-117 · 2 dòng ACI |
| Effort | Nằm trong WBS-062/065 |
| Test chứng minh | T15 PASS; moderator ngoài phạm vi trả `403`; mọi quyết định sinh audit bất biến |
| Owner / Duyệt / Trạng thái | BE / TL + PO / **OPEN** |

### AIR-007 — Kill switch cộng đồng khóa luôn moderation · High

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI`: 5 endpoint staff (`/admin/moderation/*`, `/admin/comments/{id}/pin`) gate bằng `community.comments` |
| Bằng chứng | **SUY LUẬN** (dựa trên gate đọc được trong spec) |
| Phân tích | Khi sự cố xảy ra, PO/Legal tắt cờ cộng đồng để dừng ghi public. Nếu gate cũng áp vào thao tác của moderator, đội vận hành **không ẩn/gỡ được** bình luận vi phạm đã tồn tại, cũng không xử lý được case, tức là kill switch cản trở chính việc dọn dẹp. |
| Phương án | Tách hai lớp: (a) cờ cộng đồng chặn **public read/write**; (b) moderation và takedown của staff luôn khả dụng theo RBAC (hoặc dùng cờ riêng `community.moderation` mặc định ON, chỉ Security tắt được). Ghi rõ trong registry ở AIR-001. |
| Liên quan | FR-COM-007/009, FR-MOD-002/003 · WBS-062, WBS-065 · 5 dòng ACI |
| Effort | Không đáng kể (cấu hình policy) |
| Test chứng minh | Tắt cờ cộng đồng: member POST trả `403`, moderator HIDE vẫn `200` và có audit |
| Owner / Duyệt / Trạng thái | TL / PO + Legal / **OPEN** |

### AIR-008 — Takedown request sai schema, thiếu vòng đời · High

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI` `/admin/takedown-requests`; FR-MOD-004 |
| Bằng chứng | **TOOL T27**: GET trả `ModerationCaseList`; chỉ có POST và GET list, không có chi tiết/cập nhật/đóng |
| Phân tích | FR-MOD-004 yêu cầu "tiếp nhận và theo dõi ... theo workflow duyệt", với negative case "không xóa bằng chứng yêu cầu". Hợp đồng hiện không có trạng thái, SLA, người xử lý, liên kết tới hành động gỡ, hay bằng chứng bất biến. |
| Phương án | `TakedownRequest` có trạng thái `RECEIVED → TRIAGED → ACTIONED / REJECTED → CLOSED`, `requesterType`, `legalReference`, `evidenceRefs[]` (chỉ thêm, không xóa), `slaDueAt`, `linkedActions[]`. Thêm `GET/PATCH /admin/takedown-requests/{id}` (If-Match) và `POST .../actions` để nối tới unpublish. GET list trả `TakedownRequestList`. |
| Liên quan | FR-MOD-003/004, FR-DISC-004 · WBS-033, WBS-118 · 1 dòng ACI |
| Effort | WD-10: WBS-118 thêm 2.58 PD BE; WD-12: UI FE (TBD) |
| Test chứng minh | T27 PASS; ACTIONED bắt buộc có hành động gỡ liên kết; xóa evidence trả `405/409` |
| Owner / Duyệt / Trạng thái | BE / Legal + TL / **OPEN** |

### AIR-009 — Ma trận 401/404/429/Retry-After và danh mục mã lỗi · High

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI`; guide §3–4 |
| Bằng chứng | **TOOL T06**: 8 thao tác cần đăng nhập thiếu `401`. **T07**: 5 thao tác có path param thiếu `404`. **T08**: 7 thao tác ghi thiếu `429`, và `TooManyRequests` không có `Retry-After`. **T10**: `createVideoComment` dùng `COMMENT_DISABLED`, trong khi guide quy định `FEATURE_DISABLED/ELIGIBILITY_REQUIRED` |
| Phân tích | Các thao tác "đẹp nhất" (submit/approve/publish, upload, moderation) lại là nơi thiếu mã trạng thái, vì được viết tay riêng còn phần còn lại sinh từ mẫu. `Problem.code` là chuỗi tự do nên client không thể xử lý lỗi một cách chắc chắn. |
| Phương án | Bổ sung mã còn thiếu theo danh sách ACI. Thêm header `Retry-After` vào `TooManyRequests`. Lập **danh mục mã lỗi** dạng enum (`FEATURE_DISABLED`, `ELIGIBILITY_REQUIRED`, `CSRF_INVALID`, `REVISION_CONFLICT`, `PRECONDITION_REQUIRED`, `IDEMPOTENCY_KEY_REUSED`, `SPONSORSHIP_LABEL_REQUIRED`, `SPONSORSHIP_EXPIRED`, `RIGHTS_EXPIRED`, `MEDIA_NOT_READY`, `SOD_VIOLATION`, `REASON_REQUIRED`, `RATE_LIMITED`), mỗi mã gắn một HTTP status. Quy ước thứ tự kiểm tra: 401 → CSRF 403 → gate 403 → quyền 403 → 404 → 412/428 → 422 → 409 → 429. |
| Liên quan | Mọi FR có negative test · WBS-014 · 20 dòng ACI |
| Effort | WD-13 (contract test automation); sửa spec trong WBS-014 |
| Test chứng minh | T06, T07, T08, T10 PASS; bộ `test_http_contract.py` kiểm `code` trong problem+json |
| Owner / Duyệt / Trạng thái | BE / TL + QA / **OPEN** |

### AIR-010 — Danh sách status mẫu trên 44 GET · Medium

| Trường | Nội dung |
|---|---|
| Nguồn / Bằng chứng | Inventory CSV, **TOOL**: 44 GET mang nguyên danh sách `200; 400; 401; 403; 404; 409; 412; 422; 429` |
| Phân tích | 409/412/422 không có nghĩa với GET; GET public (`/home`, `/categories`, `/site/config`…) không cần 401. Tài liệu sinh ra sẽ gây hiểu sai, và contract test sẽ thiếu oracle rõ ràng. |
| Phương án | Bỏ 409/412/422 khỏi GET; bỏ 401 ở GET public; giữ 400/404/429 khi thật sự áp dụng. |
| Liên quan | WBS-014 · 1 dòng ACI tổng hợp · Effort: không đáng kể |
| Test chứng minh | Lint rule: GET không được khai báo 409/412/422 |
| Owner / Duyệt / Trạng thái | BE / TL / **OPEN** |

### AIR-011 — Nhãn tài trợ chưa lan tới mọi kênh · High (P0-ADS)

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI` `SearchHit`, `ArticleSummary`, `Video`; review prompt mục 4 (SSR, card, watch, sitemap, RSS) |
| Bằng chứng | **TOOL T14**: `SearchHit` không có `contentKind`; `ArticleSummary.contentKind` và `Video.contentKind` không bắt buộc. **ĐỌC**: không có hợp đồng nào cho sitemap/RSS |
| Phân tích | Kết quả tìm kiếm hiển thị bài tài trợ mà không có nhãn; FE có thể nhận item thiếu `contentKind` rồi mặc định coi là editorial. Sitemap/RSS do SSR sinh ra chưa có quy định nhãn, và link CTA chưa quy định `rel="sponsored"`. |
| Phương án | `contentKind` bắt buộc ở mọi DTO public; `sponsorLabel` bắt buộc khi `SPONSORED` (dùng `if/then` của JSON Schema 2020-12). Thêm vào `SearchHit`. Phụ lục SEO: RSS item có tiền tố nhãn; structured data/OG ghi rõ; link tài trợ dùng `rel="sponsored noopener"`. Nhãn hiển thị lấy từ server, FE không được đổi. |
| Liên quan | FR-NEWS-008, FR-ADS-001, FR-DISC-001 · WBS-133, WBS-052 · 4 dòng ACI |
| Effort | WD-03 / WD-04 |
| Test chứng minh | T14 PASS; `test_public_sponsored_items_always_carry_label`; E2E chụp nhãn ở mọi breakpoint/kênh |
| Owner / Duyệt / Trạng thái | BE + FE / Legal + PO / **OPEN** |

### AIR-012 — Metadata tài trợ không đủ theo quyết định ads · High (P0-ADS)

| Trường | Nội dung |
|---|---|
| Nguồn | `07_ADS_MVP_DECISION` (sponsor name, campaign reference, chứng từ quyền, thời hạn, người duyệt); SQL addendum; `ArticlePatchRequest`, `UpdateVideoRequest`, `ArticleRevision` |
| Bằng chứng | **ĐỌC**: DB chỉ thêm `content_kind`, `sponsor_name`, `sponsor_reference`; API không có `sponsorReference`; `ArticleRevision` không có trường tài trợ dù SQL ghi rõ "sponsor metadata belongs on immutable content revisions" |
| Phân tích | Thiếu chứng từ, thời hạn và người duyệt nên không chứng minh được với Legal rằng nội dung tài trợ đã được duyệt, và không tự gỡ được khi hết hạn hợp đồng. |
| Phương án | Thêm object `sponsorship` vào revision: `sponsorName`, `sponsorReference`, `evidenceRef`, `validFrom`, `validTo`, `approvedBy`, `approvedAt` (hai trường cuối chỉ đọc, server ghi). DB thêm cột tương ứng trên `*_revisions` kèm CHECK khi `content_kind='SPONSORED'`. Có job kiểm tra hiệu lực tài trợ (nối với AIR-018). |
| Liên quan | FR-NEWS-008, FR-ADS-001 · WBS-132 · DB `article_revisions`, `video_revisions` · 2 dòng ACI |
| Effort | WD-03 (+1.5 PD BE cho WBS-132) |
| Test chứng minh | `test_sponsored_without_sponsor_name_rejected`; tài trợ hết hạn thì bị gỡ hoặc ẩn theo SLA |
| Owner / Duyệt / Trạng thái | BE / Legal + Commercial / **OPEN** |

### AIR-013 — Hợp đồng ad placement · High (P0-ADS)

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI` `AdPlacement*`, `/ads/placements`; SQL `tt.ad_placements`; quyết định ads (kích thước dự phòng chống CLS, không popup, CTA an toàn) |
| Bằng chứng | **TOOL T29**: không có `width/height/aspectRatio`. **ĐỌC**: chỉ có `PUT /admin/ad-placements/{placementId}` (không tạo mới được); API dùng `creativeUrl` nhưng DB dùng `media_asset_id`; `targetUrl` không ràng buộc; endpoint public trả cả `status/startsAt/endsAt` và không nhận tham số vị trí |
| Phân tích | Không có kích thước thì không đặt chỗ trước được nên gây CLS, trái tiêu chí của WBS-133. `targetUrl` dạng `javascript:` là vector XSS. Không có alt text thì vi phạm a11y. Trả trạng thái nội bộ ra public là lộ thông tin vận hành. |
| Phương án | Admin: `POST /admin/ad-placements` (hoặc PUT theo `code`), thêm `width`, `height`, `altText`, `advertiserName`, `mediaAssetId` (bỏ `creativeUrl` tự do), `targetUrl` có pattern `^https://`, kèm `If-Match`. Public: DTO rút gọn `{code, label, creativeUrl(signed CDN), altText, targetUrl, width, height, rel}`, query `placementCodes`, chỉ trả ACTIVE trong khung giờ; cờ OFF thì trả `200 {items: []}` (khớp quyết định ads). |
| Liên quan | FR-ADS-001/002 · WBS-132, WBS-133, WBS-136 · DB `ad_placements` (AIR-024) · 2 dòng ACI |
| Effort | WD-02, WD-03 |
| Test chứng minh | T29 PASS; `test_ad_slots_flag_off_returns_empty_list`; targetUrl `javascript:` trả `422`; CLS < ngưỡng NFR khi slot rỗng |
| Owner / Duyệt / Trạng thái | BE / TL + Commercial / **OPEN** |

### AIR-014 — Truy vết ads/scope lệch giữa FR84, WBS và quyết định · High

| Trường | Nội dung |
|---|---|
| Nguồn | `07_FR84_TRACEABILITY`, `07_WBS_EXTENDED_135`, `07_ADS_MVP_DECISION`, SRS addendum §F |
| Bằng chứng | **TOOL T25**: thiếu 11 liên kết, vì FR-ADS-001/002 và FR-NEWS-008 không liệt kê WBS-131..135 dù các task này ghi Trace tới chúng. **T23**: WBS-125 (P0*) treo dưới WBS-085 (P1); WBS-101 và WBS-126 (P1) treo dưới cha P0 (WBS-042, WBS-051). **ĐỌC**: FR84 vẫn ghi FR-ADS-001, FR-NEWS-008 là `P0* [PL]`, trong khi §F và quyết định ads xếp vào **P0-ADS tách biệt P0\*** |
| Phân tích | Với trạng thái này, PM không chọn được gói `P0 + P0-ADS` độc lập: phần ads vẫn dính vào nhãn P0*, còn effort FR-NEWS-008 lại nằm ở P1. Ngược lại, P1 (FR-CMS-008, FR-NEWS-009) có thể đang ẩn trong ước lượng P0. |
| Phương án | Áp WD-05 → WD-09: đổi cha WBS-125 sang WBS-132 với scope P0-ADS; WBS-101 chuyển sang WBS-086; WBS-126 chuyển vào P1 backlog; bổ sung WBS-131..135 vào FR84. Ghi chú trong SRS rằng FR-ADS-001/FR-NEWS-008 có `Release package = P0-ADS`, **giữ nguyên Priority gốc** (không đổi baseline, chỉ thêm cột gói phát hành). |
| Liên quan | FR-ADS-001/002, FR-NEWS-008/009, FR-CMS-008 · WBS-085, 089, 101, 125, 126, 131–135 |
| Effort | 0 PD (sửa truy vết) |
| Test chứng minh | T23, T25 PASS |
| Owner / Duyệt / Trạng thái | BA / PO + PM / **OPEN** |

### AIR-015 — Đếm trùng/thiếu effort · High (ngân sách)

| Trường | Nội dung |
|---|---|
| Nguồn | `07_WBS_EXTENDED_135`, `07_42_MISSING_FR…` (cột "0 additional estimate"), `07_ADS_MVP_DECISION` |
| Bằng chứng | **TOOL T24**: 14/42 child có FR **không nằm trong Trace** của WBS cha (WBS-091, 099, 100, 101, 102, 109, 113, 114, 118, 119, 120, 121, 125, 126). **ĐỌC**: WBS-085 (P1, FE 11.33 PD "Ads slots/sponsor labels/management UI") trùng phạm vi WBS-133 |
| Phân tích | **Đếm trùng:** nếu duyệt P0-ADS rồi sau này duyệt cả WBS-085, phần slot và nhãn (khoảng 5.17 PD FE) bị tính hai lần. **Thiếu:** P0-ADS không có FE cho CMS (nhập trường tài trợ, cấu hình placement). WBS-133 chỉ lo hiển thị public. **Child "0 PD":** 4 child có cha mang mã UX bao trùm (099, 100, 102, 114), coi như đã có trong ước lượng cha. Các child còn lại (109, 113, 118, 119, 121) có thể chưa được ước lượng, cần TL xác nhận với Excel 05 (**TBD**). Ngoài ra, nhìn WBS 07 không thấy task FE cho quản trị user/role (FR-ID-004), sổ takedown và tra cứu audit log (**TBD** theo UX 03/Excel 05). |
| Phương án | Áp `07_WBS_REESTIMATION_DIFF.csv`: tách WBS-085 (WD-01), thêm WBS-136 (WD-02), tăng WBS-132/134 (WD-03, WD-04), tăng WBS-118 (WD-10) và WBS-109 (WD-14), thêm WBS-137/138/139 (WD-11 → WD-13, cần xác minh trước khi cộng). Quy tắc: chỉ cộng child khi FR **không** có trong Trace cha **và** TL xác nhận Excel 05 chưa tính. |
| Liên quan | WBS-085, 089, 109, 118, 131–139 |
| Effort | Xem bảng tổng ở `07_AI_REVIEW_SUMMARY.md` §4: P0-ADS +6.67 PD; P0 tối đa +16.92 PD (trong đó 9.17 PD là TBD); P0* +1.08 PD (cập nhật: WD-14 SUPERSEDED bởi migration 0307, P0* còn +0); P1 −8.83 PD. Truy vết đã sửa ở `v0.3/trace/` |
| Test chứng minh | T24 được giải trình từng dòng; bảng tái ước lượng có chữ ký PM/Finance |
| Owner / Duyệt / Trạng thái | PM / PM + Finance + TL / **OPEN** |

### AIR-016 — Luồng OIDC callback/PKCE/state · High (bảo mật)

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI` `/auth/{staff,member}/oidc/{start,callback}` |
| Bằng chứng | **ĐỌC**: callback trả `200 BooleanStatus` (JSON); không khai báo query `code`/`state`; member callback dùng response `403 CommentDisabled` |
| Phân tích | Callback OIDC trên trình duyệt phải **302** về FE sau khi đặt cookie. Không quy định `state` (CSRF đăng nhập), PKCE và `nonce` thì dễ bị login CSRF hoặc tiêm code. FR-ID-009 yêu cầu chống chiếm tài khoản khi liên kết, nhưng hợp đồng chưa có quy tắc liên kết. |
| Phương án | Start: server sinh `state`, `nonce`, PKCE `code_verifier`, lưu cookie ngắn hạn `__Host-`. Callback: query `code`, `state` (bắt buộc), trả 302 tới đường dẫn trả về **nằm trong allowlist**; sai state thì `400`. Staff kiểm `acr/amr` để bắt MFA với vai trò cao (FR-ID-006). Member: liên kết tài khoản chỉ khi email đã xác minh **và** người dùng xác nhận lại; lỗi điều kiện dùng `403 ELIGIBILITY_REQUIRED`. Cookie: `Secure; HttpOnly; SameSite=Lax`, xoay phiên sau đăng nhập. |
| Liên quan | FR-ID-003/006/009 · WBS-017, WBS-059, WBS-116 · 2 dòng ACI |
| Effort | Nằm trong WBS-017/059 |
| Test chứng minh | Thiếu/sai state trả 400; open-redirect bị chặn; phiên được xoay; vai trò cao không có MFA thì `403` |
| Owner / Duyệt / Trạng thái | BE + SEC / TL + SEC / **OPEN** |

### AIR-017 — Kênh yêu cầu dữ liệu cá nhân phụ thuộc đăng nhập P0* · High (pháp lý)

| Trường | Nội dung |
|---|---|
| Nguồn | FR-ID-005 (P0 [PL]); `POST /me/data-requests` (`memberSession`); đăng nhập member là P0* (mặc định OFF) |
| Bằng chứng | **SUY LUẬN** dựa trên scope và security đọc được |
| Phân tích | Ở cấu hình P0 public, member chưa bật nên **không ai gửi được** yêu cầu dữ liệu cá nhân qua API, trong khi site vẫn thu analytics/cookie của độc giả. FR-ID-005 là P0. Ngoài ra `GET /me` (P0) chỉ nhận `memberSession`, và FR-ID-002 (RBAC CMS) đang map nhầm vào đây. |
| Phương án | Thêm kênh public `POST /privacy/requests` (xác minh qua email/OTP, rate limit, chống bot theo policy, không yêu cầu tài khoản), hoặc quy trình ngoài hệ thống do Legal duyệt và công bố trên trang chính sách (WBS-075). Map FR-ID-002 sang `GET /admin/auth/session`; chuyển `/me` sang P0* (`community.member`). Nội dung pháp lý cụ thể cần **Legal xác nhận** theo quy định bảo vệ dữ liệu cá nhân hiện hành. |
| Liên quan | FR-ID-002/005, FR-ANL-004 · WBS-074, WBS-075 · 2 dòng ACI |
| Effort | +1–2 PD BE trong WBS-074 (**TBD**, chưa đưa vào diff vì cần Legal chọn phương án) |
| Test chứng minh | Người không có tài khoản gửi được yêu cầu và nhận mã ticket; spam trả `429` |
| Owner / Duyệt / Trạng thái | BA / Legal + PO / **OPEN** |

### AIR-018 — Hết hạn quyền và lan truyền takedown · High

| Trường | Nội dung |
|---|---|
| Nguồn | `ArticlePatchRequest.rightsExpiresAt`; `MediaStatus`; `ExternalEmbedPlaybackGrant`; FR-VID-010, FR-DISC-004, FR-CMS-009 |
| Bằng chứng | **ĐỌC**: `ExternalEmbedPlaybackGrant.expiresAt` không bắt buộc; `MediaStatus` không có trạng thái quyền/hiển thị/giấy phép; không có cơ chế (job/sự kiện) gỡ khi `rightsExpiresAt` tới; không có trạng thái purge CDN để đo SLA takedown |
| Phân tích | Bất biến "hết quyền thì không còn đường truy cập công khai" phụ thuộc thứ tự: chặn ở origin, rồi thu hồi grant, rồi purge CDN, rồi gỡ khỏi search/sitemap/feed. Hợp đồng chưa có điểm đo cho bước nào. Grant embed không hết hạn thì không thu hồi được. |
| Phương án | (1) `expiresAt` bắt buộc ở mọi grant, TTL tối đa theo policy. (2) `MediaStatus` thêm `rightsStatus`, `visibility`, `licenseId`, `rightsExpiresAt`. (3) Job `rights-expiry` chạy theo lịch, đi qua outbox: unpublish, phát sự kiện `ContentWithdrawn`, các consumer search/sitemap/cache/ad-slot xử lý idempotent. (4) Bảng `takedown_propagation(content_id, step, status, completed_at)` cho phép đo SLA, kèm endpoint admin chỉ đọc. (5) Playback trả `403 RIGHTS_EXPIRED` / `410` khi đã takedown. |
| Liên quan | FR-VID-010, FR-DISC-004, FR-NEWS-006/007, FR-CMS-009 · WBS-033, 039, 040, 112, 128 · 2 dòng ACI |
| Effort | Nằm trong WBS-033/039 (**TBD**: so với ước lượng 05) |
| Test chứng minh | `test_unpublished_video_not_reachable_anywhere`; `test_expired_rights_video_has_no_playback_grant`; đo thời gian từ takedown tới khi CDN trả 404 ≤ SLA |
| Owner / Duyệt / Trạng thái | BE + MEDIA / TL + Legal / **OPEN** |

### AIR-019 — ADR: cờ tắt giữa request (FR-COM-009) · Medium

| Trường | Nội dung |
|---|---|
| Nguồn | FR-COM-009 ("feature flag bị tắt giữa request phải được chặn ở transaction write"); guide §4 |
| Bằng chứng | **SUY LUẬN**: OpenAPI không diễn đạt được yêu cầu này; cần quyết định kiến trúc |
| Phân tích | Nếu chỉ kiểm cờ ở middleware (có cache), request bắt đầu trước khi tắt cờ vẫn ghi được. Cần kiểm lại ngay trong transaction ghi. |
| Phương án | ADR: trong transaction ghi comment, `SELECT enabled, version FROM tt.feature_gates WHERE key=$1 FOR SHARE`; việc tắt cờ dùng `UPDATE` (lấy khóa ghi) nên các transaction ghi đang chạy sẽ hoàn tất trước, còn transaction mới thấy cờ OFF. Áp dụng tương tự cho eligibility và trạng thái video. Cache cờ chỉ dùng cho UX, TTL ≤ 5s. |
| Liên quan | FR-COM-009 · WBS-060, WBS-109 · DB `feature_gates` |
| Effort | WD-14 (+1.08 PD BE, P0*) |
| Test chứng minh | Test đồng thời: tắt cờ trong lúc 50 request ghi đang chạy; sau commit của lệnh tắt cờ thì không có bản ghi mới nào |
| Owner / Duyệt / Trạng thái | TL / TL + SEC / **OPEN** |

### AIR-020 — Hợp đồng comment thiếu sort/like/pinned · Medium (P0*)

| Trường | Nội dung |
|---|---|
| Nguồn / Bằng chứng | **ĐỌC**: `GET /videos/{videoId}/comments` không có `sort`; `Comment` thiếu `authorDisplayName`, `likeCount`, `likedByMe`, `pinned`, `replyCount`; có cờ `article_comments_enabled` nhưng không có API bình luận bài |
| Phân tích | FR-COM-004/005/010 cần các trường này để render, thiếu thì FE phải gọi thêm API hoặc đoán. Cờ không có API sẽ gây hiểu nhầm phạm vi. |
| Phương án | Query `sort=newest|top`; mở rộng DTO; trả comment ghim trước theo policy. Chốt với PO: bình luận bài thuộc P0* hay P1. Nếu P1, bỏ cờ khỏi cấu hình P0. |
| Liên quan | FR-COM-001/004/005/010 · WBS-061, WBS-063/064 · 1 dòng ACI · Effort: nằm trong WBS-061 |
| Test chứng minh | Sort `top` ổn định qua các trang; comment ẩn hoặc chờ duyệt không xuất hiện với người khác |
| Owner / Duyệt / Trạng thái | BE / PO + TL / **OPEN** |

### AIR-021 — DTO CMS, preview, redirect slug, thao tác không có FR · Medium

| Trường | Nội dung |
|---|---|
| Nguồn | `07_OPENAPI` CMS; FR-CMS-004/005/006, FR-NEWS-002 |
| Bằng chứng | **TOOL T19**: 20 thao tác không có FR (CRUD video CMS, taxonomy, users, campaigns). **T16**: parameter `RevisionId` khai báo nhưng không dùng, tức thiếu endpoint xem/so sánh revision. **ĐỌC**: `AdminArticle` thiếu `title`, `slug`, `contentKind`, `updatedAt`, `assigneeId`; `WorkQueue` chỉ chứa bài, không có clip; không có route tiêu thụ `previewUrl`; `GET /articles/{slug}` không có 301/308 cho slug cũ |
| Phân tích | Thiếu các trường này thì màn hình danh sách CMS (FR-CMS-006) không lọc/hiển thị được người phụ trách và thời gian, không xem được bản khác nhau giữa các revision (FR-CMS-004), preview không có đường tiêu thụ rõ ràng (FR-CMS-005), và đổi slug làm gãy link/SEO (FR-NEWS-002). Thao tác không có FR thì không có owner nghiệp vụ, dễ bị sinh mã production vô điều kiện (rủi ro đã nêu trong validation report). |
| Phương án | Gắn FR cho 20 thao tác (CRUD video → FR-VID-008/FR-CMS-001; taxonomy → FR-NEWS-003; users → FR-ID-004), hoặc loại khỏi hợp đồng. Mở rộng `AdminArticle`. `WorkQueue` dùng item đa hình `{contentType, id, title, status, assignee, updatedAt}`. Thêm `GET /admin/articles/{id}/revisions/{revisionId}?compareTo=`. Thêm route `GET /preview/articles/{token}` (`X-Robots-Tag: noindex`, `Cache-Control: no-store`). `GET /articles/{slug}` thêm `308` với `Location` canonical. |
| Liên quan | FR-CMS-004/005/006, FR-NEWS-002/010 · WBS-025, 031, 034, 121 · 24 dòng ACI · Effort: nằm trong WBS cha |
| Test chứng minh | T16, T19 PASS; preview hết hạn trả `403/404`; slug cũ trả 308 |
| Owner / Duyệt / Trạng thái | BE + BA / TL + PO / **OPEN** |

### AIR-022 — Hợp đồng sự kiện analytics · Medium

| Trường | Nội dung |
|---|---|
| Nguồn / Bằng chứng | **ĐỌC**: `Event` chỉ có `eventName`, `contentId`, `eventId`, `timestamp`, `durationMs`; endpoint ẩn danh trả `200` |
| Phân tích | FR-ANL-003 (attribution bài ↔ video) cần nguồn và phiên. FR-ANL-004 cần trạng thái consent. Endpoint ẩn danh nhận batch tới 100 sự kiện dễ bị bơm số liệu giả. |
| Phương án | Thêm `contentType`, `sourceContentId`, `sessionId` (ẩn danh, xoay vòng, không phải PII), `consentState`, `schemaVersion`; trả `202 Accepted`; giới hạn kích thước body (`413`), rate limit theo IP/phiên (`429`), lọc bot; dedupe theo `eventId` (FR-ANL-002). |
| Liên quan | FR-ANL-001..004 · WBS-053/054/055 · 1 dòng ACI · Effort: nằm trong WBS-053/055 |
| Test chứng minh | Gửi trùng `eventId` chỉ được đếm một lần; không có consent thì không ghi sự kiện cần consent |
| Owner / Duyệt / Trạng thái | DATA / TL + Legal / **OPEN** |

### AIR-023 — Chính sách Idempotency-Key · Medium

| Trường | Nội dung |
|---|---|
| Nguồn / Bằng chứng | **TOOL T12**: `POST /admin/articles` (201) không có `Idempotency-Key`; ngược lại 47 thao tác bắt buộc header này, kể cả PATCH, PUT và logout |
| Phân tích | Guide nói "thao tác tạo có nguy cơ retry phải có Idempotency-Key", nhưng thao tác tạo bài lại thiếu. Bắt buộc với PUT/logout vốn đã idempotent chỉ tăng ma sát cho client mà không thêm an toàn. |
| Phương án | Bắt buộc cho POST tạo mới và POST hành động (submit/approve/publish/...). Tùy chọn hoặc bỏ với PUT/DELETE/logout. PATCH dùng `If-Match` (AIR-004). Quy định thời gian lưu khóa (đề xuất 24h), phạm vi `actor+method+route`, phát lại response cũ kèm header `Idempotent-Replayed: true`. |
| Liên quan | FR-COM-012 · WBS-014, WBS-020 · 3 dòng ACI · Effort: không đáng kể |
| Test chứng minh | T12 PASS; `test_comment_idempotent_replay_and_key_reuse_conflict` |
| Owner / Duyệt / Trạng thái | BE / TL / **OPEN** |

### AIR-024 — Gia cố SQL addendum P0-ADS · Medium

| Trường | Nội dung |
|---|---|
| Nguồn / Bằng chứng | **ĐỌC** `07_DB_ADS_LITE_REFERENCE_v0.2.sql`; không chạy được vì thiếu schema 04 (**TBD**) |
| Phân tích | (1) `content_kind` chưa có CHECK. (2) CHECK duyệt chỉ áp cho `ACTIVE`, nên `APPROVED` có thể thiếu người duyệt. (3) `target_url` không ràng buộc scheme. (4) Không có script rollback/down. (5) `updated_at` và `row_version` không tự cập nhật. (6) Không có audit cho thay đổi placement. (7) Index chưa tối ưu cho truy vấn slot đang chạy. (8) Thiếu `width`, `height`, `alt_text`, `advertiser_name` (AIR-013). (9) `feature_gates` chỉ ghi `key, enabled`, chưa rõ có cột duyệt như yêu cầu ở tài liệu song hành. |
| Phương án | Migration có version: `ADD CONSTRAINT ck_*_content_kind CHECK (content_kind IN ('EDITORIAL','SPONSORED')) NOT VALID` rồi `VALIDATE`; `CHECK (content_kind <> 'SPONSORED' OR sponsor_name IS NOT NULL)` trên revision; CHECK duyệt áp cho `('APPROVED','ACTIVE')`; `CHECK (target_url IS NULL OR target_url ~ '^https://')`; thêm cột ở (8); trigger `updated_at` hoặc cập nhật ở tầng ứng dụng; partial index `WHERE status='ACTIVE'`; ghi audit qua tầng domain (WBS-019); có file `down`. |
| Liên quan | FR-ADS-001/002, FR-NEWS-008 · WBS-132, WBS-013 · DB `ad_placements`, `*_revisions`, `feature_gates` |
| Effort | Nằm trong WD-03 |
| Test chứng minh | Chạy migration up/down trên bản sao DB; dữ liệu vi phạm CHECK bị chặn; test publish đồng thời |
| Owner / Duyệt / Trạng thái | BE (DBA) / TL + DBA / **OPEN** |

### AIR-025 — Chất lượng test design và mã TC · Medium

| Trường | Nội dung |
|---|---|
| Nguồn / Bằng chứng | **ĐỌC**: 42 positive test dùng cùng một mẫu ("trạng thái/đầu ra đúng SRS, lưu traceId"); 42 FR `EXISTING_EXACT` chỉ ghi "AT: map by QA before signoff", chưa có mã TC |
| Phân tích | Positive test không có oracle cụ thể nên không tự động hóa được. Chỉ 50% FR có mã TC, nên truy vết FR → test chưa đủ 84/84. |
| Phương án | Mỗi FR có ít nhất 2 TC với dữ liệu và kết quả mong đợi đo được (status, code, trường DTO, trạng thái DB, audit). Gán mã `TC-FR-*` cho cả 84 FR. Liên kết TC với test tự động (contract/HTTP/E2E). Bộ `contract-tests/` có thể dùng làm điểm khởi đầu. |
| Liên quan | 84 FR · WBS-068, WBS-069, WBS-139 · Effort: nằm trong WBS-068 (+WD-13) |
| Test chứng minh | Báo cáo coverage: 84/84 FR có TC, và TC P0 có test tự động tương ứng |
| Owner / Duyệt / Trạng thái | QA / QA Lead + BA / **OPEN** |

### AIR-026 — Watch URL video và video sitemap · Low

| Trường | Nội dung |
|---|---|
| Nguồn / Bằng chứng | **SUY LUẬN**: `Video.slug` bắt buộc nhưng route public dùng `{videoId}` (uuid); không có hợp đồng video sitemap |
| Phân tích | URL chỉ có uuid làm watch page kém thân thiện SEO/chia sẻ (FR-VID-003). Nếu đổi dạng URL sau khi đã index thì phải redirect hàng loạt. Video sitemap thiếu quy ước sẽ khó đạt NFR-SEO-001 và khó gỡ nhanh khi takedown. |
| Phương án | Chốt canonical watch URL (ví dụ `/video/{slug}-{shortId}`), redirect từ dạng uuid. Video sitemap cần `thumbnail_loc`, `title`, `description`, `content_loc`/`player_loc`, `duration`, `publication_date`; gỡ ngay khi takedown (AIR-018). |
| Liên quan | FR-VID-003, FR-DISC-007, NFR-SEO-001 · WBS-044, WBS-052 · Effort: nằm trong WBS-052 |
| Test chứng minh | Crawler smoke (WBS-057): URL uuid trả 301 về canonical; video đã gỡ không còn trong sitemap sau SLA |
| Owner / Duyệt / Trạng thái | FE / TL + SEO / **OPEN** |

### AIR-027 — Lỗi nhỏ trong schema · Low

| Trường | Nội dung |
|---|---|
| Nguồn / Bằng chứng | **TOOL T17**: `Video.commentsEnabled` có khóa `defaults false: null` (gõ sai, validator 3.1 bỏ qua nên không có default). **T16**: component mồ côi `Flag`, `ModerationActionRequest`, `RevisionId`, `CampaignId`. **TOOL**: 73/106 thao tác có `description: ''` |
| Phân tích | JSON Schema 2020-12 cho phép khóa lạ nên validator không báo lỗi, nhưng FE/SDK sinh mã sẽ không có giá trị mặc định `false`, ngược ý đồ "server policy-derived, mặc định tắt". Component mồ côi gây hiểu nhầm cho người sinh mã. |
| Phương án | Sửa thành `default: false`; dùng hoặc xóa component mồ côi (gắn với AIR-006/021); bổ sung mô tả hành vi, quyền và lỗi cho từng thao tác. |
| Liên quan | 2 dòng ACI · Effort: không đáng kể |
| Test chứng minh | T16, T17 PASS; lint: không có `description: ''` |
| Owner / Duyệt / Trạng thái | BE / TL / **OPEN** |

### AIR-028 — Lỗi định dạng tài liệu và dependencies · Low

| Trường | Nội dung |
|---|---|
| Nguồn / Bằng chứng | **ĐỌC**: cột "Parent hiện có" ở `07_SRS_WBS_TRACEABILITY_ADDENDUM` §D bị tách thành từng ký tự (ví dụ `W, B, S, -, 0, 8, 5`), tức chuỗi bị duyệt như mảng ký tự khi sinh bảng. WBS-131..135 có dependencies giống hệt nhau (`WBS-022; WBS-032; WBS-016`) |
| Phân tích | Bảng §D khó đọc và dễ bị hiểu sai cha của task. Dependencies giống hệt nhau làm lịch và critical path của gói P0-ADS sai: QA (134) có thể được xếp chạy song song với BE (132). |
| Phương án | Sinh lại bảng §D từ `07_42_MISSING_FR…csv`. Sửa dependencies theo WD-15 (131←022/032; 132←131/013/014; 133←016/132; 134←132/133; 135←010/011). |
| Liên quan | WBS-131..135 · Effort: 0 |
| Test chứng minh | Bảng §D khớp cột `Parent WBS` của CSV; công cụ lập lịch không báo vòng lặp và 134 bắt đầu sau 132/133 |
| Owner / Duyệt / Trạng thái | BA + PM / PM / **OPEN** |

---

## 4. Tác động migration (tổng hợp)

| Đối tượng | Thay đổi đề xuất | Đề xuất nguồn |
|---|---|---|
| `tt.article_revisions`, `tt.video_revisions` | CHECK `content_kind`; nhóm cột `sponsorship_*` (evidence_ref, valid_from/to, approved_by/at); CHECK bắt buộc sponsor khi SPONSORED | AIR-012, AIR-024 |
| `tt.ad_placements` | `width`, `height`, `alt_text`, `advertiser_name`; CHECK https; CHECK duyệt cho APPROVED/ACTIVE; partial index; down script | AIR-013, AIR-024 |
| `tt.feature_gates` | Khóa theo registry; `approval_reference`, `approved_by`, `approved_at`, `version` (TBD so với schema 04) | AIR-001, AIR-019 |
| Mới: `tt.takedown_requests`, `tt.takedown_evidence` (chỉ thêm), `tt.takedown_propagation` | Vòng đời takedown + đo SLA lan truyền | AIR-008, AIR-018 |
| `tt.moderation_cases` (schema 04, TBD) | `priority`, `assignee_id`, `sla_due_at`, `violation_type` | AIR-006 |

Thứ tự triển khai đề xuất: registry cờ → bảng revision (NOT VALID → VALIDATE) → ad_placements → takedown. Mọi bước cần snapshot và đã chạy thử rollback trên bản sao trước khi đưa lên staging.

## 5. Phần chưa chứng minh được (TBD)

- Mọi so sánh với SRS 02, UX 03, schema 04 và Excel 05: thiếu tài liệu nguồn trong `doc/`.
- WD-11 và WD-12 (task FE mới cho user admin, takedown, audit) chỉ cộng sau khi đối chiếu Excel 05/UX 03.
- Hành vi runtime (HTTP, transaction, CDN purge, rate limit): mới có kịch bản trong `test_http_contract.py`, chưa có server.
- Căn cứ pháp lý (quảng cáo, dữ liệu cá nhân): AI chỉ nêu rủi ro, **Legal kết luận**.

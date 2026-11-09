---
document_id: TINHTE-TRACE-CHANGELOG-v0.3
project: Tinhte.com.vn
version: 0.3.0-draft
status: DRAFT — CHỜ BA / TECH LEAD / PM DUYỆT
based_on: 07_FR84_TRACEABILITY_v0.2.csv, 07_WBS_EXTENDED_135_v0.2.csv, 07_42_MISSING_FR_IMPLEMENTATION_TASKS_v0.2.csv (không bị sửa)
created_at: 2026-11-09
---

# Truy vết FR84 / WBS v0.3 DRAFT — Changelog

## 1. Kết quả

| File | Nội dung |
|---|---|
| `07_FR84_TRACEABILITY_v0.3_DRAFT.csv` | 84 FR. Cột mới: gói phát hành, child coverage, WBS đề xuất (chờ PM), API theo spec v0.3, migration DB v0.3, TC ID, trạng thái thiết kế test, test tự động liên quan, ChangeV03 |
| `07_WBS_EXTENDED_v0.3_DRAFT.csv` | 139 dòng: 135 dòng gốc và 4 dòng **đề xuất** WBS-136…139. Cột mới: ReleasePackage, ChildCoverage, ProposedOMP/ProposedPERTPD/ProposalRef, TraceChangeV03 |
| `07_42_CHILD_TASKS_v0.3_DRAFT.csv` | 42 child task với cha v0.3, coverage, TC ID POS/NEG, API v0.3, cách tính effort |

**Kiểm chứng:**

- `contract-tests/test_static_contract.py` trên toàn bộ bản v0.3 (spec, inventory, FR84, WBS): **34/34 PASS** (log `contract-tests/static_run_v0.3_2026-11-09.txt`). Các test truy vết đã FAIL ở v0.2 nay đều PASS: T23 (scope con/cha), T24 (coverage của child), T25 (ads trong FR84).
- Thêm test mới:
  - **T31:** mỗi FR có TC POS/NEG, có API, có gói phát hành hợp lệ.
  - **T32:** tập FR trong spec trùng khớp tập FR trong FR84.
  - **T33:** Trace của WBS và cột WBS của FR84 **khớp hai chiều**. T33 FAIL trên v0.2, đó là bằng chứng các lệch được sửa ở §2.
- **PERT chính thức không đổi:** 896.84 PD ở v0.2, 896.84 PD ở v0.3. Mọi thay đổi effort chỉ nằm ở cột `ProposedPERTPD`, chờ PM/Finance ký (xem `../../07_WBS_REESTIMATION_DIFF.csv`).

## 2. Thay đổi liên kết FR → WBS (12 FR)

FR84 v0.3 được **sinh từ Trace của WBS**, nên hai chiều luôn khớp: một WBS có FR trong Trace thì FR84 phải liệt kê WBS đó, và ngược lại.

| FR | Thêm WBS | Bỏ WBS | Lý do |
|---|---|---|---|
| FR-ADS-001 | WBS-131, WBS-132, WBS-133, WBS-134 | — | WBS-131…134 Trace tới FR (WD-07) |
| FR-ADS-002 | WBS-132, WBS-133, WBS-135 | — | WBS-132/133/135 Trace tới FR (WD-07) |
| FR-ANL-002 | WBS-053 | — | Trace WBS-053 có FR-ANL-001..004 (WD-16) |
| FR-ANL-003 | WBS-053 | — | Trace WBS-053 có FR-ANL-001..004 (WD-16) |
| FR-ANL-004 | WBS-053 | — | Trace WBS-053 có FR-ANL-001..004 (WD-16) |
| FR-CMS-008 | WBS-086 | WBS-042 | FR P1 chuyển sang WBS-086 (P1) (WD-08) |
| FR-COM-009 | WBS-059 | — | WBS-059: Denied if ineligible or legal OFF |
| FR-DISC-003 | WBS-051 | — | Trace WBS-051 có FR-DISC-002,003 (WD-16) |
| FR-MOD-006 | WBS-018, WBS-061 | WBS-062 | Rate-limit WBS-061 + suspend WBS-018; WBS-062 không bao hàm (WD-18) |
| FR-NEWS-008 | WBS-131, WBS-132, WBS-133, WBS-134 | — | WBS-131…134 Trace tới FR (WD-07) |
| FR-NEWS-009 | — | WBS-051 | FR P1, bỏ cha P0 WBS-051 → P1 backlog (WD-09) |
| FR-VID-003 | WBS-041 | — | Trace WBS-041 có FR-VID-001,003 (WD-16) |

## 3. Ghi rõ Trace của WBS cha (21 WBS, WD-17)

Những WBS dưới đây vốn **đã bao hàm** FR theo deliverable/acceptance, nhưng Trace chỉ ghi mã UX hoặc ghi thiếu. v0.3 ghi rõ FR vào Trace và **không đổi effort**. TL cần xác nhận từng dòng; dòng nào không đồng ý thì child tương ứng phải chuyển sang `NEEDS_OWN_ESTIMATE`.

| WBS | Task | FR thêm vào Trace | Căn cứ |
|---|---|---|---|
| WBS-015 | Khung Next.js SSR, routes & shared data client | FR-ID-001 | Khung SSR/route public: đọc nội dung không cần đăng nhập |
| WBS-018 | RBAC/permission policy trên API & CMS | FR-MOD-006 | RBAC + suspend/reactivate (FR-ID-004) là cơ chế chặn tài khoản vi phạm |
| WBS-022 | Content type, article revision, publish pointer | FR-NEWS-002 | Domain bài: ID ổn định, slug, publish pointer |
| WBS-024 | Draft/review/approve/reject state machine | FR-NEWS-004 | State machine draft/review/approve/reject = vòng đời bài |
| WBS-025 | Publish scheduling + preview token & noindex | FR-CMS-005 | Deliverable 'preview token & noindex' |
| WBS-026 | Optimistic concurrency + edit conflict & history | FR-NEWS-005 | Edit conflict & history = sửa bài đã công bố bằng revision |
| WBS-030 | CMS editor bài viết + lưu nháp và preview | FR-CMS-005 | Deliverable 'lưu nháp và preview' |
| WBS-031 | CMS dashboard + hàng đợi duyệt/trạng thái | FR-CMS-006 | Dashboard + hàng đợi duyệt/trạng thái, 'role filtered lists' |
| WBS-033 | Takedown/correction, public invalidation events | FR-DISC-004, FR-MOD-004 | Public invalidation events; phần hành động gỡ của khiếu nại (sổ tiếp nhận vẫn thiếu → WBS-118) |
| WBS-034 | 404/410, redirects, accessibility cho bài/tin | FR-NEWS-002 | Deliverable 'redirects' + canonical |
| WBS-042 | Article ↔ video associations & editorial curation | FR-DISC-005 | Liên kết bài ↔ video many-to-many, không trả link private |
| WBS-046 | Video upload/status/editor CMS + media library | FR-CMS-009 | Deliverable 'media library' |
| WBS-056 | CMS analytics dashboard v1 & definitions | FR-ANL-005 | CMS analytics dashboard v1 & definitions |
| WBS-059 | Member login/verification/eligibility + flag | FR-COM-009 | Acceptance 'Denied if ineligible or legal OFF' |
| WBS-060 | Comment CRUD, reply tree and idempotency | FR-COM-009 | Đường ghi comment: kiểm cờ/eligibility trong transaction (guard DB 0307) |
| WBS-061 | Like/pin/sort/pagination rate-limit | FR-MOD-006 | Rate-limit, 'anti-spam' = phát hiện lạm dụng tần suất |
| WBS-063 | Mobile comment bottom sheet + composer | FR-COM-012 | Acceptance 'no double send' |
| WBS-076 | Security/privacy/legal release checklist, external coordination | FR-MOD-004 | Điều phối pháp lý với cơ quan quản lý cho khiếu nại |
| WBS-083 | Bookmarks/profile API & notification baseline | FR-MOD-007 | Notification baseline = phần thông báo của appeal (luồng appeal vẫn chưa ước lượng) |
| WBS-085 | Ads slots/sponsor labels/management UI | FR-ADS-004, FR-NEWS-008 | Deliverable 'sponsor labels' (FE) và phần video ads P1 |
| WBS-086 | Advanced recommendation & report v2 | FR-CMS-008 | Recommendation P1 = gợi ý bài/video/tag liên quan cho editor |

## 4. Đổi cha và dependencies

| WBS | Cha v0.2 | Cha v0.3 | Lý do |
|---|---|---|---|
| WBS-101 | WBS-042 | WBS-086 | WD-08: FR-CMS-008 là P1, không treo dưới WBS-042 (P0) |
| WBS-109 | WBS-060 | WBS-059; WBS-060 | Thêm WBS-059 (eligibility + cờ) |
| WBS-119 | WBS-062 | WBS-061; WBS-018 | WD-18: rate-limit (061) + suspend (018); WBS-062 không bao hàm |
| WBS-125 | WBS-085 | WBS-132 | WD-05: FR-NEWS-008 thuộc gói P0-ADS |
| WBS-126 | WBS-051 | (không cha — P1 backlog) | WD-09: FR-NEWS-009 là P1 → P1 backlog, bỏ cha WBS-051 (P0) |

| WBS | Dependencies v0.2 | Dependencies v0.3 (WD-15) |
|---|---|---|
| WBS-131 | WBS-022; WBS-032; WBS-016 | WBS-022; WBS-032 |
| WBS-132 | WBS-022; WBS-032; WBS-016 | WBS-131; WBS-013; WBS-014 |
| WBS-133 | WBS-022; WBS-032; WBS-016 | WBS-016; WBS-132 |
| WBS-134 | WBS-022; WBS-032; WBS-016 | WBS-132; WBS-133 |
| WBS-135 | WBS-022; WBS-032; WBS-016 | WBS-010; WBS-011 |

## 5. Coverage của 42 child task

| Coverage | Số child | Ý nghĩa |
|---|---:|---|
| `INCLUDED_IN_PARENT` | 39 | FR nằm trong Trace của WBS cha, effort đã tính ở cha, không cộng thêm |
| `NEEDS_OWN_ESTIMATE` | 1 | Cha không bao hàm, cần ước lượng riêng: **WBS-118** (sổ tiếp nhận takedown, đề xuất 2.58 PD, WD-10) |
| `BACKLOG_UNESTIMATED` | 2 | P1 ngoài MVP, chưa ước lượng: **WBS-120** (appeal), **WBS-126** (series, không còn cha P0) |

WD-14 (WBS-109, +1.08 PD) được đánh dấu **SUPERSEDED**: việc kiểm cờ trong transaction đã nằm ở migration DB 0307, WBS-059/060 lo phần còn lại. TL cần xác nhận.

### Bảng mục D đã sửa (thay cho cột "Parent hiện có" bị vỡ ký tự trong `07_SRS_WBS_TRACEABILITY_ADDENDUM_v0.2.md`)

| FR | Scope SRS | Gói phát hành | Child | Cha v0.3 | Owner | Coverage |
|---|---|---|---|---|---|---|
| FR-ADS-002 | P1 | P1 | WBS-089 | WBS-085 | FE | INCLUDED_IN_PARENT |
| FR-ADS-003 | P1 | P1 | WBS-090 | WBS-085 | DATA | INCLUDED_IN_PARENT |
| FR-ADS-004 | P1 | P1 | WBS-091 | WBS-085 | FE | INCLUDED_IN_PARENT |
| FR-AI-002 | P2 | P2 | WBS-092 | WBS-087 | DATA | INCLUDED_IN_PARENT |
| FR-AI-004 | P2 | P2 | WBS-093 | WBS-088 | DATA | INCLUDED_IN_PARENT |
| FR-ANL-002 | P0 | P0 | WBS-094 | WBS-054 | FE | INCLUDED_IN_PARENT |
| FR-ANL-003 | P0 | P0 | WBS-095 | WBS-054 | FE | INCLUDED_IN_PARENT |
| FR-ANL-005 | P0 | P0 | WBS-096 | WBS-055; WBS-056 | FE | INCLUDED_IN_PARENT |
| FR-CMS-002 | P0 | P0 | WBS-097 | WBS-024 | BE | INCLUDED_IN_PARENT |
| FR-CMS-004 | P0 | P0 | WBS-098 | WBS-026 | BE | INCLUDED_IN_PARENT |
| FR-CMS-005 | P0 | P0 | WBS-099 | WBS-025; WBS-030 | FE | INCLUDED_IN_PARENT |
| FR-CMS-006 | P0 | P0 | WBS-100 | WBS-031 | FE | INCLUDED_IN_PARENT |
| FR-CMS-008 | P1 | P1 | WBS-101 | WBS-086 | BE | INCLUDED_IN_PARENT |
| FR-CMS-009 | P0 | P0 | WBS-102 | WBS-046 | FE | INCLUDED_IN_PARENT |
| FR-CMS-010 | P0 | P0 | WBS-103 | WBS-024 | BE | INCLUDED_IN_PARENT |
| FR-COM-002 | P0* | P0* | WBS-104 | WBS-060 | BE | INCLUDED_IN_PARENT |
| FR-COM-003 | P0* | P0* | WBS-105 | WBS-060 | BE | INCLUDED_IN_PARENT |
| FR-COM-005 | P0* | P0* | WBS-106 | WBS-061 | BE | INCLUDED_IN_PARENT |
| FR-COM-007 | P0* | P0* | WBS-107 | WBS-062 | BE | INCLUDED_IN_PARENT |
| FR-COM-008 | P0* | P0* | WBS-108 | WBS-061 | BE | INCLUDED_IN_PARENT |
| FR-COM-009 | P0* | P0* | WBS-109 | WBS-059; WBS-060 | BE | INCLUDED_IN_PARENT |
| FR-COM-010 | P0* | P0* | WBS-110 | WBS-061 | BE | INCLUDED_IN_PARENT |
| FR-COM-012 | P0* | P0* | WBS-111 | WBS-060; WBS-063 | FE | INCLUDED_IN_PARENT |
| FR-DISC-004 | P0 | P0 | WBS-112 | WBS-049; WBS-033 | BE | INCLUDED_IN_PARENT |
| FR-DISC-005 | P0 | P0 | WBS-113 | WBS-042 | BE | INCLUDED_IN_PARENT |
| FR-ID-001 | P0 | P0 | WBS-114 | WBS-015 | FE | INCLUDED_IN_PARENT |
| FR-ID-004 | P0 | P0 | WBS-115 | WBS-018 | BE | INCLUDED_IN_PARENT |
| FR-ID-009 | P0* | P0* | WBS-116 | WBS-059 | BE | INCLUDED_IN_PARENT |
| FR-MOD-002 | P0* | P0* | WBS-117 | WBS-062 | BE | INCLUDED_IN_PARENT |
| FR-MOD-004 | P0 | P0 | WBS-118 | WBS-033; WBS-076 | BE | NEEDS_OWN_ESTIMATE (2.58 PD đề xuất) |
| FR-MOD-006 | P0* | P0* | WBS-119 | WBS-061; WBS-018 | BE | INCLUDED_IN_PARENT |
| FR-MOD-007 | P1 | P1 | WBS-120 | WBS-083 | BE | BACKLOG_UNESTIMATED |
| FR-NEWS-002 | P0 | P0 | WBS-121 | WBS-022; WBS-034 | BE | INCLUDED_IN_PARENT |
| FR-NEWS-004 | P0 | P0 | WBS-122 | WBS-022; WBS-024 | BE | INCLUDED_IN_PARENT |
| FR-NEWS-005 | P0 | P0 | WBS-123 | WBS-022; WBS-026 | BE | INCLUDED_IN_PARENT |
| FR-NEWS-006 | P0 | P0 | WBS-124 | WBS-023 | BE | INCLUDED_IN_PARENT |
| FR-NEWS-008 | P0* | P0-ADS | WBS-125 | WBS-132 | BE | INCLUDED_IN_PARENT |
| FR-NEWS-009 | P1 | P1 | WBS-126 | (P1 backlog) | BE | BACKLOG_UNESTIMATED |
| FR-VID-004 | P0 | P0 | WBS-127 | WBS-044 | FE | INCLUDED_IN_PARENT |
| FR-VID-010 | P0 | P0 | WBS-128 | WBS-039; WBS-040 | BE | INCLUDED_IN_PARENT |
| FR-VID-011 | P0 | P0 | WBS-129 | WBS-045 | FE | INCLUDED_IN_PARENT |
| FR-VID-012 | P0 | P0 | WBS-130 | WBS-045 | FE | INCLUDED_IN_PARENT |

## 6. Dòng WBS đề xuất (chưa vào baseline, `Status = PROPOSED_PENDING_PM_SIGNOFF`)

| WBS | Scope | Role | Task | Trace | PERT đề xuất | Nguồn |
|---|---|---|---|---|---:|---|
| WBS-136 | P0-ADS | FE | CMS: trường tài trợ trong editor bài/video + màn hình cấu hình placement | FR-ADS-001; FR-ADS-002; FR-NEWS-008 | 3.67 | WD-02 |
| WBS-137 | P0 | FE | CMS quản trị user/role: suspend/reactivate/đổi role có lý do | FR-ID-004 | 5.17 | WD-11 (TBD: đối chiếu Excel 05/UX 03) |
| WBS-138 | P0 | FE | CMS sổ takedown + tra cứu audit log có phân quyền | FR-MOD-004; FR-ID-007; FR-MOD-005 | 4.00 | WD-12 (TBD: đối chiếu Excel 05/UX 03) |
| WBS-139 | P0 | QA | Tự động hóa contract test (tĩnh + HTTP) và migration test trong CI | AT-001..042 | 5.17 | WD-13 |

Effort đề xuất trên dòng có sẵn (cột `ProposedPERTPD`, PERT chính thức giữ nguyên):

| WBS | PERT chính thức | PERT đề xuất | Nguồn |
|---|---:|---:|---|
| WBS-085 | 11.33 | 2.50 | WD-01 |
| WBS-132 | 5.17 | 6.67 | WD-03 |
| WBS-134 | 3.17 | 4.67 | WD-04 |
| WBS-118 | 0 (child) | 2.58 | WD-10 |

## 7. Cột mới trong FR84 v0.3

- **Release package:** P0 / P0* / P0-ADS / P0-ADS + P1 / P1 / P2. **Scope SRS giữ nguyên** (`Scope (SRS, unchanged)`). FR-ADS-001 và FR-NEWS-008 thuộc gói P0-ADS theo quyết định ads; FR-ADS-002 là P0-ADS cho phần slot tĩnh, P1 cho quy tắc nâng cao.
- **API endpoints (v0.3):** sinh từ `x-requirements` của spec v0.3. 27 FR có danh sách API thay đổi so với v0.2.
- **DB migration (v0.3):** 28 FR có migration liên quan (0300–0311).
- **Test case IDs:** cả 84 FR có `TC-FR-…-POS/NEG`. Với 42 FR trước đây ghi "AT: map by QA", ID mới được gán ở v0.3 và **QA vẫn phải thiết kế G/W/T** (cột `Test design status`).
- **Automated tests (draft):** 35/84 FR có test tự động liên quan. Ký hiệu: `SC:` test tĩnh hợp đồng, `HTTP:` test HTTP (chưa chạy vì chưa có server), `DB:` test migration trên PGlite.

## 8. Việc còn lại

| Owner | Việc |
|---|---|
| TL | Xác nhận 21 dòng làm rõ Trace (§3) và WD-14 SUPERSEDED |
| PM + Finance | Ký các effort đề xuất (§6); quyết định WBS-136…139 (WD-11/12 cần đối chiếu Excel 05/UX 03 trước) |
| BA | Đưa cột "gói phát hành" vào SRS v0.3 (không đổi Priority gốc); xác nhận WBS-051 không chứa effort series (FR-NEWS-009) và WBS-042 không chứa FR-CMS-008 |
| QA | Thiết kế G/W/T cho 42 FR mới được gán TC ID; gắn TC với test tự động |

Chạy lại kiểm tra:

```bash
cd doc/07_AI_REVIEW/contract-tests
TT_SPEC_FILE=../v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml TT_INVENTORY_FILE=../v0.3/07_API_ENDPOINT_INVENTORY_v0.3_DRAFT.csv TT_FR84_FILE=../v0.3/trace/07_FR84_TRACEABILITY_v0.3_DRAFT.csv TT_WBS_FILE=../v0.3/trace/07_WBS_EXTENDED_v0.3_DRAFT.csv python -m unittest -v test_static_contract.py
```

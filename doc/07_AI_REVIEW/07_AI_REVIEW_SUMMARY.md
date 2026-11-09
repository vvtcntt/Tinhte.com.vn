---
document_id: TINHTE-AI-REVIEW-SUMMARY-07
project: Tinhte.com.vn
reviewed_package: Remediation 07 v0.2
version: 0.1
status: OPEN
created_at: 2026-11-09
---

# Báo cáo tổng hợp AI review — Remediation 07 v0.2

## 1. Kết luận

Gói 07 v0.2 **đạt mục tiêu truy vết tĩnh**:
- 84/84 FR có WBS + API.
- Spec hợp lệ OpenAPI 3.1.
- Inventory khớp spec 106/106.
- Số học dự toán ads đúng.

Tuy nhiên, gói này **chưa sẵn sàng đóng băng hợp đồng hay sinh mã**:
- **3 Blocker** và **14 High** cần xử lý trong bản hợp đồng **v0.3**.
- Phần lớn là lỗ hổng *hợp đồng*: thiếu If-Match/ETag, cờ tính năng lệch tên, publish không kiểm tra sponsored, list không phân trang.
- Một số mâu thuẫn truy vết/ngân sách giữa FR84, WBS và quyết định ads.

**Khuyến nghị:**
- **Được** bắt đầu Sprint 0 với phần không phụ thuộc hợp đồng: repo, CI, IaC, design system, khung SSR, media ADR.
- **Chưa** codegen client/server từ v0.2.
- Đóng các Blocker trong v0.3, rồi chạy lại `contract-tests/test_static_contract.py` cho tới khi PASS.

| Mức độ | Số đề xuất | Dòng ACI (endpoint) | Ghi chú |
|---|---:|---:|---|
| Blocker | 3 | 7 | Chặn đóng băng hợp đồng / codegen |
| High | 14 | 65 | Sửa trước khi code module liên quan |
| Medium | 8 | 29 | Sửa trước sign-off |
| Low | 3 | 4 | Tài liệu |
| **Tổng** | **28** | **105** | |

*Mức độ ở `07_API_CONTRACT_ISSUES.csv` được đánh theo từng endpoint, nên có thể thấp hơn mức của đề xuất cha. Ví dụ AIR-004 là Blocker cho PATCH bài/video và ETag, nhưng là High cho các PUT phụ.*

## 2. Blocker và High — điều kiện đóng

| Mã | Mức độ | Vấn đề (ngắn) | Điều kiện đóng (bằng chứng cần có) |
|---|---|---|---|
| AIR-001 | Blocker | 5 kiểu tên cờ; 8/8 khóa gate API không có trong DB/cấu hình | Registry cờ được PO/Legal duyệt; T11 PASS; test tích hợp: tắt cờ trong DB thì API trả 403 |
| AIR-002 | Blocker (P0-ADS) | Publish không kiểm tra `sponsored_content_publication`/nhãn | Spec publish/schedule khai báo gate điều kiện + 422/403; T10b PASS; 2 HTTP test sponsored PASS trên staging |
| AIR-004 | Blocker | 9 PATCH/PUT thiếu If-Match; GET không có ETag; rowVersion lệch kiểu | T04, T05, T18 PASS; HTTP test 412 PASS; test hai phiên sửa song song |
| AIR-003 | High | 14 list thiếu cursor/limit/filter | T13 PASS; quy ước cursor ký HMAC trong WBS-014 |
| AIR-005 | High | Rollback/publish video không chỉ định revision; enum trạng thái lệch | T28 PASS; bảng chuyển trạng thái được TL duyệt; domain test bất biến publicRevisionId |
| AIR-006 | High | Enum moderation lệch, thiếu APPROVE/LOCK, assign/priority | T15 PASS; schema ModerationCase mở rộng được PO duyệt |
| AIR-007 | High | Kill switch cộng đồng chặn luôn moderation | Registry ghi rõ phạm vi gate; test: tắt cờ, moderator vẫn HIDE được |
| AIR-008 | High | Takedown request sai schema, thiếu vòng đời | T27 PASS; vòng đời và SLA được Legal duyệt |
| AIR-009 | High | Thiếu 401/404/429/Retry-After; mã lỗi tự do | T06, T07, T08, T10 PASS; danh mục mã lỗi enum trong spec |
| AIR-011 | High | Nhãn tài trợ thiếu ở search, không bắt buộc ở DTO public | T14 PASS; E2E nhãn ở mọi kênh; Legal duyệt câu chữ nhãn |
| AIR-012 | High | Metadata tài trợ thiếu chứng từ/thời hạn/người duyệt | Spec + migration có nhóm `sponsorship_*`; Legal/Commercial duyệt trường |
| AIR-013 | High | Ad placement thiếu tạo mới, kích thước, alt, an toàn URL | T29 PASS; test `javascript:` trả 422; đo CLS khi slot rỗng |
| AIR-014 | High | FR84 thiếu WBS-131..135; WBS-125/101/126 sai cha/scope | T23, T25 PASS; FR84 v0.3 được BA/PO duyệt |
| AIR-015 | High | Trùng 5.17 PD (WBS-085 so với WBS-133); 14 child ngoài trace cha; thiếu FE CMS P0-ADS | PM/Finance ký `07_WBS_REESTIMATION_DIFF`; TL giải trình T24 từng dòng |
| AIR-016 | High | OIDC callback 200 JSON, thiếu state/PKCE/nonce | Spec 302 + code/state; test login-CSRF/open-redirect PASS; SEC duyệt |
| AIR-017 | High | Yêu cầu dữ liệu cá nhân (P0) phụ thuộc đăng nhập member (P0*) | Legal chọn kênh; endpoint hoặc quy trình được công bố ở WBS-075 |
| AIR-018 | High | Hết hạn quyền/takedown chưa có cơ chế lan truyền và đo SLA | Grant luôn có expiresAt; job rights-expiry; HTTP test takedown PASS; đo SLA purge |

Medium (AIR-010, 019–025) và Low (AIR-026–028): điều kiện đóng ghi trong từng đề xuất ở `07_AI_TECH_REVIEW_PROPOSALS.md`.

## 3. Trả lời 6 mục tiêu của review prompt

1. **84/84 FR → WBS → API → acceptance:** liên kết tĩnh đủ (T22 PASS). Chỉ 42/84 FR có mã TC (AIR-025). Có 14/42 child mà FR không nằm trong Trace của WBS cha (T24). 4 trong số đó được cha mang mã UX bao trùm. Số còn lại có thể **chưa được ước lượng** hoặc **ẩn effort P1 trong P0** (WBS-101, WBS-126). Không phát hiện đếm trùng *trong* 42 child, vì child đều 0 PD. Có rủi ro đếm trùng **giữa WBS-085 và WBS-133** (AIR-015).
2. **OpenAPI 93/106:**
   - Hợp lệ cú pháp 3.1.
   - CSRF đầy đủ cho cookie mutation (T03 PASS), gate P0* đầy đủ (T09 PASS).
   - Vẫn hỏng: If-Match/ETag (AIR-004), cursor/filter (AIR-003), ma trận status (AIR-009), idempotency (AIR-023), public/private revision (AIR-005), OIDC (AIR-016).
3. **Bất biến nghiệp vụ:**
   - Revision bất biến chưa chặt (AIR-005).
   - Hết hạn quyền/takedown chưa có cơ chế lan truyền và điểm đo (AIR-018).
   - Kiểm tra cờ khi bị tắt giữa request cần ADR ở mức transaction (AIR-019).
   - Kill switch đang khóa cả moderation (AIR-007).
4. **P0-ADS tách P0\*:** chưa tách hẳn trong truy vết (AIR-014). Nhãn chưa có ở search/sitemap/RSS (AIR-011). Metadata tài trợ thiếu (AIR-012). Đã đề xuất migration ở `07_AI_TECH_REVIEW_PROPOSALS.md` §4.
5. **WBS-131..135 so với WBS-085, capacity và chi phí:** xem §4. Không dùng đơn giá giả định làm báo giá.
6. **Contract test:** đã dựng và chạy **30 test tĩnh** (22 FAIL, mỗi FAIL là bằng chứng cho một AIR). Đã viết **15 HTTP test** cho 401/403/409/412/422/429, comment OFF, sponsored OFF, unpublish, rights hết hạn; **chưa chạy** vì chưa có server.

## 4. Tái ước lượng (đề xuất, chưa ký)

Đơn giá giả định lấy từ WBS 05/07 (BA 2.3M, BE 2.6M, FE 2.5M, QA 1.9M, DEVOPS 3.0M VND/ngày công). **Không phải báo giá.**

| Gói | v0.2 (PERT PD / triệu VND) | Sau đề xuất — phần chắc chắn | Sau đề xuất — gồm TBD (WD-11, WD-12) |
|---|---:|---:|---:|
| P0 | 676.50 / 1,736.47 | 684.25 / 1,753.00 | 693.42 / 1,775.93 |
| P0* | 104.17 / 259.64 | 104.17 / 259.64 | 104.17 / 259.64 |
| P0-ADS | 17.51 / 42.97 | 24.18 / 58.89 | 24.18 / 58.89 |
| P1 (ngoài MVP) | 59.16 / 154.68 | 50.33 / 132.61 | 50.33 / 132.61 |

| Phương án PM chọn | v0.2 | Chắc chắn | Gồm TBD |
|---|---:|---:|---:|
| P0 | 676.50 PD / 1,736.47 tr | 684.25 / 1,753.00 | 693.42 / 1,775.93 |
| P0 + P0-ADS | 694.01 / 1,779.44 | 708.43 / 1,811.89 | 717.60 / 1,834.82 |
| P0 + P0* | 780.67 / 1,996.11 | 788.42 / 2,012.64 | 797.59 / 2,035.57 |
| P0 + P0-ADS + P0* | 798.18 / 2,039.08 | 812.60 / 2,071.53 | 821.77 / 2,094.46 |

*Cập nhật 2026-11-09: WD-14 (WBS-109, +1.08 PD cho P0*) được đánh dấu SUPERSEDED vì việc kiểm cờ trong transaction đã có ở migration DB 0307, chờ TL xác nhận. Các điều chỉnh truy vết WD-05…09 và WD-15…18 đã áp vào bản truy vết v0.3 (`v0.3/trace/`) và không làm đổi effort.*

- P0-ADS sau đề xuất: **24.18 PD, 58.89 triệu**. Cộng 18% dự phòng giả định thì khoảng **69.49 triệu** (v0.2: 50.70 triệu).
- Phần giảm của P1 (−8.83 PD) là **không đếm trùng**: slot và nhãn chuyển sang WBS-133, UI quản trị cơ bản chuyển sang WBS-136.
- Chưa tính VAT, phí bên thứ ba, OPEX video/CDN, dự phòng chung của dự án.

## 5. Mức chắc chắn của bằng chứng

| Loại | Nội dung |
|---|---|
| **Chứng minh bằng tool** | Spec hợp lệ OpenAPI 3.1 (openapi-spec-validator). 30 test tĩnh: 8 PASS, 22 FAIL, log trong `contract-tests/static_run_2026-11-09.txt`. Số học dự toán ads (T26). Inventory khớp spec (T21). API trong FR84 có thật trong spec (T22). Các con số ACI do script sinh từ spec |
| **Đối chiếu tài liệu** | Metadata tài trợ (AIR-012), OIDC (AIR-016), comment DTO (AIR-020), SQL (AIR-024), test design (AIR-025), định dạng tài liệu (AIR-028) |
| **Suy luận (cần owner xác nhận)** | Kill switch moderation (AIR-007), kênh dữ liệu cá nhân (AIR-017), ADR cờ giữa request (AIR-019), các mức tăng effort WD-03/04/13/14 |
| **TBD** | Mọi đối chiếu với tài liệu 01–06 (không có trong `doc/`); WD-11/12; runtime HTTP/DB/CDN; kết luận pháp lý |

## 6. Lộ trình đóng đề xuất

1. **Tuần này — TL + BA:** chốt registry cờ (AIR-001), danh mục mã lỗi (AIR-009), quy ước If-Match/ETag/cursor/idempotency (AIR-003/004/023).
2. **BE:** ra hợp đồng **v0.3** sửa Blocker và High về API (AIR-002…018). Chạy `test_static_contract.py` trong CI, PR chỉ được merge khi không có FAIL mới.
3. **BA + PM:** sửa truy vết (WD-05…09, WD-15); PM/Finance tái ước lượng theo `07_WBS_REESTIMATION_DIFF.csv` sau khi TL xác minh với Excel 05.
4. **Legal:** kết luận AIR-002/011/012 (quảng cáo), AIR-017 (dữ liệu cá nhân), AIR-008 (takedown).
5. **QA:** khi có staging/mock, chạy `test_http_contract.py`; bổ sung TC cho 42 FR chưa có mã (AIR-025).

## 7. File đầu ra

| File | Nội dung |
|---|---|
| `07_AI_TECH_REVIEW_PROPOSALS.md` | 28 đề xuất đầy đủ trường bắt buộc (nguồn, mức độ, phân tích, phương án, liên quan, effort, test, owner, người duyệt, OPEN) |
| `07_API_CONTRACT_ISSUES.csv` | 105 vấn đề theo endpoint (status, test trace, owner, bằng chứng) |
| `07_WBS_REESTIMATION_DIFF.csv` | 15 điều chỉnh WBS, có PERT trước/sau và rủi ro đếm trùng |
| `contract-tests/test_static_contract.py` | 30 test tĩnh (chạy được ngay) |
| `contract-tests/test_http_contract.py` | 15 test HTTP (chạy khi có server, tự skip nếu chưa có) |
| `contract-tests/static_run_2026-11-09.txt` | Log lần chạy test tĩnh trên v0.2 |
| `v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml` | Hợp đồng v0.3 DRAFT áp các đề xuất (chờ TL/PO/Legal duyệt; v0.2 giữ nguyên) |
| `v0.3/07_API_ENDPOINT_INVENTORY_v0.3_DRAFT.csv` | Inventory v0.3: gate, concurrency, idempotency, mã lỗi, AIR liên quan cho từng thao tác |
| `v0.3/07_OPENAPI_v0.3_CHANGELOG.md` | Đối chiếu AIR → thay đổi, breaking changes, registry cờ, danh mục mã lỗi, việc còn lại |
| `contract-tests/static_run_v0.3_2026-11-09.txt` | Log lần chạy test tĩnh trên v0.3 |
| `v0.3/db/migrations/` | Migration PostgreSQL 0299–0310 (+ `optional/0311` chờ Legal), mỗi bản có up/down |
| `v0.3/db/README.md` | Giả định schema 04, quy trình chạy, backfill trước 0310, hợp đồng với BE, chính sách rollback |
| `v0.3/db/tools/report_not_valid_violations.sql` | Báo cáo (chỉ đọc) số dòng vi phạm từng ràng buộc trước khi VALIDATE |
| `v0.3/db/tests/` | Stub schema 04, bộ test PGlite (140 test), log `migration_test_run_2026-11-09.txt` |
| `v0.3/trace/07_FR84_TRACEABILITY_v0.3_DRAFT.csv` | FR84 v0.3: khớp hai chiều với WBS; gói phát hành, API/DB/TC/test tự động theo từng FR |
| `v0.3/trace/07_WBS_EXTENDED_v0.3_DRAFT.csv` | WBS v0.3: 135 dòng gốc + 4 dòng đề xuất; coverage của child; effort đề xuất tách khỏi PERT chính thức |
| `v0.3/trace/07_42_CHILD_TASKS_v0.3_DRAFT.csv` | 42 child task với cha v0.3, coverage, TC POS/NEG |
| `v0.3/trace/07_TRACE_v0.3_CHANGELOG.md` | Thay đổi truy vết, căn cứ làm rõ Trace, bảng mục D đã sửa, việc chờ TL/PM/BA/QA |

Chạy lại test tĩnh:

```bash
pip install pyyaml openapi-spec-validator
python doc/07_AI_REVIEW/contract-tests/test_static_contract.py
```

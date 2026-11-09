# Bản bàn giao 07 — Remediation v0.2

**Ngày:** 07/11/2026  
**Mục tiêu:** Giải quyết 4 quyết định sau Review 06, chuẩn bị để đội code triển khai song hành và giảm khoảng trống FR/WBS/API.

## Bốn đầu việc đã xử lý

1. **Pháp lý:** ghi nhận khách xác nhận sở hữu domain và đang xin giấy phép; **code ngay, phát hành public qua release gates**. Đã tạo ma trận hai luồng Engineering và Legal/Operations.
2. **Truy vết:** kiểm đếm lại đúng 84 FR, 42 đã exact, 42 còn thiếu. Đã tạo 42 nhiệm vụ con WBS-089…130, mỗi nhiệm vụ có owner, API, positive/negative tests, parent; **84/84 liên kết chính xác**.
3. **API:** từ 17 paths / 18 operations lên **93 paths / 106 operations** (P0 + P0* + P0-ADS + P1/P2 được phân loại), DTO, policy/feature gate, Error Contract và source FR.
4. **Quảng cáo:** đề xuất `P0-ADS/SPONSORED-LITE`: nhãn rõ, 1–2 banner static, CMS control; ads video dạng chen ngang để P1. 5 task WBS-131..135, dự toán mới riêng.

## Danh mục file

| File | Công dụng |
|---|---|
| `07_PARALLEL_WORKSTREAM_RELEASE_GATES_v0.2.md` | Quyết định code song song + release gates |
| `07_SRS_WBS_TRACEABILITY_ADDENDUM_v0.2.md` | Đặc tả 42 FR và positive/negative QA |
| `07_42_MISSING_FR_IMPLEMENTATION_TASKS_v0.2.csv` | 42 child implementation tasks |
| `07_FR84_TRACEABILITY_v0.2.csv` | 84/84 FR → WBS / API / Test |
| `07_WBS_EXTENDED_135_v0.2.csv` | 88 gốc + 42 child + 5 ads mới |
| `07_OPENAPI_TINHTE_EXPANDED_v0.2.yaml` | OpenAPI 3.1 93 paths / 106 operations |
| `07_API_ENDPOINT_INVENTORY_v0.2.csv` | Danh sách API & quyền/cờ/scope |
| `07_API_IMPLEMENTATION_GUIDE_v0.2.md` | Chính sách API và hạn chế trước khi codegen |
| `07_ADS_MVP_DECISION_AND_ESTIMATE_v0.2.md` | Phương án quảng cáo + dự toán độc lập |
| `07_WBS_ADS_LITE_ADDON_v0.2.csv` | Chi tiết 5 task ads và effort |
| `07_DB_ADS_LITE_REFERENCE_v0.2.sql` | Schema SQL addendum tham chiếu; CHƯA test migration |
| `07_REVIEW_FINDING_STATUS_DELTA_v0.2.md` | Trạng thái các CRV findings sau vòng sửa |
| `07_CODEX_CLAUDE_REVIEW_PROMPT_v0.2.md` | Đầu việc giao AI review độc lập |
| `07_VALIDATION_REPORT_v0.2.md` | Bằng chứng kiểm tra tĩnh / giới hạn |

## Cách sử dụng

1. Giữ nguyên bản gốc 01…06, đưa gói này vào `/docs/remediation/07-v0.2/` của repo; không ghi đè v0.1.
2. PO/BA duyệt scope, Tech Lead duyệt API/DB, QA triển khai test; Legal và Commercial xử lý release gates.
3. Tạo nhánh API mock/server trong Sprint 0, ưu tiên P0; có thể triển khai P0* code behind flags. P1/P2 chỉ là hợp đồng tham khảo và không mặc định đưa vào sprint.
4. PM/Finance tái ước lượng và cập nhật sheet dự toán 05 thành v0.2 **chỉ sau khi khử trùng lặp parent-child**.
5. Codex/Claude review thêm theo prompt, nộp đề xuất riêng không đổi baseline.

**Tình trạng:** DRAFT v0.2 — sẵn sàng kỹ thuật để review và phân rã Sprint, chưa phải baseline production đã phê duyệt.

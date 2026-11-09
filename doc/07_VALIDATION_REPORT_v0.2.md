# Validation report — Remediation 07 v0.2

**Trạng thái:** STATIC_CHECKS_PASS / IMPLEMENTATION_TESTS_NOT_RUN

| Kiểm tra | Kết quả |
|---|---|
| FR SRS | 84 |
| FR thiếu exact mapping trước khi sửa | 42 |
| FR có explicit WBS/API mapping sau sửa | 84/84 |
| New WBS child tasks | 42 (`WBS-089..130`) |
| WBS core trước đây | 88 (`WBS-001..088`) |
| New sponsored-lite tasks | 5 (`WBS-131..135`) |
| WBS tổng trong extended CSV | 135 |
| API Paths/OpenAPI Operations | 93 / 106 |
| OperationId uniqueness | PASS |
| Local schema/parameter `$ref` existence | PASS |
| URL path parameter linkage | PASS |
| Admin routes require staff session | PASS tĩnh |
| Comments API feature-gated | PASS tĩnh (không phải runtime) |
| API→FR linked | 84/84 theo bảng tham chiếu, không có nghĩa backend đã code đủ |
| YAML parsing | PASS |
| CSV row counts / WBS parent existence | PASS |

## Chưa được kiểm thử

- Chưa validate bằng OpenAPI chuyên dụng / mock service / generated SDK / contract tests có HTTP thực.
- Chưa thực thi SQL migration trên PostgreSQL hoặc thử rollback.
- Chưa đo QoE, SLA, tải đồng thời, CDN purge, security scanning, browser/E2E.
- Các mô tả Given/When/Then là **test design**, chưa phải test cases tự động chạy.
- 42 nhiệm vụ con chưa được estimate tăng thêm: không cộng ngày công hai lần nếu đã nằm trong WBS cha.
- P0-ADS là khoản đề xuất bổ sung, chưa được PO/Finance ký duyệt.

## Sai số/góc cần review

- OIDC adapter/API là mẫu công nghệ, không chọn IdP thực; team cần chốt cookies/CSRF/redirect và policy xác thực.
- Nguồn nhãn hiệu và quyền xuất bản public phải xác nhận riêng với việc sở hữu domain.
- P0/P0*/P0-ADS/P1/P2 được ghi rõ trong API nhưng một số endpoint có thể cần điều chỉnh lại ưu tiên trong vòng sign-off.
- Khi duyệt schema, cần xóa/chỉnh những endpoint không có business owner, không sử dụng để tạo mã production vô điều kiện.

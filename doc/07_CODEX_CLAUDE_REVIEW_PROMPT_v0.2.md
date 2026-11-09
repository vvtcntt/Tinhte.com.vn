# Codex / Claude — Cross-remediation review prompt

Bạn là Principal Software Architect + Senior BA + QA Lead cho dự án **Tinhte.com.vn**, hệ thống Next.js/ASP.NET Core 10/PostgreSQL/video shorts. Hãy đọc các tài liệu nguồn 01..06 và bản cập nhật 07 v0.2 này.

## Mục tiêu review

1. Kiểm tra **84/84 FR → WBS → API → acceptance**. Với 42 nhiệm vụ con (`WBS-089..130`), tìm các phần thiếu và nêu rõ có bị **đếm trùng effort** với WBS cha 001..088 không.
2. Rà soát **OpenAPI 3.1, 93 paths / 106 operations**, `x-scope`, `x-feature-gate`, method/status/DTO, permission/RBAC, CSRF/session, idempotency, ETag/If-Match, cursor, public vs private revision, request validation và lỗi âm. Viết đề xuất nếu endpoint/domain state chưa tương thích.
3. Kiểm tra business invariant: published article/video là immutable revision; media rights; CDN takedown; bất kỳ community mutation nào phải kiểm tra backend feature gate, eligibility, content state và rate limit, kể cả khi flag vừa bị tắt giữa request.
4. Review `P0-ADS` tách biệt với `P0*` comments; sponsored content phải có nhãn rõ ở mọi đường phát hành (SSR, card, watch, sitemap, RSS nếu có), thông tin sponsor trong revision; đề xuất migration bổ sung và kế hoạch xử lý truyền dữ liệu.
5. Đối chiếu `WBS-131..135` với khoản trùng `WBS-085` (P1); tính lại capacity và chi phí. Không dùng đơn giá giả định làm báo giá thực tế.
6. Thử dựng contract test cơ bản, tập trung 401/403/409/412/422/429, comment OFF, sponsored publication OFF, unpublish media, expired rights.

## Quy tắc

- **ĐƯỢC PHÉP đề xuất:** bổ sung nghiệp vụ/FR/NFR/API/index/test, tối ưu UI/UX, bổ sung monitoring, caching, chống spam và giảm chi phí.
- **KHÔNG ĐƯỢC:** thay đổi ngầm baseline v0.1; nâng P1/P2 thành P0; tự bật cờ public; mặc định cấp phép pháp lý đã được xác nhận bằng giấy tờ; sửa file nguồn trong một PR review không có phê duyệt.
- Mọi đề xuất tạo file `07_AI_TECH_REVIEW_PROPOSALS.md`, gồm: mã đề xuất, nguồn, Severity, phân tích lỗi, phương án, FR/WBS/API/DB/UX liên quan, effort tác động, test chứng minh, owner, người duyệt, trạng thái OPEN.
- Báo cáo phần đã chứng minh bằng tool/test, phần chỉ suy luận, và phần TBD rõ ràng.

## Đầu ra bắt buộc

- `07_AI_TECH_REVIEW_PROPOSALS.md` (không tự động thay baseline).
- `07_API_CONTRACT_ISSUES.csv` (endpoint/status/test trace/owner).
- `07_WBS_REESTIMATION_DIFF.csv` (nếu đề xuất sửa effort; tránh cộng đúp).
- Báo cáo tổng hợp: Blocker/High/Medium/Low và điều kiện đóng.

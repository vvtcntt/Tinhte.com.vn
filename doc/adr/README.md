# Architecture Decision Records

| ADR | Tiêu đề | Trạng thái |
|---|---|---|
| [ADR-0001](ADR-0001-multi-subdomain-site-registry.md) | Kiến trúc đa subdomain dựa trên Site Registry | PROPOSED (Sprint 0) |
| [ADR-0002](ADR-0002-aspnetcore-controller-web-api.md) | Backend theo mô hình ASP.NET Core Web API dạng Controller | PROPOSED (Sprint 0) |
| [ADR-0003](ADR-0003-data-access-and-common-api-contracts.md) | Truy cập PostgreSQL, migration runner và hợp đồng API dùng chung | PROPOSED (Sprint 0) |
| [ADR-0004](ADR-0004-staff-auth-session-rbac.md) | Đăng nhập staff qua OIDC, phiên phía server và RBAC theo ma trận quyền | PROPOSED (Sprint 0) |
| [ADR-0005](ADR-0005-news-revisions-publish-pointer.md) | Bài viết theo revision + publish pointer; truy cập DB bằng Npgsql thuần | PROPOSED (Sprint 0) |
| [ADR-0006](ADR-0006-media-upload-storage-adapter.md) | Tải media lên qua upload intent + adapter kho lưu trữ; bản dev lưu cục bộ và giả lập xử lý; §6 duyệt/xuất bản/gỡ video + media theo revision (WBS-040) | PROPOSED (Sprint 0) |
| [ADR-0007](ADR-0007-content-sources-licenses-rights.md) | Nguồn, giấy phép và xác nhận quyền nội dung của bài viết | PROPOSED — §1 đã hiện thực (migration 0340, API + màn CMS nguồn/giấy phép, quyền `licenses.manage` tạm); §2–§4 chưa hiện thực |
| [ADR-0008](ADR-0008-news-aggregation-radar.md) | Module Aggregation: thu thập RSS và Radar tin (GĐ1, chưa có AI viết) — BA-01 | PROPOSED — đã hiện thực GĐ1 (migration 0341–0346, worker, Radar, màn CMS, vận hành); chủ dự án chốt A-1…A-5 + quy tắc quét 10/11/2026 (Radar P1, AI trùng chủ đề sau cờ riêng tắt); chờ các bên ký |
| [ADR-0009](ADR-0009-aggregation-ai-drafts.md) | Radar tin GĐ2: AI viết lại nháp có kiểm duyệt nhiều lớp — BA-01 §7.5–7.8 | PROPOSED — chỉ thiết kế + hợp đồng nháp riêng; chờ Legal, không có code |

Quy ước: mỗi quyết định kiến trúc một file `ADR-NNNN-<slug>.md`, gồm các phần Bối cảnh, Quyết định, Hệ quả và Phương án đã loại. ADR đã ACCEPTED không sửa nội dung; muốn đổi thì viết ADR mới thay thế.

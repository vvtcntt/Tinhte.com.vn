---
document_id: TINHTE-API-REMEDIATION-07
version: 0.2
status: CONTRACT_DRAFT_NOT_CODED
created_at: 2026-11-07
---

# API Contract Expansion v0.2 — Integration Rules

## Mục đích và mức hoàn thiện

- Nâng hợp đồng API tham chiếu từ **17 đường dẫn / 18 thao tác** lên **93 đường dẫn / 106 thao tác** trong `07_OPENAPI_TINHTE_EXPANDED_v0.2.yaml`.
- Bao gồm public news/shorts, CMS bài/video, quyền & kiểm duyệt, tương tác, ads tối thiểu, analytics và nhóm P1/P2 có gắn nhãn; **có endpoint trong YAML không đồng nghĩa cam kết triển khai toàn bộ MVP**.
- Bảng inventory có security/scope/feature gate/FR cho từng thao tác. **84/84 FR có ít nhất một đường dẫn API liên quan**, kể cả FR thuộc UI sẽ còn cần FE/E2E test; mapping không có nghĩa mọi FR chỉ cần đúng một API.
- Phần server URL chỉ là `.invalid` placeholder; chưa có hạ tầng thực. Đây là **contract draft**, chưa chạy server hoặc generated SDK.

## Chính sách thống nhất

1. Tất cả admin endpoints phải xác thực staff và kiểm RBAC ở service/domain. Thành viên dùng session riêng. Không dùng frontend flag thay cho backend authorization.
2. Mọi thao tác thay đổi dữ liệu bằng cookie phải có **X-CSRF-Token**, và các thao tác tạo có nguy cơ retry phải có `Idempotency-Key`. Các điểm đã có trong spec cần review sau khi chọn auth/session ADR.
3. Dùng `application/problem+json` cho lỗi; ưu tiên `400` sai format, `401` chưa đăng nhập, `403` không đủ quyền/disabled, `404` không tồn tại hoặc không public, `409` state conflict, `412` stale rowVersion, `422` validation, `429` bị rate limit.
4. `P0*` community endpoints **deny by default** nếu cờ OFF hoặc tư cách thành viên không đạt: `403` với `code = FEATURE_DISABLED` hoặc `ELIGIBILITY_REQUIRED` theo policy. Tắt cờ khi đang có phiên phải tác động tới backend transaction.
5. `P0-ADS` metadata bắt buộc hiện nhãn khi `contentKind=SPONSORED`, reject publish khi thiếu sponsor/approval. **Không phụ thuộc vào cờ cộng đồng**.
6. Public API chỉ trả revision đã duyệt và còn quyền. Không lấy nội dung private bằng ID đoán được; media/video playback grant phải kiểm rights/publish và bị thu hồi khi takedown.
7. `PlaybackGrant` đã sửa kiểu dữ liệu từ DTO có manifest bắt buộc sang `oneOf(HLS, EXTERNAL_EMBED)` để tránh lỗi với video nhúng; FE/BE cần xác nhận trước codegen.
8. Analytics batch không được đưa thông tin nhạy cảm tùy ý vào payload; kiểm soát consent, retention và độ trễ ghi theo chính sách được duyệt.
9. Quản lý sponsor/banner: tag sponsor luôn là metadata chuẩn, không cho FE điều chỉnh để ẩn nhãn; banner có kích thước để tránh layout shift.
10. Cấp production chưa được phép dựa vào file spec này; cần CI lint OpenAPI + mock contract tests + integration test + security test và role-policy matrix trước sign-off.

## Coverage theo scope và kích hoạt

| Mã scope | Định nghĩa |
|---|---|
| `P0` | MVP nền tảng, cần PO/Tech Lead phê duyệt nghiệp vụ |
| `P0*` | Bình luận, verified member và moderation; code/test song song, public có release gate |
| `P0-ADS` | Gói sponsored-lite, chi phí và legal gate độc lập P0* |
| `P1` | Mở rộng sau MVP; chưa được phát triển production trừ khi có CR |
| `P2` | AI/gợi ý nâng cao; chưa cam kết production |

## Bảng tình trạng xác thực hiện tại

- YAML parser: PASS; các `$ref` cục bộ được duyệt bằng script: PASS.
- Không trùng operationId: PASS; không có đường dẫn `{param}` chưa được khai báo: PASS.
- API→FR: 84/84 FR có liên kết ít nhất một thao tác: PASS về **mapping tĩnh**.
- Chưa chạy `aspnet`/DB/contract tests bằng server thực tế; **KHÔNG** khẳng định runtime sẵn sàng.

## Các quyết định kỹ thuật còn mở

- Chính thức hóa auth: cookie/OIDC/token và chống CSRF tương ứng.
- Chuẩn hóa nullable OpenAPI 3.1, response pagination/cursor, ETag/If-Match, idempotency retention.
- Chọn vendor media, HLS/embedding permissions, CDN purge window và privacy policy.
- Chuẩn hóa status transitions, rights management, RBAC role ids, taxonomy model.
- Khóa API cho moderation cases, sponsored campaigns (P1) sau PO/Legal review.

## Codex / Claude review

Đề xuất mới hoặc sửa hợp đồng phải tạo `07_AI_TECH_REVIEW_PROPOSALS.md`, nêu endpoint, schema, trạng thái, test, yêu cầu liên quan, tác động migration, effort và rủi ro. **Không tự động sửa file v0.1 và không bật feature flag public.**

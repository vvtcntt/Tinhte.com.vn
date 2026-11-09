---
document_id: TINHTE-PARALLEL-DELIVERY-07
version: 0.2
status: PO_DECISION_CAPTURED_WITH_PUBLIC_RELEASE_GATES
created_at: 2026-11-07
---

# Triển khai song hành — Engineering và Legal/Operations

## 1. Quyết định của chủ dự án

- Chủ dự án xác nhận **khách hàng sở hữu tên miền `tinhte.com.vn`** và đang tiến hành xin giấy phép liên quan.
- Quyết định triển khai ngay về **mặt kỹ thuật**, không chờ giấy phép: phát triển News, Shorts, CMS, account, comment/moderation, ads theo scope và feature flags.
- Các chức năng có điều kiện phải được test trong môi trường giới hạn. **Không mặc định rằng yêu cầu xin cấp phép đã đồng nghĩa với được phép vận hành công khai.** Việc xác nhận quyền sử dụng thương hiệu/nhãn hiệu, bản quyền nội dung/media vẫn cần evidence.

## 2. Phân luồng công việc

| Track | Có thể làm ngay | Chỉ bật công khai sau khi |
|---|---|---|
| News/CMS/SEO | Phát triển CRUD, workflow, quyền nguồn, SSR, SEO, staging | Nội dung/hoạt động phù hợp loại giấy phép được áp dụng, quyền nguồn đã duyệt |
| Video/Shorts | Upload, transcode, streaming, watch, cross-link, staging | Quyền media, nội dung, giấy phép phát hành, CDN takedown đã nghiệm thu |
| User/Comments | Account, eligibility adapter, comment/reply/like/report/pin/moderation, test seed | Legal + PO duyệt phạm vi action×content-type, cách xác thực và moderation |
| Sponsored/Ads | Metadata sponsor, banner slots, labels, flags, reporting tối thiểu | Legal + PO duyệt kênh/định dạng quảng cáo và thông tin tài trợ |
| AI P2 | Thiết kế adapter/PoC tùy nguồn lực | PO nâng scope + duyệt quyền dữ liệu/chi phí |

## 3. Các cấu hình bắt buộc lúc khởi tạo

```yaml
production:
  video_comments_enabled: false
  article_comments_enabled: false
  user_upload_enabled: false
  public_creator_publish_enabled: false
  sponsored_content_publication: false
  display_ad_slots: false
  shorts_instream_ads: false
```

**Lưu ý:** `false` là trạng thái **public production mặc định**, không phải cấm code hoặc cấm chạy test trong môi trường sandbox được phân quyền. Cờ bật phải có `approval_reference`, `approved_by`, `approved_at` và audit; không cấu hình bằng biến môi trường không truy vết tại runtime.

## 4. Release gate matrix

| Mốc | Điều kiện | Owner | Evidence |
|---|---|---|---|
| G1 — Build allowed | BR/SRS/UX version rõ, development environments tách biệt | PM + TL | Repo, CI, staging access policy |
| G2 — Internal test allowed | Tài khoản test, dữ liệu giả, môi trường không index công khai | QA + Security | Test plan, sandbox configuration |
| G3 — Public news/video | Xác nhận quyền khai thác & giấy phép phù hợp + bảo mật, QoE, SEO | PO + Legal + TL | Biên bản duyệt + UAT + DR/takedown |
| G4 — Public comments | Xác nhận policy action×content-type, member verification, moderation | Legal + PO + Security | Quyết định/giấy phép áp dụng, test deny-by-default |
| G5 — Public sponsored/ads | Duyệt nội dung/định dạng quảng cáo, nhãn, hợp đồng và rights | Legal + PO + Commercial | Biên bản policy + test nhãn/placement |

## 5. Điều kiện kỹ thuật an toàn

- Backend chặn trước thao tác có điều kiện; feature flag client chỉ phục vụ UX, không phải cơ chế bảo vệ.
- `404/410` phù hợp cho nội dung bị rút và các tài nguyên media liên quan; CDN purge và cấp playback grant phải đảm bảo không còn đường truy cập công khai theo thời hạn policy được duyệt.
- Tách `DRAFT/APPROVED/PUBLISHED` và `working_revision/public_revision` để duyệt nội dung mới mà không làm thay đổi nội dung đang phát hành.
- Các API có dữ liệu nhạy cảm cần RBAC/ABAC, audit, rate limit và retention; mọi test công khai phải có smoke/negative cases.
- Chỉ PO/Legal có quyền thay đổi quyết định phát hành; DevOps thực thi theo change record có thể rollback.

## 6. Vấn đề chưa giải quyết trong vòng này

- Quyền sử dụng nhãn hiệu/tên thương mại không được suy ra chỉ từ quyền sở hữu domain.
- Loại giấy phép và phạm vi được cấp, thời điểm có hiệu lực và hoạt động cho phép chưa có văn bản đính kèm.
- Chi phí WBS bản 05 vẫn đang dùng đơn giá giả định; capacity/critical path và video OPEX phải được chốt riêng.
- API v0.2/SQL v0.2 chưa chạy trên máy chủ/DB production thực; cần review, contract tests và migration tests.

---
document_id: TINHTE-REMEDIATION-07
project: Tinhte.com.vn
version: 0.2
status: PROPOSED_FOR_PO_TECH_LEGAL_REVIEW
created_at: 2026-11-07
supersedes: none
source: 01_BRD/02_SRS/03_UX/04_ARCH_API/05_WBS/06_CROSS_REVIEW v0.1
---
# 07 — Quyết định quảng cáo MVP: SPONSORED-LITE

## Đề xuất chốt

**Lựa chọn khuyến nghị:** `P0-ADS/SPONSORED-LITE` bổ sung có điều kiện vào MVP, **không gắn với P0* cộng đồng**. Bao gồm:

- Biên tập viên được đánh dấu bài/video **`EDITORIAL` / `SPONSORED`**, sponsor name, campaign reference (nếu có), chứng từ quyền, thời hạn, người duyệt.
- Gắn nhãn **“Nội dung tài trợ” / “Quảng cáo” rõ ràng** trên card, bài chi tiết, video poster/caption nơi liên quan; từ backend đến SSR, không cho tắt dấu hiệu bằng query string/theme.
- CMS cấu hình **1–2 vị trí banner cố định** (chẳng hạn cuối bài + một vị trí trong feed desktop sau số item do PO duyệt) với kích thước dự phòng để giảm CLS. Banner rỗng => ẩn container hợp lý; mặc định **không autoplay audio, không popup che nội dung**.
- Cấu hình placement theo trạng thái (`DRAFT`, `APPROVED`, `ACTIVE`, `PAUSED`, `EXPIRED`) và phân quyền quản trị. Track impression/click tối thiểu theo chính sách quyền riêng tư; không xây full ad network ở MVP.
- Có feature flags riêng: `sponsored_content_publication`, `display_ad_slots`, `shorts_instream_ads`. **Shorts in-stream OFF ở MVP**. Public activation chỉ qua chứng cứ/legal + PO phê duyệt và có rollback.

**Không nằm MVP:** Ad server đấu giá/RTB, đấu thầu programmatic, pre-roll/mid-roll Shorts, A/B quảng cáo nâng cao, profile người xem cho retargeting, campaign attribution phức tạp, thanh toán quảng cáo.

## Lý do chọn

- Cho phép mô hình thu nhập quảng cáo trực tiếp, nhưng không tăng đáng kể bề mặt kỹ thuật/pháp lý như mạng quảng cáo hay video ad serving.
- Tách sponsorship label (cần có nếu nội dung được đăng) khỏi widget/quảng cáo (có thể trì hoãn); tránh trộn chi phí P0 với P0* comments.
- Nếu chưa có sponsor, cơ chế metadata và policy vẫn có thể xây song song, **không bắt buộc hiển thị banner công khai**.

## Dự toán bổ sung (tham khảo; chưa ký duyệt)

| Hạng mục | Vai trò | PERT ngày công | Đơn giá giả định | Chi phí giả định |
|---|---|---:|---:|---:|
| `WBS-131` — Quy định sponsored labels, loại quảng cáo, vị trí và release gate | BA | 2.0 | 2,300,000 | 4,600,000 |
| `WBS-132` — Data model sponsored + placement config + publish validation + API | BE | 5.17 | 2,600,000 | 13,433,333 |
| `WBS-133` — Label sponsored dễ nhận biết và 1–2 static slots desktop/mobile | FE | 5.17 | 2,500,000 | 12,916,667 |
| `WBS-134` — E2E positive/negative, accessibility, consent/flags, rendering | QA | 3.17 | 1,900,000 | 6,016,667 |
| `WBS-135` — Feature flags, config secret và chỉ số tối thiểu | DEVOPS | 2.0 | 3,000,000 | 6,000,000 |


- **Tổng:** khoảng **17.51 ngày công**, **42.97 triệu VND nhân công**.
- **Dự phòng 18% (giả định):** **50.70 triệu VND**.
- Chưa bao gồm VAT, phí bên thứ ba, vận hành ad platform, phí hợp đồng/giấy phép, marketing và lợi nhuận nhà thầu.
- **Không cộng thêm vào Excel 05 cho đến khi PM/Finance ký bảng tái ước lượng**; có thể tái sử dụng một phần WBS-085 P1 sau khi trừ phạm vi P1 tương ứng.

## Phân tách gói sản phẩm

| Gói | Nội dung | Public gate |
|---|---|---|
| `P0` | News + Shorts + CMS + SEO + nền tảng hạ tầng | Chốt baseline thông thường |
| `P0-ADS` | Sponsored-lite, 1–2 slots, label, CMS control | Điều kiện quảng cáo / nội dung cụ thể được duyệt |
| `P0*` | Bình luận, xác thực, kiểm duyệt cộng đồng | Điều kiện mạng xã hội/hoạt động có liên quan |
| `P1` | Chiến dịch nâng cao, analytics ads, quảng cáo video | Quyết định sau khi có dữ liệu và ngân sách |

## Ràng buộc phát hành

Theo Luật sửa đổi, bổ sung Luật Quảng cáo số 75/2025/QH15 (hiệu lực 01/01/2026) và Nghị định 342/2025/NĐ-CP (hiệu lực 15/02/2026), hoạt động quảng cáo trên mạng cần tuân thủ yêu cầu nhận diện rõ và quy định áp dụng. Cần Legal rà soát hiện hành theo từng kênh/định dạng, đặc biệt quảng cáo không ở vùng cố định. Nguồn: https://vanban.chinhphu.vn/?classid=1&docid=214561&pageid=27160 ; https://chinhphu.vn/?docid=216403&pageid=27160

## Test tối thiểu

- Bài tài trợ thiếu sponsor/nhãn phải bị chặn publish; public view hiển thị nhãn rõ ở mọi breakpoint.
- Khi `sponsored_content_publication=false`, backend không cho public sponsored revision; khi `display_ad_slots=false`, response chỉ rỗng và UI không để vùng trắng lớn.
- Người dùng thiếu quyền không được đổi banner/placement; mọi sửa đổi có audit.
- CTA quảng cáo có rel/attribution phù hợp, an toàn URL và chống XSS; ảnh hỏng không làm hỏng layout.
- Bài tài trợ bị gỡ / hết quyền thì kết quả tìm kiếm, cache, ad slot cập nhật theo takedown SLA.

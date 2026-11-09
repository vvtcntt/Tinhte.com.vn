---
document_id: TINHTE-REMEDIATION-07
project: Tinhte.com.vn
version: 0.2
status: PROPOSED_FOR_PO_TECH_LEGAL_REVIEW
created_at: 2026-11-07
supersedes: none
source: 01_BRD/02_SRS/03_UX/04_ARCH_API/05_WBS/06_CROSS_REVIEW v0.1
---
# 07 — SRS × WBS × API: BẢN KHẮC PHỤC SAU CROSS-REVIEW

> **Phạm vi thay đổi:** Đây là addendum v0.2, không âm thầm sửa các tài liệu v0.1. 84 FR giữ nguyên nội dung và mức ưu tiên gốc; 42 FR trước đây chưa có *exact direct code* được bổ sung nhiệm vụ con, test và liên kết API. Lưu ý: vấn đề là **thiếu truy vết**, không phải 42 tính năng hoàn toàn chưa được yêu cầu hay chưa từng được tính effort.

## A. Kết luận nghiệp vụ đã nhận từ chủ dự án

1. Domain `tinhte.com.vn`: chủ dự án xác nhận khách hàng **sở hữu tên miền**; coi đây là thông tin do khách hàng cung cấp, lưu bằng chứng quản lý tên miền ở kho hồ sơ dự án; **chưa suy diễn quyền với nhãn hiệu `Tinhte.vn`**.
2. Chủ dự án yêu cầu **phát triển song song trong khi giấy phép đang được xin cấp**. Triển khai giao diện, nghiệp vụ, test, staging, PoC và hoàn thiện kiến trúc ngay. Không coi giả định "đủ giấy phép" là chứng nhận pháp lý để phát hành công khai những hành vi cần giấy phép.
3. Thành lập hai workstream: **Engineering (không chờ cấp phép)** và **Legal/Operations (xử lý giấy phép, quyền nội dung, policy)**. Các việc phụ thuộc cần phải release-gate, không block mọi development task.
4. `community.comments.enabled = false`, `community.ugc.enabled = false`, `ads.sponsored_publication.enabled = false` trong public production cho đến khi hoàn tất chứng cứ, duyệt của PO/Legal và rollout kiểm soát. Trong local/staging, bật dữ liệu giả/seed và kiểm thử có kiểm soát; cấm public indexing.
5. **Quy tắc xử lý yêu cầu:** quyền theo action×content-type; backend deny-by-default, không chỉ ẩn nút frontend. Có audit và kill switch server-side; public read có thể độc lập với authenticated write.

## B. Kết quả đối chiếu thật sự

- SRS: **84 FR**, 22 NFR (theo báo cáo Review 06).
- Bảng `06_TRACEABILITY_FR_WBS_v0.1.csv`: **42 EXACT_LINK** và **42 NEEDS_MAPPING** (không phải 46). Đã mở rộng thành **84/84 có WBS ID tường minh** trong `07_FR84_TRACEABILITY_v0.2.csv`.
- Tạo `WBS-089…WBS-130`: **42 nhiệm vụ con**, mỗi nhiệm vụ có scope, owner role, tiêu chí thành công/thất bại và mã test. **Không cộng nhân công lần hai**: phần lớn liên quan tới WBS-001…088 đã ước lượng ở mức cha. Chỉ sau review capacity mới xác định tăng effort thật.
- Tạo `WBS-131…135`: **gói quảng cáo MVP riêng**, có effort ba điểm, **chưa nằm trong dự toán P0 v0.1**. Nội dung P1/P2 vẫn giữ P1/P2.

## C. Điều kiện Ready/Done từng FR

- `FR` có mô tả nghiệp vụ + scope/Priority từ SRS; ít nhất một WBS chính và 1 subtask nếu trước đây thiếu exact link.
- Task có owner role, dependencies, output, acceptance case tích cực và bất lợi, mã QA test.
- Tính năng cần API có endpoint + schema/error/permission. Tính năng thuần UI phải có contract backend liên quan và test E2E. Mapping API **không thay thế** unit test/domain test.
- P0* được triển khai nội bộ nhưng **không phát hành public** khi feature gate đang OFF; P1/P2 không biến thành P0 nếu không có CR và ước lượng mới.
- Chưa phê duyệt budget do nested task: owner TL/PM phải review ước lượng effort lại theo từng epic trước baseline.

## D. 42 FR đã được cụ thể hóa

| FR | Pha SRS | Task mới | Parent hiện có | Owner | Trường hợp lỗi bắt buộc |
|---|---|---|---|---|---|
| `FR-ADS-002` | P1 hoặc P0 nếu PO phê duyệt | `WBS-089` | W, B, S, -, 0, 8, 5 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Slot không có creative hoặc đã tắt phải giữ bố cục, không render quảng cáo rỗng. |
| `FR-ADS-003` | P1 [DX] | `WBS-090` | W, B, S, -, 0, 8, 5 | DATA | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không được tính impression trùng hoặc xuất báo cáo cho role không được cấp quyền. |
| `FR-ADS-004` | P1 [DX] | `WBS-091` | W, B, S, -, 0, 8, 5 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không phát quảng cáo Shorts khi cờ video-ads OFF hoặc vượt frequency cap. |
| `FR-AI-002` | P2 [DX] | `WBS-092` | W, B, S, -, 0, 8, 7 | DATA | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Transcript chưa được biên tập viên duyệt không xuất bản như phụ đề chính thức. |
| `FR-AI-004` | P2 [DX]+[PL] | `WBS-093` | W, B, S, -, 0, 8, 8 | DATA | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Dữ liệu nhạy cảm không gửi ra model ngoài khi chưa được phép; sự kiện AI có audit. |
| `FR-ANL-002` | P0 [DX] | `WBS-094` | W, B, S, -, 0, 5, 4 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không double-count một video start khi player retry; không thu PII ngoài policy. |
| `FR-ANL-003` | P0 [DX] | `WBS-095` | W, B, S, -, 0, 5, 4 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không ghi attribution cho link ngoài/nhấp lặp không hợp lệ. |
| `FR-ANL-005` | P0 [DX] | `WBS-096` | W, B, S, -, 0, 5, 5, ;,  , W, B, S, -, 0, 5, 6 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Role không được xem analytics nhận 403; dashboard không hiển thị số liệu không rõ nguồn. |
| `FR-CMS-002` | P0 [DX] | `WBS-097` | W, B, S, -, 0, 2, 4 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Người không có quyền duyệt hoặc tự duyệt trái SoD bị 403/409. |
| `FR-CMS-004` | P0 [DX] | `WBS-098` | W, B, S, -, 0, 2, 6 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Chỉnh bản nháp không làm đổi publicRevisionId; rollback bắt buộc audit. |
| `FR-CMS-005` | P0 [DX] | `WBS-099` | W, B, S, -, 0, 2, 5, ;,  , W, B, S, -, 0, 3, 0 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Link preview hết hạn/thiếu quyền trả 403/404; preview phải noindex. |
| `FR-CMS-006` | P0 [DX] | `WBS-100` | W, B, S, -, 0, 3, 1 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Biên tập viên không nhìn thấy hồ sơ nội dung ngoài phạm vi quyền. |
| `FR-CMS-008` | P1 [DX] | `WBS-101` | W, B, S, -, 0, 4, 2 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không tự gắn nội dung AI gợi ý khi chưa xác nhận; P1 OFF không ảnh hưởng CMS. |
| `FR-CMS-009` | P0 [DX] | `WBS-102` | W, B, S, -, 0, 4, 6 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Asset private/expired/unlicensed không được gắn vào bản công khai. |
| `FR-CMS-010` | P0 [DX] | `WBS-103` | W, B, S, -, 0, 2, 4 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Từ chối/chuyển sửa thiếu lý do sẽ bị 422. |
| `FR-COM-002` | P0* [KH]+[PL] | `WBS-104` | W, B, S, -, 0, 6, 0 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Chưa bật community, tài khoản chưa đủ điều kiện, hoặc video bị gỡ phải từ chối write. |
| `FR-COM-003` | P0* [KH]+[PL] | `WBS-105` | W, B, S, -, 0, 6, 0 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không được reply comment thuộc video khác, đã gỡ hoặc khóa. |
| `FR-COM-005` | P0* [DX]+[PL] | `WBS-106` | W, B, S, -, 0, 6, 1 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không được lộ comment chưa duyệt, bị ẩn, đã gỡ; cursor không gây trùng item. |
| `FR-COM-007` | P0* [DX]+[PL] | `WBS-107` | W, B, S, -, 0, 6, 2 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Moderator thiếu quyền không được xử lý case; mọi quyết định phải có audit. |
| `FR-COM-008` | P0* [DX]+[PL] | `WBS-108` | W, B, S, -, 0, 6, 1 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Spam quá ngưỡng trả 429/hold; HTML/script không được thực thi. |
| `FR-COM-009` | P0* [PL] | `WBS-109` | W, B, S, -, 0, 6, 0 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Feature flag bị tắt giữa request phải được chặn ở transaction write. |
| `FR-COM-010` | P0* [DX]+[PL] | `WBS-110` | W, B, S, -, 0, 6, 1 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Người dùng thường không được pin; sau unpin không còn ưu tiên top. |
| `FR-COM-012` | P0* [DX]+[PL] | `WBS-111` | W, B, S, -, 0, 6, 0, ;,  , W, B, S, -, 0, 6, 3 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Retry cùng Idempotency-Key không tạo bình luận thứ hai. |
| `FR-DISC-004` | P0 [DX]+[PL] | `WBS-112` | W, B, S, -, 0, 4, 9, ;,  , W, B, S, -, 0, 3, 3 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Nội dung bị gỡ không còn xuất hiện ở feed, search, sitemap, cache sau thời hạn SLA. |
| `FR-DISC-005` | P0 [DX] | `WBS-113` | W, B, S, -, 0, 4, 2 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không hiện bài/video chưa public hoặc media đã hết quyền. |
| `FR-ID-001` | P0 [DX] | `WBS-114` | W, B, S, -, 0, 1, 5 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không tự chuyển hướng vào login khi đọc tin công khai. |
| `FR-ID-004` | P0 [DX] | `WBS-115` | W, B, S, -, 0, 1, 8 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Admin không đủ quyền không được đổi role hoặc mở khóa user. |
| `FR-ID-009` | P0* [PL] | `WBS-116` | W, B, S, -, 0, 5, 9 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không tự đồng bộ dữ liệu IdP ngoài phạm vi consent/căn cứ được phê duyệt. |
| `FR-MOD-002` | P0* [DX]+[PL] | `WBS-117` | W, B, S, -, 0, 6, 2 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không cho moderator thao tác trên case ngoài quyền; log không bị sửa âm thầm. |
| `FR-MOD-004` | P0 [PL] | `WBS-118` | W, B, S, -, 0, 3, 3, ;,  , W, B, S, -, 0, 7, 6 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Takedown phải chặn public và ghi ticket/audit; không xóa bằng chứng yêu cầu. |
| `FR-MOD-006` | P0* [PL] | `WBS-119` | W, B, S, -, 0, 6, 2 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không vượt giới hạn khi suspended; rate limit có log, hạn chế false positives. |
| `FR-MOD-007` | P1 [DX]+[PL] | `WBS-120` | W, B, S, -, 0, 8, 3 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Appeal không mở nếu P1 chưa bật; không tự công khai nội dung đang bị gỡ. |
| `FR-NEWS-002` | P0 [DX] | `WBS-121` | W, B, S, -, 0, 2, 2, ;,  , W, B, S, -, 0, 3, 4 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Slug trùng không được chiếm URL; đổi slug phải có redirect/canonical thống nhất. |
| `FR-NEWS-004` | P0 [DX] | `WBS-122` | W, B, S, -, 0, 2, 2, ;,  , W, B, S, -, 0, 2, 4 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Chuyển trạng thái trái policy/thiếu quyền phải 409/403. |
| `FR-NEWS-005` | P0 [DX] | `WBS-123` | W, B, S, -, 0, 2, 2, ;,  , W, B, S, -, 0, 2, 6 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Draft revision không thay đổi phiên bản công khai cho đến khi đủ duyệt. |
| `FR-NEWS-006` | P0 [PL] | `WBS-124` | W, B, S, -, 0, 2, 3 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Bài thiếu metadata rights/source không được publish. |
| `FR-NEWS-008` | P0* [PL] | `WBS-125` | W, B, S, -, 0, 8, 5 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Bài sponsored thiếu nhãn sẽ bị chặn publish, ngay cả khi editor cố bỏ nhãn. |
| `FR-NEWS-009` | P1 [DX] | `WBS-126` | W, B, S, -, 0, 5, 1 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không lộ series P1 chưa bật hoặc nội dung unpublish. |
| `FR-VID-004` | P0 [DX] | `WBS-127` | W, B, S, -, 0, 4, 4 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Clip đổi bằng back/keyboard không làm mất deep-link hoặc phát 2 clip đồng thời. |
| `FR-VID-010` | P0 [DX] | `WBS-128` | W, B, S, -, 0, 3, 9, ;,  , W, B, S, -, 0, 4, 0 | BE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Takedown clip phải chặn cấp playback grant mới và vô hiệu cache/feeds. |
| `FR-VID-011` | P0 [DX] | `WBS-129` | W, B, S, -, 0, 4, 5 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Autoplay bị browser chặn phải có affordance play; keyboard vẫn hoạt động. |
| `FR-VID-012` | P0 [DX] | `WBS-130` | W, B, S, -, 0, 4, 5 | FE | Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không tải trước vô hạn; clip không active phải pause và giải phóng resource. |

## E. Kiểm thử Given/When/Then — 42 yêu cầu bổ sung

> QA phải có **ít nhất hai trường hợp** cho mỗi FR dưới đây; tiêu chí lỗi dạng văn bản cần được diễn giải lại thành test tự động thực thi. Nên mở ticket `TC-FR-...` trong công cụ quản lý dự án.

### FR-ADS-002 — P1 hoặc P0 nếu PO phê duyệt
**Nghiệp vụ:** Cho phép quản lý vị trí/quy tắc hiển thị quảng cáo nhưng không phá layout/trải nghiệm video.  
**Thực hiện:** `WBS-089` (parent: WBS-085; owner: FE).  
**API:** `GET /ads/placements; GET /admin/ad-placements; PUT /admin/ad-placements/{placementId}`.  
**Positive `TC-FR-ADS-002-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Cho phép quản lý vị trí/quy tắc hiển thị quảng cáo nhưng không phá layout/trải nghiệm video; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-ADS-002-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Slot không có creative hoặc đã tắt phải giữ bố cục, không render quảng cáo rỗng.

### FR-ADS-003 — P1 [DX]
**Nghiệp vụ:** Báo cáo số liệu chiến dịch theo quyền, định nghĩa metric và căn cứ thu thập dữ liệu.  
**Thực hiện:** `WBS-090` (parent: WBS-085; owner: DATA).  
**API:** `GET /admin/ads/reports`.  
**Positive `TC-FR-ADS-003-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Báo cáo số liệu chiến dịch theo quyền, định nghĩa metric và căn cứ thu thập dữ liệu; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-ADS-003-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không được tính impression trùng hoặc xuất báo cáo cho role không được cấp quyền.

### FR-ADS-004 — P1 [DX]
**Nghiệp vụ:** Nếu triển khai video ads: frequency capping, trạng thái quảng cáo và đánh giá UX.  
**Thực hiện:** `WBS-091` (parent: WBS-085; owner: FE).  
**API:** `GET /site/config; GET /ads/placements`.  
**Positive `TC-FR-ADS-004-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Nếu triển khai video ads: frequency capping, trạng thái quảng cáo và đánh giá UX; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-ADS-004-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không phát quảng cáo Shorts khi cờ video-ads OFF hoặc vượt frequency cap.

### FR-AI-002 — P2 [DX]
**Nghiệp vụ:** Tạo transcript/phụ đề nháp, cho sửa và kiểm tra trước công khai  
**Thực hiện:** `WBS-092` (parent: WBS-087; owner: DATA).  
**API:** `POST /admin/ai/transcript-jobs; PUT /admin/videos/{videoId}/subtitles`.  
**Positive `TC-FR-AI-002-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Tạo transcript/phụ đề nháp, cho sửa và kiểm tra trước công khai; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-AI-002-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Transcript chưa được biên tập viên duyệt không xuất bản như phụ đề chính thức.

### FR-AI-004 — P2 [DX]+[PL]
**Nghiệp vụ:** Lưu dấu vết sử dụng AI, cấu hình quyền và kiểm soát thông tin nhạy cảm  
**Thực hiện:** `WBS-093` (parent: WBS-088; owner: DATA).  
**API:** `GET /admin/ai/audit`.  
**Positive `TC-FR-AI-004-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Lưu dấu vết sử dụng AI, cấu hình quyền và kiểm soát thông tin nhạy cảm; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-AI-004-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Dữ liệu nhạy cảm không gửi ra model ngoài khi chưa được phép; sự kiện AI có audit.

### FR-ANL-002 — P0 [DX]
**Nghiệp vụ:** Theo dõi video impression/start/progress/complete, startup failure, buffering, player error theo policy.  
**Thực hiện:** `WBS-094` (parent: WBS-054; owner: FE).  
**API:** `POST /analytics/events`.  
**Positive `TC-FR-ANL-002-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Theo dõi video impression/start/progress/complete, startup failure, buffering, player error theo policy; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-ANL-002-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không double-count một video start khi player retry; không thu PII ngoài policy.

### FR-ANL-003 — P0 [DX]
**Nghiệp vụ:** Ghi click hai chiều video → bài và bài → video, attribution theo session/context hợp lệ.  
**Thực hiện:** `WBS-095` (parent: WBS-054; owner: FE).  
**API:** `POST /analytics/events`.  
**Positive `TC-FR-ANL-003-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Ghi click hai chiều video → bài và bài → video, attribution theo session/context hợp lệ; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-ANL-003-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không ghi attribution cho link ngoài/nhấp lặp không hợp lệ.

### FR-ANL-005 — P0 [DX]
**Nghiệp vụ:** Dashboard tối thiểu hiển thị số liệu tổng hợp theo khoảng thời gian, loại nội dung và nguồn dữ liệu; có định nghĩa chỉ số.  
**Thực hiện:** `WBS-096` (parent: WBS-055; WBS-056; owner: FE).  
**API:** `GET /admin/analytics/overview`.  
**Positive `TC-FR-ANL-005-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Dashboard tối thiểu hiển thị số liệu tổng hợp theo khoảng thời gian, loại nội dung và nguồn dữ liệu; có định nghĩa chỉ số; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-ANL-005-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Role không được xem analytics nhận 403; dashboard không hiển thị số liệu không rõ nguồn.

### FR-CMS-002 — P0 [DX]
**Nghiệp vụ:** Reviewer có thể trả về để sửa kèm lý do; approver có thể duyệt/từ chối theo role.  
**Thực hiện:** `WBS-097` (parent: WBS-024; owner: BE).  
**API:** `POST /admin/articles/{articleId}/request-changes; POST /admin/articles/{articleId}/reject; POST /admin/articles/{articleId}/approve`.  
**Positive `TC-FR-CMS-002-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Reviewer có thể trả về để sửa kèm lý do; approver có thể duyệt/từ chối theo role; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-CMS-002-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Người không có quyền duyệt hoặc tự duyệt trái SoD bị 403/409.

### FR-CMS-004 — P0 [DX]
**Nghiệp vụ:** Lưu revision và audit cho publish/update/unpublish/rollback; đối chiếu phiên bản trước/sau.  
**Thực hiện:** `WBS-098` (parent: WBS-026; owner: BE).  
**API:** `GET /admin/articles/{articleId}/revisions; POST /admin/articles/{articleId}/rollback`.  
**Positive `TC-FR-CMS-004-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Lưu revision và audit cho publish/update/unpublish/rollback; đối chiếu phiên bản trước/sau; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-CMS-004-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Chỉnh bản nháp không làm đổi publicRevisionId; rollback bắt buộc audit.

### FR-CMS-005 — P0 [DX]
**Nghiệp vụ:** Preview nội dung desktop/mobile trước publish; link preview có thời hạn/kiểm quyền/noindex.  
**Thực hiện:** `WBS-099` (parent: WBS-025; WBS-030; owner: FE).  
**API:** `GET /admin/articles/{articleId}/preview`.  
**Positive `TC-FR-CMS-005-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Preview nội dung desktop/mobile trước publish; link preview có thời hạn/kiểm quyền/noindex; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-CMS-005-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Link preview hết hạn/thiếu quyền trả 403/404; preview phải noindex.

### FR-CMS-006 — P0 [DX]
**Nghiệp vụ:** Danh sách bài/clip theo trạng thái, owner, người duyệt, thời gian, lọc/tìm/điều hướng tới chi tiết.  
**Thực hiện:** `WBS-100` (parent: WBS-031; owner: FE).  
**API:** `GET /admin/articles; GET /admin/work-queue`.  
**Positive `TC-FR-CMS-006-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Danh sách bài/clip theo trạng thái, owner, người duyệt, thời gian, lọc/tìm/điều hướng tới chi tiết; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-CMS-006-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Biên tập viên không nhìn thấy hồ sơ nội dung ngoài phạm vi quyền.

### FR-CMS-008 — P1 [DX]
**Nghiệp vụ:** Gợi ý bài/video/tag liên quan có khả năng editor chấp thuận hoặc bỏ qua.  
**Thực hiện:** `WBS-101` (parent: WBS-042; owner: BE).  
**API:** `GET /admin/articles/{articleId}/related-suggestions`.  
**Positive `TC-FR-CMS-008-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Gợi ý bài/video/tag liên quan có khả năng editor chấp thuận hoặc bỏ qua; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-CMS-008-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không tự gắn nội dung AI gợi ý khi chưa xác nhận; P1 OFF không ảnh hưởng CMS.

### FR-CMS-009 — P0 [DX]
**Nghiệp vụ:** Media library liệt kê asset và quyền, trạng thái private/public; chỉ gắn asset phù hợp với nội dung được phép.  
**Thực hiện:** `WBS-102` (parent: WBS-046; owner: FE).  
**API:** `GET /admin/media; GET /admin/media/{mediaId}`.  
**Positive `TC-FR-CMS-009-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Media library liệt kê asset và quyền, trạng thái private/public; chỉ gắn asset phù hợp với nội dung được phép; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-CMS-009-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Asset private/expired/unlicensed không được gắn vào bản công khai.

### FR-CMS-010 — P0 [DX]
**Nghiệp vụ:** Ghi và hiển thị reason khi reject/request changes, có hiển thị rõ người phụ trách tiếp theo.  
**Thực hiện:** `WBS-103` (parent: WBS-024; owner: BE).  
**API:** `POST /admin/articles/{articleId}/request-changes`.  
**Positive `TC-FR-CMS-010-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Ghi và hiển thị reason khi reject/request changes, có hiển thị rõ người phụ trách tiếp theo; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-CMS-010-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Từ chối/chuyển sửa thiếu lý do sẽ bị 422.

### FR-COM-002 — P0* [KH]+[PL]
**Nghiệp vụ:** User đủ điều kiện gửi text comment trên video cho phép; validate nội dung, xác thực, rate limit, kiểm tra trạng thái video tại thời điểm ghi.  
**Thực hiện:** `WBS-104` (parent: WBS-060; owner: BE).  
**API:** `POST /videos/{videoId}/comments`.  
**Positive `TC-FR-COM-002-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện User đủ điều kiện gửi text comment trên video cho phép; validate nội dung, xác thực, rate limit, kiểm tra trạng thái video tại thời điểm ghi; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-COM-002-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Chưa bật community, tài khoản chưa đủ điều kiện, hoặc video bị gỡ phải từ chối write.

### FR-COM-003 — P0* [KH]+[PL]
**Nghiệp vụ:** Reply vào bình luận thuộc cùng video, không bị gỡ/khóa; kiểm tra parent còn hợp lệ.  
**Thực hiện:** `WBS-105` (parent: WBS-060; owner: BE).  
**API:** `POST /comments/{commentId}/replies`.  
**Positive `TC-FR-COM-003-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Reply vào bình luận thuộc cùng video, không bị gỡ/khóa; kiểm tra parent còn hợp lệ; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-COM-003-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không được reply comment thuộc video khác, đã gỡ hoặc khóa.

### FR-COM-005 — P0* [DX]+[PL]
**Nghiệp vụ:** Danh sách comment có phân trang ổn định, thứ tự `newest` và `top` nếu được bật, ưu tiên pinned theo policy.  
**Thực hiện:** `WBS-106` (parent: WBS-061; owner: BE).  
**API:** `GET /videos/{videoId}/comments`.  
**Positive `TC-FR-COM-005-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Danh sách comment có phân trang ổn định, thứ tự `newest` và `top` nếu được bật, ưu tiên pinned theo policy; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-COM-005-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không được lộ comment chưa duyệt, bị ẩn, đã gỡ; cursor không gây trùng item.

### FR-COM-007 — P0* [DX]+[PL]
**Nghiệp vụ:** Moderator ẩn/gỡ/phê duyệt/khôi phục/khóa comment theo workflow, có reason/audit, kiểm tra quyền.  
**Thực hiện:** `WBS-107` (parent: WBS-062; owner: BE).  
**API:** `POST /admin/moderation/cases/{caseId}/actions`.  
**Positive `TC-FR-COM-007-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Moderator ẩn/gỡ/phê duyệt/khôi phục/khóa comment theo workflow, có reason/audit, kiểm tra quyền; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-COM-007-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Moderator thiếu quyền không được xử lý case; mọi quyết định phải có audit.

### FR-COM-008 — P0* [DX]+[PL]
**Nghiệp vụ:** Áp dụng spam controls: rate limiting, lọc liên kết nguy hiểm/HTML, rule blocking và moderation hold theo policy.  
**Thực hiện:** `WBS-108` (parent: WBS-061; owner: BE).  
**API:** `POST /videos/{videoId}/comments`.  
**Positive `TC-FR-COM-008-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Áp dụng spam controls: rate limiting, lọc liên kết nguy hiểm/HTML, rule blocking và moderation hold theo policy; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-COM-008-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Spam quá ngưỡng trả 429/hold; HTML/script không được thực thi.

### FR-COM-009 — P0* [PL]
**Nghiệp vụ:** Việc chuyển trạng thái video/bình luận/eligibility phải khiến tất cả hành động không còn hợp lệ bị chặn ngay.  
**Thực hiện:** `WBS-109` (parent: WBS-060; owner: BE).  
**API:** `GET /site/config; POST /videos/{videoId}/comments`.  
**Positive `TC-FR-COM-009-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Việc chuyển trạng thái video/bình luận/eligibility phải khiến tất cả hành động không còn hợp lệ bị chặn ngay; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-COM-009-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Feature flag bị tắt giữa request phải được chặn ở transaction write.

### FR-COM-010 — P0* [DX]+[PL]
**Nghiệp vụ:** Ghim/bỏ ghim comment chỉ thuộc role được ủy quyền, có log; tối thiểu hỗ trợ một bình luận ghim nếu tính năng được chốt.  
**Thực hiện:** `WBS-110` (parent: WBS-061; owner: BE).  
**API:** `PUT /admin/comments/{commentId}/pin; DELETE /admin/comments/{commentId}/pin`.  
**Positive `TC-FR-COM-010-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Ghim/bỏ ghim comment chỉ thuộc role được ủy quyền, có log; tối thiểu hỗ trợ một bình luận ghim nếu tính năng được chốt; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-COM-010-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Người dùng thường không được pin; sau unpin không còn ưu tiên top.

### FR-COM-012 — P0* [DX]+[PL]
**Nghiệp vụ:** Hiển thị trạng thái đang gửi, thành công/chờ duyệt/thất bại; tránh đăng trùng khi click nhiều lần.  
**Thực hiện:** `WBS-111` (parent: WBS-060; WBS-063; owner: FE).  
**API:** `POST /videos/{videoId}/comments`.  
**Positive `TC-FR-COM-012-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Hiển thị trạng thái đang gửi, thành công/chờ duyệt/thất bại; tránh đăng trùng khi click nhiều lần; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-COM-012-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Retry cùng Idempotency-Key không tạo bình luận thứ hai.

### FR-DISC-004 — P0 [DX]+[PL]
**Nghiệp vụ:** Khi bài/clip bị gỡ/hết quyền, kết quả search, gợi ý, sitemap, feed và cache bị đồng bộ loại bỏ theo quy trình.  
**Thực hiện:** `WBS-112` (parent: WBS-049; WBS-033; owner: BE).  
**API:** `GET /search; GET /videos; GET /articles`.  
**Positive `TC-FR-DISC-004-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Khi bài/clip bị gỡ/hết quyền, kết quả search, gợi ý, sitemap, feed và cache bị đồng bộ loại bỏ theo quy trình; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-DISC-004-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Nội dung bị gỡ không còn xuất hiện ở feed, search, sitemap, cache sau thời hạn SLA.

### FR-DISC-005 — P0 [DX]
**Nghiệp vụ:** Quan hệ many-to-many giữa bài và video, ưu tiên hiển thị các liên kết có nội dung đang public.  
**Thực hiện:** `WBS-113` (parent: WBS-042; owner: BE).  
**API:** `GET /articles/{slug}/related-videos; GET /videos/{videoId}/related-articles`.  
**Positive `TC-FR-DISC-005-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Quan hệ many-to-many giữa bài và video, ưu tiên hiển thị các liên kết có nội dung đang public; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-DISC-005-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không hiện bài/video chưa public hoặc media đã hết quyền.

### FR-ID-001 — P0 [DX]
**Nghiệp vụ:** Người chưa đăng nhập đọc/xem nội dung public mà không bị ép tạo tài khoản (trừ trường hợp được phép chặn theo policy đã duyệt).  
**Thực hiện:** `WBS-114` (parent: WBS-015; owner: FE).  
**API:** `GET /articles; GET /videos`.  
**Positive `TC-FR-ID-001-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Người chưa đăng nhập đọc/xem nội dung public mà không bị ép tạo tài khoản (trừ trường hợp được phép chặn theo policy đã duyệt); Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-ID-001-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không tự chuyển hướng vào login khi đọc tin công khai.

### FR-ID-004 — P0 [DX]
**Nghiệp vụ:** Admin được phân quyền có thể suspend/reactivate user, thu hồi role/phiên, ghi reason/audit.  
**Thực hiện:** `WBS-115` (parent: WBS-018; owner: BE).  
**API:** `POST /admin/users/{userId}/suspend; POST /admin/users/{userId}/reactivate; PUT /admin/users/{userId}/roles`.  
**Positive `TC-FR-ID-004-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Admin được phân quyền có thể suspend/reactivate user, thu hồi role/phiên, ghi reason/audit; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-ID-004-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Admin không đủ quyền không được đổi role hoặc mở khóa user.

### FR-ID-009 — P0* [PL]
**Nghiệp vụ:** Nếu có external identity provider, thông báo và phạm vi dữ liệu đồng bộ phải được duyệt; account linking phải chống takeover.  
**Thực hiện:** `WBS-116` (parent: WBS-059; owner: BE).  
**API:** `POST /auth/member/verification-requests`.  
**Positive `TC-FR-ID-009-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Nếu có external identity provider, thông báo và phạm vi dữ liệu đồng bộ phải được duyệt; account linking phải chống takeover; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-ID-009-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không tự đồng bộ dữ liệu IdP ngoài phạm vi consent/căn cứ được phê duyệt.

### FR-MOD-002 — P0* [DX]+[PL]
**Nghiệp vụ:** Moderator được quyền xem ngữ cảnh cần thiết, ra quyết định hide/remove/restore/reject với reason, audit và kiểm tra quyền.  
**Thực hiện:** `WBS-117` (parent: WBS-062; owner: BE).  
**API:** `POST /admin/moderation/cases/{caseId}/actions`.  
**Positive `TC-FR-MOD-002-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Moderator được quyền xem ngữ cảnh cần thiết, ra quyết định hide/remove/restore/reject với reason, audit và kiểm tra quyền; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-MOD-002-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không cho moderator thao tác trên case ngoài quyền; log không bị sửa âm thầm.

### FR-MOD-004 — P0 [PL]
**Nghiệp vụ:** Tiếp nhận và theo dõi khiếu nại về bản quyền, quyền cá nhân, nội dung, yêu cầu hợp lệ từ cơ quan quản lý theo workflow duyệt.  
**Thực hiện:** `WBS-118` (parent: WBS-033; WBS-076; owner: BE).  
**API:** `POST /admin/takedown-requests; GET /admin/takedown-requests`.  
**Positive `TC-FR-MOD-004-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Tiếp nhận và theo dõi khiếu nại về bản quyền, quyền cá nhân, nội dung, yêu cầu hợp lệ từ cơ quan quản lý theo workflow duyệt; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-MOD-004-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Takedown phải chặn public và ghi ticket/audit; không xóa bằng chứng yêu cầu.

### FR-MOD-006 — P0* [PL]
**Nghiệp vụ:** Phát hiện rate abuse, tài khoản vi phạm và chặn tương tác theo rule được duyệt.  
**Thực hiện:** `WBS-119` (parent: WBS-062; owner: BE).  
**API:** `POST /admin/users/{userId}/suspend`.  
**Positive `TC-FR-MOD-006-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Phát hiện rate abuse, tài khoản vi phạm và chặn tương tác theo rule được duyệt; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-MOD-006-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không vượt giới hạn khi suspended; rate limit có log, hạn chế false positives.

### FR-MOD-007 — P1 [DX]+[PL]
**Nghiệp vụ:** Kháng nghị/appeal và thông báo xử lý nâng cao nếu policy yêu cầu.  
**Thực hiện:** `WBS-120` (parent: WBS-083; owner: BE).  
**API:** `POST /me/appeals`.  
**Positive `TC-FR-MOD-007-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Kháng nghị/appeal và thông báo xử lý nâng cao nếu policy yêu cầu; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-MOD-007-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Appeal không mở nếu P1 chưa bật; không tự công khai nội dung đang bị gỡ.

### FR-NEWS-002 — P0 [DX]
**Nghiệp vụ:** Mỗi bài có ID và URL ổn định/canonical; khi đổi slug có phương án redirect được chấp thuận.  
**Thực hiện:** `WBS-121` (parent: WBS-022; WBS-034; owner: BE).  
**API:** `GET /articles/{slug}; PATCH /admin/articles/{articleId}`.  
**Positive `TC-FR-NEWS-002-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Mỗi bài có ID và URL ổn định/canonical; khi đổi slug có phương án redirect được chấp thuận; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-NEWS-002-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Slug trùng không được chiếm URL; đổi slug phải có redirect/canonical thống nhất.

### FR-NEWS-004 — P0 [DX]
**Nghiệp vụ:** Bài có vòng đời draft/review/approval/scheduled/published/retracted; kiểm soát transition theo role.  
**Thực hiện:** `WBS-122` (parent: WBS-022; WBS-024; owner: BE).  
**API:** `POST /admin/articles/{articleId}/submit; POST /admin/articles/{articleId}/approve`.  
**Positive `TC-FR-NEWS-004-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Bài có vòng đời draft/review/approval/scheduled/published/retracted; kiểm soát transition theo role; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-NEWS-004-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Chuyển trạng thái trái policy/thiếu quyền phải 409/403.

### FR-NEWS-005 — P0 [DX]
**Nghiệp vụ:** Sửa bài đã công bố bằng revision; không tự thay thế public trước khi đủ điều kiện; lưu người sửa, thời điểm, lý do và nội dung thay đổi.  
**Thực hiện:** `WBS-123` (parent: WBS-022; WBS-026; owner: BE).  
**API:** `GET /admin/articles/{articleId}/revisions`.  
**Positive `TC-FR-NEWS-005-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Sửa bài đã công bố bằng revision; không tự thay thế public trước khi đủ điều kiện; lưu người sửa, thời điểm, lý do và nội dung thay đổi; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-NEWS-005-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Draft revision không thay đổi phiên bản công khai cho đến khi đủ duyệt.

### FR-NEWS-006 — P0 [PL]
**Nghiệp vụ:** Bản tin có metadata nguồn/giấy phép/phạm vi sử dụng; kiểm tra policy trước publish.  
**Thực hiện:** `WBS-124` (parent: WBS-023; owner: BE).  
**API:** `PATCH /admin/articles/{articleId}`.  
**Positive `TC-FR-NEWS-006-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Bản tin có metadata nguồn/giấy phép/phạm vi sử dụng; kiểm tra policy trước publish; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-NEWS-006-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Bài thiếu metadata rights/source không được publish.

### FR-NEWS-008 — P0* [PL]
**Nghiệp vụ:** Bài tài trợ/nội dung hợp tác có trường đánh dấu và nhãn hiển thị rõ khi được sử dụng.  
**Thực hiện:** `WBS-125` (parent: WBS-085; owner: BE).  
**API:** `PATCH /admin/articles/{articleId}; GET /articles/{slug}`.  
**Positive `TC-FR-NEWS-008-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Bài tài trợ/nội dung hợp tác có trường đánh dấu và nhãn hiển thị rõ khi được sử dụng; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-NEWS-008-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Bài sponsored thiếu nhãn sẽ bị chặn publish, ngay cả khi editor cố bỏ nhãn.

### FR-NEWS-009 — P1 [DX]
**Nghiệp vụ:** Tổ chức tuyến bài/series, thứ tự nội dung và trang tổng hợp.  
**Thực hiện:** `WBS-126` (parent: WBS-051; owner: BE).  
**API:** `GET /series/{slug}`.  
**Positive `TC-FR-NEWS-009-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Tổ chức tuyến bài/series, thứ tự nội dung và trang tổng hợp; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-NEWS-009-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không lộ series P1 chưa bật hoặc nội dung unpublish.

### FR-VID-004 — P0 [DX]
**Nghiệp vụ:** Chuyển video không mất vị trí quá trình duyệt; URL/watch state đồng bộ theo thiết kế UX; xử lý browser Back.  
**Thực hiện:** `WBS-127` (parent: WBS-044; owner: FE).  
**API:** `GET /videos/{videoId}`.  
**Positive `TC-FR-VID-004-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Chuyển video không mất vị trí quá trình duyệt; URL/watch state đồng bộ theo thiết kế UX; xử lý browser Back; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-VID-004-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Clip đổi bằng back/keyboard không làm mất deep-link hoặc phát 2 clip đồng thời.

### FR-VID-010 — P0 [DX]
**Nghiệp vụ:** Gỡ/unpublish video phải vô hiệu watch public, loại khỏi feed/search và xử lý vô hiệu hóa cache/media delivery theo policy.  
**Thực hiện:** `WBS-128` (parent: WBS-039; WBS-040; owner: BE).  
**API:** `POST /admin/videos/{videoId}/unpublish`.  
**Positive `TC-FR-VID-010-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Gỡ/unpublish video phải vô hiệu watch public, loại khỏi feed/search và xử lý vô hiệu hóa cache/media delivery theo policy; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-VID-010-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Takedown clip phải chặn cấp playback grant mới và vô hiệu cache/feeds.

### FR-VID-011 — P0 [DX]
**Nghiệp vụ:** Điều khiển touch/keyboard/mouse; điều kiện autoplay tuân thủ browser; khi browser cấm autoplay có nút phát rõ ràng.  
**Thực hiện:** `WBS-129` (parent: WBS-045; owner: FE).  
**API:** `GET /videos/{videoId}/playback`.  
**Positive `TC-FR-VID-011-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Điều khiển touch/keyboard/mouse; điều kiện autoplay tuân thủ browser; khi browser cấm autoplay có nút phát rõ ràng; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-VID-011-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Autoplay bị browser chặn phải có affordance play; keyboard vẫn hoạt động.

### FR-VID-012 — P0 [DX]
**Nghiệp vụ:** Chỉ tải trước giới hạn số clip theo cấu hình, pause video không còn active, giải phóng tài nguyên; ưu tiên mạng yếu/save-data.  
**Thực hiện:** `WBS-130` (parent: WBS-045; owner: FE).  
**API:** `GET /videos`.  
**Positive `TC-FR-VID-012-POS`:** Given dữ liệu và quyền hợp lệ; When thực hiện Chỉ tải trước giới hạn số clip theo cấu hình, pause video không còn active, giải phóng tài nguyên; ưu tiên mạng yếu/save-data; Then trạng thái/đầu ra đúng SRS, lưu traceId và không phá dữ liệu công khai.  
**Negative `TC-FR-VID-012-NEG`:** Given điều kiện lỗi/quyền/cấu hình bất lợi; When tác vụ được yêu cầu; Then Không tải trước vô hạn; clip không active phải pause và giải phóng resource.

## F. Phân loại không triển khai ngay

- `FR-ADS-003`, `FR-ADS-004`: P1; báo cáo chiến dịch sâu và quảng cáo xen Shorts vẫn để về sau.
- `FR-AI-*`: P2; có thể dựng abstraction/PoC nhưng không mặc định chi phí build production.
- `FR-COM-*` P0*: xây dựng và kiểm thử ngay nhưng **public flag OFF** khi chưa có release approval.
- `FR-NEWS-008` và `FR-ADS-001`: gói quảng cáo **tách riêng P0-ADS** để có thể duyệt độc lập với P0* cộng đồng.

## G. Cập nhật công việc/dự toán bắt buộc sau khi nhận file

1. BA kiểm tra 84 mapping; Tech Lead review 42 nhiệm vụ con; QA viết test case có ID.
2. Backend + FE cùng chốt OpenAPI v0.2 (file bên cạnh), khóa domain invariants và auth behavior.
3. PM chọn `P0`, `P0 + P0-ADS`, `P0 + P0*`, hay `P0 + P0-ADS + P0*`; cập nhật Excel 05 **qua bản mới có version** sau sign-off, không cộng cả parent và child effort.
4. Đối chiếu SRS/UI với schema PostgreSQL 04: field sponsor, ads placement, audit và content-rights có thể cần migration addendum riêng.
5. Codex/Claude được đề xuất tối ưu, tạo ticket và test; **không tự ý nâng ưu tiên, sửa phạm vi/baseline đã duyệt hoặc mở public flags**.

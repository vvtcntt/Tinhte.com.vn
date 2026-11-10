# BA-01 — Nghiệp vụ cài đặt nguồn thu thập cho Radar tin — v0.1

**Trạng thái:** DRAFT, 10/11/2026. Phục vụ câu E-02/L-01 trong `doc/ba/BA-01_PHIEU_XIN_QUYET_DINH_v0.1_DRAFT.md`.
**Áp dụng cho:** GĐ1 (Radar tin), nguồn ở chế độ **M0** hoặc **M1**. Không dùng cho M2/M3.
**Màn hình CMS** (đã có trên nhánh `claude/agg-radar-slice2-029e01`, chưa merge):
- **Nguồn & giấy phép** (`/catalog`): danh mục nguồn dùng chung của cả hệ thống.
- **Nguồn thu thập** (`/aggregation`): hồ sơ thu thập, feed, thử feed, duyệt.
- **Luật chuyên mục** (`/aggregation/rules`): từ khóa phân loại tin vào chuyên mục.

> Tài liệu không phải ý kiến pháp lý. Mọi kết luận về điều khoản của nguồn do Legal chốt.

---

## 1. Phát hiện quan trọng về điều khoản RSS (kiểm ngày 10/11/2026)

Trang RSS của **VnExpress**, **Dân trí** và **Tuổi Trẻ** có cùng một điều khoản sử dụng, đại ý:
- kênh RSS **miễn phí cho cá nhân và tổ chức phi lợi nhuận**;
- người dùng phải ghi rõ thông tin nguồn;
- báo có quyền yêu cầu ngừng dùng bất kỳ lúc nào.

Tinhte là đơn vị kinh doanh, nên việc dùng RSS của các báo này **có thể không nằm trong phạm vi miễn phí** đó, kể cả khi chỉ dùng nội bộ ở M0.

**Đề xuất (chờ Legal chốt):**
- (a) **Khuyến nghị:** gửi email xin phép báo trước khi bật, nói rõ mục đích "chỉ đọc tiêu đề và link để BTV theo dõi nội bộ, không đăng lại". Có thư đồng ý thì lưu làm bằng chứng.
- (b) Chạy thí điểm M0 nội bộ trong lúc chờ trả lời. Rủi ro do Legal đánh giá.
- (c) Thí điểm trước với các nguồn không có điều khoản này (GenK, Thanh Niên: chưa thấy điều khoản trên trang RSS; vẫn cần Legal xem điều khoản chung của site).

**Chốt (chủ dự án, 10/11/2026): phương án (a)** — xin phép VnExpress, Dân trí, Tuổi Trẻ trước khi bật.
- Ba nguồn này chỉ được duyệt sau khi có thư đồng ý; lưu thư vào *Bằng chứng điều khoản*.
- Trong lúc chờ, thí điểm có thể bắt đầu với nguồn không có điều khoản này (Thanh Niên, GenK), sau khi Legal xem điều khoản chung của hai site đó.

Dù chọn phương án nào: **chụp lại trang điều khoản** lúc duyệt và lưu đường dẫn ảnh vào ô *Bằng chứng điều khoản*. Khi báo yêu cầu ngừng, chuyển nguồn sang **Ngừng** ngay (mục 3, bước 8).

---

## 2. Ai làm gì (tòa soạn 2 người)

Ban đầu chỉ có 2 thành viên quản lý. **Chốt (chủ dự án, 10/11/2026): cả 2 người đều có thể đề xuất và duyệt, nếu được phân quyền.**

| Người | Vai trò cần cấp trong CMS | Làm được |
|---|---|---|
| Thành viên 1 | EDITOR + APPROVER (bật MFA) | Mọi bước 1–8 |
| Thành viên 2 | EDITOR + APPROVER (bật MFA) | Mọi bước 1–8 |

- Cấp vai trò ở màn quản lý người dùng (quyền `users.manage`). Không cần sửa ma trận quyền.
- Mỗi bước dưới đây ghi "Người A" là **người làm**, "Người B" là **người duyệt**. Hai vai có thể là cùng một người hoặc hai người khác nhau:
  - **M0/M1:** hệ thống cho phép người tạo nguồn tự duyệt. Nên để người kia duyệt khi có thể, để có thêm một người kiểm.
  - **M2/M3** (chưa dùng ở GĐ1): hệ thống **bắt buộc** người duyệt khác người đề xuất. Với 2 người thì vẫn làm được: người này đề xuất, người kia duyệt.
- Email cảnh báo gửi cho cả 2 người.

---

## 3. Quy trình cài đặt một nguồn (8 bước)

### Bước 1 — Khảo sát nguồn (Người A, ngoài hệ thống)

Điền phiếu khảo sát cho mỗi nguồn:

| Mục kiểm | Cách kiểm | Ví dụ VnExpress |
|---|---|---|
| Trang RSS | Thường là `/rss`, `/rss.htm`, `/rss.html` hoặc link "RSS" ở chân trang | `https://vnexpress.net/rss` |
| Điều khoản RSS | Đọc mục "Điều khoản sử dụng" trên trang RSS và điều khoản chung của site | Miễn phí cho cá nhân, tổ chức phi lợi nhuận (xem mục 1) |
| robots.txt | Mở `/robots.txt`, xem có chặn bot lạ hoặc chặn `/rss` không | Cho phép tất cả (`Allow: /`), chặn một số bot có tên riêng |
| Feed khớp chuyên mục | Chọn feed theo chuyên mục của Tinhte, không lấy feed "Trang chủ" (quá rộng) | `khoa-hoc-cong-nghe.rss`, `oto-xe-may.rss` |
| Feed có tóm tắt không | Mở feed, xem thẻ `<description>` | Có tóm tắt ngắn và ảnh (hệ thống bỏ ảnh, chỉ giữ chữ) |
| Ngôn ngữ | | `vi` |
| Tần suất ra tin | Đếm số tin trong ngày trên feed | Vài chục tin/ngày ở mỗi chuyên mục |

Chỉ chọn những feed thật sự cần. Mỗi nguồn nên có **1–3 feed**.

### Bước 2 — Tạo nguồn trong danh mục (Người A, màn **Nguồn & giấy phép**)

| Trường | Điền | Ví dụ |
|---|---|---|
| Tên | Tên báo, viết như báo tự gọi | `VnExpress` |
| Loại | `OTHER` cho báo, trang tin; `PRESS_RELEASE` cho newsroom/thông cáo của hãng; `AGENCY` cho hãng thông tấn | `OTHER` |
| URL | Trang chủ | `https://vnexpress.net` |

- Tên trùng sẽ bị từ chối. Nguồn không xóa được, chỉ tắt.
- GĐ1 **không tạo giấy phép**, vì mọi nguồn đều ở M0/M1.

### Bước 3 — Tạo hồ sơ thu thập (Người A, màn **Nguồn thu thập → Tạo**)

| Trường | Điền | Ví dụ |
|---|---|---|
| Nguồn | Chọn nguồn vừa tạo ở bước 2 | `VnExpress` |
| Ngôn ngữ | Mã ngôn ngữ | `vi` |
| Site | Site Tinhte nhận tin từ nguồn này | site chính |
| Mức uy tín (1–5) | 5: nguồn chính thức (hãng, cơ quan nhà nước); 4: báo lớn; 3: trang công nghệ chuyên ngành; 2: blog; 1: diễn đàn | `4` |

Sau bước này nguồn ở trạng thái **Đề xuất**, chưa thu thập.

### Bước 4 — Thêm feed (Người A, chi tiết nguồn → **Thêm feed**)

| Trường | Điền | Ví dụ |
|---|---|---|
| URL feed | Bắt buộc `https://` | `https://vnexpress.net/rss/khoa-hoc-cong-nghe.rss` |
| Chu kỳ đọc (phút) | **30** (chủ dự án chốt 10/11/2026) | `30` |
| Ánh xạ category | Chỉ dùng khi tin trong feed có thẻ `<category>`: ghép giá trị của thẻ với chuyên mục Tinhte. Feed theo chuyên mục thường không có thẻ này; khi đó tin được phân loại bằng luật từ khóa ở bước 6 | để trống |

Thêm feed hoặc đổi URL feed của nguồn đã duyệt sẽ đưa nguồn về **chờ duyệt lại**.

### Bước 5 — Thử feed (Người A, nút **Thử feed**)

Hệ thống đọc thử bằng chính bộ thu thập thật, nhưng không lưu gì. Kiểm:
- đọc được, có tin, tin mới nhất là của hôm nay;
- tiêu đề, tóm tắt đọc được, không lỗi font;
- không bị robots.txt chặn.

| Kết quả thử | Ý nghĩa | Làm gì |
|---|---|---|
| `UNSAFE_URL` | URL trỏ vào địa chỉ nội bộ hoặc không phải https | Kiểm lại URL |
| Lỗi robots | Site cấm bot | Không dùng nguồn này |
| Lỗi mạng hoặc timeout | Site chậm hoặc chặn | Thử lại sau; vẫn lỗi thì bỏ |
| Đọc được nhưng 0 tin | Sai URL hoặc feed ngừng cập nhật | Tìm feed khác |

Mỗi người được thử tối đa 10 lần/phút.

### Bước 6 — Đặt luật chuyên mục (Người A, màn **Luật chuyên mục**)

Làm một lần cho mỗi chuyên mục Tinhte, không phải cho từng nguồn:

| Loại từ khóa | Dùng để | Ví dụ cho chuyên mục "Điện thoại" |
|---|---|---|
| Bao gồm | Tin có từ này thì thuộc chuyên mục | `iphone`, `galaxy`, `điện thoại`, `smartphone` |
| Loại trừ | Tin có từ này thì không thuộc chuyên mục | `điện thoại bàn`, `tổng đài` |
| Thực thể | Tên riêng giúp nhận ra cùng một sự kiện | `Apple`, `Samsung`, `Xiaomi` |

Từ khóa **giữ dấu tiếng Việt** và khớp nguyên từ: `đảng` không khớp với `đang`.

### Bước 7 — Duyệt (Người B, chi tiết nguồn → **Duyệt chế độ khai thác**, cần MFA)

Checklist trước khi bấm duyệt:
- [ ] Phiếu khảo sát bước 1 đầy đủ.
- [ ] Có bằng chứng điều khoản: ảnh chụp trang điều khoản RSS, kèm thư đồng ý của báo nếu đã xin (mục 1).
- [ ] Thử feed thành công.
- [ ] Chế độ đúng: **M0** nếu chỉ để BTV biết có tin; **M1** nếu được nhắc tên và dẫn link trong bài.

| Trường | Điền |
|---|---|
| Chế độ | `M0` hoặc `M1` |
| Bằng chứng điều khoản | Đường dẫn tới ảnh chụp hoặc thư lưu trong kho tài liệu nội bộ |
| Ghi chú | Lý do chọn chế độ, ngày xin phép, ai trả lời |

Duyệt xong nguồn chuyển **Đang chạy**. Worker bắt đầu đọc ở chu kỳ kế tiếp, nếu cờ `aggregation.radar` đang bật.

### Bước 8 — Theo dõi và xử lý sự cố (Người B nhận cảnh báo qua email)

| Tình huống | Hệ thống tự làm | Người làm |
|---|---|---|
| Feed lỗi 5 lần liên tiếp | Nguồn chuyển **Suy giảm**, gửi cảnh báo | Kiểm URL; site đổi feed thì sửa URL (sẽ phải duyệt lại) |
| Mọi feed lỗi liên tục ≥ 24 giờ | Nguồn tự **Tạm dừng**, gửi cảnh báo | Sửa xong thì bấm **Tiếp tục** |
| Báo yêu cầu ngừng dùng RSS | — | Bấm **Ngừng** ngay, ghi lý do và ngày nhận yêu cầu. Ngừng là trạng thái cuối, không bật lại được |
| Điều khoản của báo thay đổi | — | Tạm dừng, chụp điều khoản mới, duyệt lại |

---

## 4. Cấu hình mẫu cho các nguồn đầu tiên

Feed đã mở thử được ngày 10/11/2026. Chuyên mục Tinhte để Ban biên tập điền. **Chế độ trong bảng chỉ là đề xuất, Legal chốt.**

| Nguồn | Loại | Uy tín | Feed đề xuất (chu kỳ 30 phút) | Chế độ đề xuất | Điều khoản RSS |
|---|---|---|---|---|---|
| VnExpress | OTHER | 4 | `https://vnexpress.net/rss/khoa-hoc-cong-nghe.rss`<br>`https://vnexpress.net/rss/oto-xe-may.rss` | M0 | Miễn phí cho cá nhân, phi lợi nhuận → cần xin phép |
| Dân trí | OTHER | 4 | `https://dantri.com.vn/rss/cong-nghe.rss`<br>`https://dantri.com.vn/rss/o-to-xe-may.rss` | M0 | Như VnExpress → cần xin phép |
| Tuổi Trẻ | OTHER | 4 | `https://tuoitre.vn/nhip-song-so.rss` (Công nghệ)<br>`https://tuoitre.vn/xe.rss` | M0 | Như VnExpress → cần xin phép |
| Thanh Niên | OTHER | 4 | `https://thanhnien.vn/rss/cong-nghe/tin-tuc-cong-nghe.rss`<br>`https://thanhnien.vn/rss/cong-nghe/san-pham.rss`<br>`https://thanhnien.vn/rss/xe.rss` | M0 | Chưa thấy điều khoản trên trang RSS → Legal xem điều khoản chung |
| GenK | OTHER | 3 | `https://genk.vn/rss/mobile.rss`<br>`https://genk.vn/rss/ai.rss`<br>`https://genk.vn/rss/tin-ict.rss` | M0 | Chưa thấy điều khoản trên trang RSS → Legal xem điều khoản chung |
| VietnamNet | OTHER | 4 | Chưa tìm thấy: `https://vietnamnet.vn/rss` trả về 404 | — | Cần tìm lại trang RSS |

**Còn thiếu để đủ 10–20 nguồn** (Ban biên tập bổ sung, cùng mẫu):
- newsroom và thông cáo báo chí của các hãng (Apple, Samsung, Xiaomi, Google…): loại `PRESS_RELEASE`, uy tín 5;
- trang công nghệ quốc tế có RSS công khai: ngôn ngữ `en`;
- trang thông báo của cơ quan nhà nước về công nghệ, viễn thông: uy tín 5.

Với nguồn tiếng Anh: hiện máy chưa gom được tin Anh với tin Việt vào cùng một cụm (xem mục 5).

---

## 5. Quy tắc quét chủ dự án chốt ngày 10/11/2026 và tác động

| Quy tắc | Hệ thống hiện tại | Cần làm |
|---|---|---|
| **Quét 30 phút một lần** | Chu kỳ đặt theo từng feed (5–1440 phút) | Đặt 30 phút cho mọi feed (bước 4); đổi mặc định trong code thành 30 |
| **Chỉ lấy tin trong ngày** (giờ Việt Nam) | Radar lọc được "Hôm nay"; lần đầu bật feed, worker nhận tin trong 24 giờ gần nhất | Worker bỏ qua tin đăng trước 00:00 hôm nay theo giờ tòa soạn; Radar mặc định mở ở "Hôm nay" |
| **Trùng loại** — chỉ so trùng giữa các tin *cùng chuyên mục* (chủ dự án xác nhận 10/11/2026) | Gom cụm trong cùng site, không bắt buộc cùng chuyên mục | Thêm điều kiện cùng chuyên mục khi gom. Tin thuộc nhiều chuyên mục được so trong từng chuyên mục của nó |
| **AI phân tích nội dung từ nhiều nguồn để biết có trùng chủ đề không** | GĐ1 **không dùng AI**: gom bằng độ giống chữ (MinHash) và tên riêng chung | **Đổi quyết định ADR-0008 §7** (xem dưới) |

**Đề xuất cách dùng AI để nhận ra tin trùng chủ đề:**
1. Máy lọc trước bằng cách hiện có (MinHash, cùng chuyên mục, cùng ngày) để ra các cặp "có thể trùng". Như vậy không phải hỏi AI cho mọi cặp tin.
2. Gửi cho AI **chỉ tiêu đề và tóm tắt** của các tin trong cặp, qua module kết nối AI (YesScale, đang làm ở phiên riêng). AI trả lời "cùng sự kiện" hoặc "khác sự kiện", kèm độ tin cậy.
3. AI kết luận cùng sự kiện thì gộp cụm; khác sự kiện thì giữ riêng. Mỗi kết luận được ghi lại để BTV kiểm khi tách hoặc gộp tay.
4. AI lỗi, hết ngân sách hoặc tắt cờ: quay về cách gom hiện tại, Radar vẫn chạy.
5. Lợi ích kèm theo: AI nhận ra được tin tiếng Anh và tiếng Việt về cùng một sự kiện, việc MinHash không làm được.

**Điều kiện trước khi bật:**
- Legal (L-07) và Security (S-03) chấp nhận gửi tiêu đề và tóm tắt qua YesScale. Đây chỉ là trường công khai của RSS, không phải toàn văn.
- Legal xác nhận việc dùng AI để so sánh nội bộ vẫn nằm trong phạm vi M0. M0 hiện chỉ cấm đưa nội dung vào *nháp* AI, chưa nói tới việc dùng AI để so sánh.
- PM/Finance chốt ngân sách (F-02). Ước lượng thô: 20 nguồn × vài chục tin/ngày, mỗi ngày vài trăm tới vài nghìn lượt so cặp ngắn.
- Ghi quyết định mới vào ADR-0008 (§7 và mục câu hỏi), thêm cờ riêng cho phần AI để tắt độc lập với Radar.

---

## 6. Câu hỏi còn mở

| # | Ai | Câu hỏi |
|---|---|---|
| N-01 | Legal | ~~Với điều khoản "miễn phí cho cá nhân, phi lợi nhuận" của VnExpress, Dân trí, Tuổi Trẻ: chọn (a), (b) hay (c)?~~ **Đã chốt 10/11/2026: (a) xin phép trước.** |
| N-02 | Chủ dự án | ~~"Trùng loại" có đúng nghĩa là chỉ so trùng giữa các tin cùng chuyên mục không?~~ **Đã chốt 10/11/2026: đúng, cùng chuyên mục.** |
| N-03 | Chủ dự án | ~~2 người cụ thể giữ vai trò Người A / Người B?~~ **Đã chốt: cả 2 cùng đề xuất và duyệt được.** **Site thí điểm `www`; bắt đầu khi đủ 3 điều kiện ở phiếu, mục Kết quả chốt (đã chốt 10/11/2026).** |
| N-04 | Ban biên tập | Ánh xạ các feed ở mục 4 vào chuyên mục Tinhte nào? Bổ sung nguồn cho đủ 10–20. |

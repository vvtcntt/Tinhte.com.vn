# BA-01 — Phiếu xin quyết định: Radar tin (GĐ1) và AI viết lại (GĐ2) — v0.1

**Trạng thái:** DRAFT, lập ngày 10/11/2026. Mỗi phiếu gửi cho một bên. Người nhận điền mục **Trả lời**, ký tên, ghi ngày rồi gửi lại nhóm dự án.
**Căn cứ:**
- `doc/ba/BA-01_TONG_HOP_TIN_RSS_AI_v0.1_DRAFT.md` §18
- `doc/adr/ADR-0007-content-sources-licenses-rights.md`
- `doc/adr/ADR-0008-news-aggregation-radar.md`
- `doc/adr/ADR-0009-aggregation-ai-drafts.md` (ADR-0009 hiện chỉ có trên nhánh code bên dưới)

**Tình trạng code:**
- GĐ1 đã hiện thực đủ 5 lát trên nhánh `claude/agg-radar-slice2-029e01` (commit `afe1405`). Nhánh chưa merge và chưa push.
- Cờ `aggregation.radar` đang tắt ở mọi môi trường.
- GĐ2 chưa có dòng code nào.
- Module kết nối AI qua **YesScale** đang được phân tích ở một phiên làm việc riêng. Phiếu này coi YesScale là phương án nhà cung cấp AI đang xem xét, **chưa phải quyết định**.

**Cách đọc mỗi câu hỏi:**
- *Phương án:* các lựa chọn có thể chọn.
- *Đề xuất:* ý kiến của nhóm dự án, không thay thế ý kiến chuyên môn của người nhận phiếu, nhất là ý kiến pháp lý.
- *Đang tạm làm:* code hiện đang chạy theo giả định nào.
- *Nếu chưa trả lời:* việc gì bị chặn.

**Không tự ý làm:**
- Phiếu không bật cờ nào.
- Phiếu không nâng mức ưu tiên.
- Phiếu không điền effort.
- Phiếu không coi giấy phép hay thỏa thuận nguồn nào là đã có.

## Tổng quan

| Phiếu | Người nhận | Số câu | Chặn GĐ1 (bật Radar) | Chặn GĐ2 (AI viết) |
|---|---|---|---|---|
| [1](#phiếu-1--po) | PO (một số câu cùng BA) | 6 | P-01, P-02, P-03 | P-04, P-05, P-06 |
| [2](#phiếu-2--legal) | Legal | 10 | L-01, L-02, L-03 | L-04…L-10 |
| [3](#phiếu-3--security) | Security | 4 | S-01, S-02 | S-03, S-04 |
| [4](#phiếu-4--tl--dba) | TL, DBA | 7 | T-01, T-02, T-05 | T-03, T-04, T-06, T-07 |
| [5](#phiếu-5--ban-biên-tập) | Ban biên tập | 5 | E-01, E-02, E-03 | E-04, E-05 |
| [6](#phiếu-6--pm--finance) | PM, Finance | 2 | F-01 | F-02 |
| [7](#phiếu-7--seo-không-gấp) | SEO | 1 | — | khi có trang công khai |

**Thứ tự nên hỏi để bật Radar sớm nhất:**
1. P-01, P-02 (PO)
2. S-01 (Security)
3. L-01, L-02, L-03 (Legal), E-02 (Ban biên tập)
4. T-01, T-02 (TL)
5. E-01 (Ban biên tập)

Câu L-04 là câu quyết định toàn bộ GĐ2. Nên hỏi Legal ngay, dù GĐ2 chưa làm.

---

## Phiếu 1 — PO

### P-01 · Mức ưu tiên của Radar tin (A-5, D4, Q-AGG-06)
- **Câu hỏi:** xếp cờ `aggregation.radar` và các yêu cầu FR-AGG-001…005 vào P1 hay giữ P2?
- **Phương án:**
  - (a) Giữ P2, làm cùng nhóm AI.
  - (b) Nâng P1 để dùng Radar nội bộ sớm.
- **Đề xuất:** (b) cho Radar (GĐ1); AI viết lại (GĐ2) giữ P2.
  - Radar chỉ đọc tiêu đề và link để BTV xem nội bộ, không đăng gì ra ngoài, nên rủi ro pháp lý thấp.
  - Code đã xong; giữ P2 thì code nằm chờ mà không giảm rủi ro nào.
- **Đang tạm làm:** P2.
- **Nếu chưa trả lời:** chưa xếp được vào sprint phát hành.
- **Trả lời:** ………

### P-02 · Thí điểm Radar
- **Câu hỏi:** bật cờ `aggregation.radar` ở môi trường nào, từ khi nào, cho site nào và nhóm BTV nào?
- **Phương án:**
  - (a) Bật ở staging với dữ liệu nguồn thật, chạy 2–4 tuần để BTV dùng thử và gom nhãn tách/gộp cụm.
  - (b) Chờ đủ các phiếu khác rồi bật thẳng production.
- **Đề xuất:** (a), cụ thể:
  - bật trên **staging**, cho **1 site** và **2–3 BTV**, trong **3 tuần**;
  - hết 3 tuần, xem số liệu (số tin mỗi ngày, tỷ lệ gom đúng, góp ý của BTV) rồi mới quyết định lên production.
  - Lý do: ngưỡng gom cụm cần ít nhất 30 cặp nhãn mỗi loại mới hiệu chỉnh được (xem E-03), mà nhãn chỉ có khi BTV dùng thật.
- **Đang tạm làm:** cờ tắt ở mọi nơi.
- **Nếu chưa trả lời:** không có dữ liệu thật để hiệu chỉnh.
- **Trả lời:** ………

### P-03 · Ai được duyệt nguồn (A-3, Q-AGG-07; cùng BA)
- **Câu hỏi:** chốt ma trận quyền của Radar. Có cần thêm vai trò mới không?
- **Đang tạm làm** (chủ dự án cấp tạm ngày 10/11/2026):
  - EDITOR: `aggregation.read` và `aggregation.sources_manage`.
  - APPROVER: `aggregation.read` và `aggregation.sources_approve` (cần MFA).
- **Phương án:**
  - (a) Giữ như đang tạm làm.
  - (b) Thêm vai trò mới, ví dụ người phụ trách nguồn. Phải sửa `rbac.json` và CHECK `ck_user_roles_role` bằng migration mới.
- **Đề xuất:** (a), không thêm vai trò mới.
  - Hệ thống đã tách nhiệm vụ: người tự đề xuất nguồn M2/M3 không tự duyệt được.
  - Chỉ cần vai trò riêng khi có người của Legal trực tiếp vào CMS duyệt; khi đó xem lại cùng L-02.
- **Trả lời:** ………

### P-04 · Mức ưu tiên của AI viết lại (B-3)
- **Câu hỏi:** cờ `ai.aggregation_drafts` của GĐ2 xếp P1 hay P2?
- **Đề xuất:** giữ P2 cho tới khi Legal trả lời L-04 và L-05.
- **Trả lời:** ………

### P-05 · AI có được tự soạn nháp không (Q-AGG-12)
- **Câu hỏi:** ở GĐ2, AI có được tự soạn nháp cho cụm điểm cao mà không chờ BTV bấm không?
- **Đề xuất:** không. Xem lại ở GĐ4 khi đã có số liệu chất lượng.
- **Trả lời:** ………

### P-06 · Quyền nội dung trên bài (ADR-0007 Q1, Q6; Q6 hỏi cùng TL)
- **Q1:** vai trò nào có quyền `content.rights_verify`?
  - (a) APPROVER.
  - (b) Vai trò mới chuyên xác nhận quyền.
- **Q6:** bài hết hạn quyền xử lý thế nào?
  - (a) Chuyển UNPUBLISHED.
  - (b) Ẩn và giữ trạng thái.
  - Kèm theo: job kiểm hạn quyền thuộc WBS nào?
- **Đề xuất:** Q1 chọn (a). Q6 chọn (b), kèm cảnh báo cho thư ký tòa soạn.
- **Nếu chưa trả lời:** chưa làm được ADR-0007 §2–§4, mà GĐ2 cần các mục này.
- **Trả lời:** ………

---

## Phiếu 2 — Legal

> Các căn cứ pháp lý trong BA-01 §4 (NĐ 147/2024, Luật Báo chí 126/2025, Luật AI 134/2025, Luật SHTT, Luật BVDLCN) mới tổng hợp từ bài phân tích, **chưa đối chiếu văn bản gốc**. Xin Legal kiểm lại khi trả lời.
>
> **Các chế độ khai thác nguồn** (BA-01 §5):
> - **M0:** chỉ phát hiện tin, dùng nội bộ.
> - **M1:** nhắc tên và dẫn link nguồn.
> - **M2:** dùng sự kiện để viết bài mới.
> - **M3:** có giấy phép làm tác phẩm phái sinh.

### L-01 · Danh sách nguồn ban đầu (Q-AGG-03, D3; cùng Ban biên tập)
- **Câu hỏi:** những nguồn nào được thu thập? Mỗi nguồn ở chế độ nào? Nguồn nào cần văn bản thỏa thuận?
- **Mẫu điền:**

  | Nguồn | Feed | Chế độ đề nghị | Có thỏa thuận hoặc giấy phép? | Bằng chứng điều khoản / robots | Ghi chú |
  |---|---|---|---|---|---|
  | | | M0 / M1 / M2 / M3 | | | |

- **Đề xuất:** 10–20 nguồn cho thí điểm, **tất cả ở M0, cùng lắm M1**.
  - Ưu tiên: newsroom và thông cáo báo chí của các hãng công nghệ; trang công nghệ lớn có RSS công khai; trang thông báo của cơ quan nhà nước.
  - Ở M0/M1 hệ thống chỉ lưu tiêu đề, tóm tắt và link, không lấy toàn văn, nên duyệt nhanh được.
  - Chưa nâng nguồn nào lên M2/M3 vì GĐ1 chưa có AI. Nguồn nào chưa rõ điều khoản thì để M0.
  - Ban biên tập đưa danh sách (E-02), Legal gán chế độ.
- **Nếu chưa trả lời:** Radar không có nguồn thật để chạy.
- **Trả lời:** ………

### L-02 · Ai được tạo và sửa giấy phép (ADR-0007 Q2)
- **Câu hỏi:** ai được tạo và sửa giấy phép trong danh mục? Có bắt buộc nhập bằng chứng (`evidenceRef`) không?
- **Đang tạm làm** (chặt hơn mức tối thiểu):
  - Quyền `licenses.manage` cấp tạm cho APPROVER, cần MFA.
  - `evidenceRef` bắt buộc và không xóa được.
  - Mã giấy phép không đổi được.
- **Đề xuất:** giữ đúng như đang tạm làm:
  - chỉ APPROVER, có MFA, được tạo và sửa giấy phép;
  - **bắt buộc** đính kèm bằng chứng (link hợp đồng hoặc ảnh chụp điều khoản);
  - không xóa giấy phép, chỉ được tắt.
  - Ở GĐ1 chưa cần nhập giấy phép nào, vì mọi nguồn đều ở M0/M1.
- **Lưu ý:** có bản ghi giấy phép trong hệ thống **không** có nghĩa là đã được phép khai thác. Người nhập chịu trách nhiệm về bản ghi đó.
- **Trả lời:** ………

### L-03 · Thời gian lưu dữ liệu (Q-AGG-14)
- **Câu hỏi:** giữ mục tin thô (tiêu đề, tóm tắt, link) và nhật ký thu thập bao lâu? Ở GĐ2, giữ toàn văn nguồn bao lâu?
- **Đang tạm làm:**
  - Nhật ký thu thập xóa sau 30 ngày.
  - Mục tin thô **chưa có hạn xóa**.
- **Đề xuất:**
  - Nhật ký thu thập: giữ 30 ngày, như đang tạm làm.
  - Mục tin không thuộc cụm nào: xóa sau 90 ngày.
  - Mục tin thuộc cụm đã có bài: giữ cùng bài để còn đối chiếu nguồn.
  - Nếu đồng ý thì cần thêm job xóa mục tin sau 90 ngày (khoảng nửa ngày làm).
- **Trả lời:** ………

### L-04 · Tinhte xuất bản theo loại giấy phép nào (Q-AGG-01, D1) — câu quyết định GĐ2
- **Câu hỏi:** Tinhte hoạt động theo loại giấy phép nào?
  - (a) Báo hoặc tạp chí điện tử.
  - (b) Trang thông tin điện tử tổng hợp.
  - (c) Mạng xã hội.
  - (d) Loại khác.
- **Câu hỏi kèm theo:** với loại đó, Tinhte được viết bài mới từ sự kiện của báo khác, hay chỉ được trích nguyên văn khi có thỏa thuận?
- **Hệ quả:** nếu là (b), "AI viết lại" có thể không phù hợp. Khi đó GĐ2 phải đổi sang mô hình trích nguyên văn và cần phân tích lại.
- **Đề xuất:** Legal chỉ cần đối chiếu giấy phép hiện có của Tinhte là trả lời được. Nên hỏi ngay dù GĐ2 chưa làm, vì câu này quyết định GĐ2 có làm được hay không. Câu này không chặn GĐ1.
- **Trả lời:** ………

### L-05 · Viết bài mới từ sự kiện (Q-AGG-02)
- **Câu hỏi:**
  - Có chấp nhận chế độ M2 (AI kể lại sự kiện bằng lời và bố cục mới) không?
  - Có chấp nhận với báo trong nước chưa có thỏa thuận không?
  - Ngưỡng độ giống tối đa với bài nguồn, và độ dài trích nguyên văn tối đa (BR-44), là bao nhiêu?
- **Trả lời:** ………

### L-06 · Nhãn AI (Q-AGG-04)
- **Câu hỏi:**
  - Câu chữ, vị trí và định dạng máy đọc của nhãn AI trên bài?
  - Bài BTV đã sửa nhiều có còn bắt buộc gắn nhãn không (BR-65)?
- **Trả lời:** ………

### L-07 · Gửi nội dung ra nhà cung cấp AI (Q-AGG-05, D5; cùng Security, xem S-03)
- **Câu hỏi:** có được gửi nội dung nguồn ra nhà cung cấp AI không?
- **Bối cảnh:** phương án đang xem xét là gọi AI qua **YesScale**. Xin Legal đánh giá:
  - Dữ liệu đi qua YesScale tới những model và nhà cung cấp nào, xử lý ở quốc gia nào?
  - Có lưu dữ liệu hay dùng để huấn luyện không?
  - Việc này có phải chuyển dữ liệu cá nhân ra nước ngoài theo Luật BVDLCN không? Nội dung tin tức có tên và ảnh người thật.
- **Đề xuất kỹ thuật:** chỉ gửi phần cần thiết của nguồn M2/M3 (BR-40), không gửi ảnh, không gửi dữ liệu người dùng.
- **Trả lời:** ………

### L-08 · Quy định "không đăng sớm hơn 1 giờ" (Q-AGG-13)
- **Câu hỏi:** quy định không đăng sớm hơn 1 giờ sau tin gốc có áp dụng cho Tinhte không? Nếu có, hệ thống cần chặn ở bước xuất bản (BR-64).
- **Trả lời:** ………

### L-09 · Bài tự viết có cần xác nhận quyền không (ADR-0007 Q3; cùng PO)
- **Câu hỏi:** bài tự viết (nguồn `ORIGINAL`) có cần bước xác nhận quyền không, hay mặc định là đã đủ quyền?
- **Đề xuất:** vẫn cần xác nhận, vì ảnh trong bài có thể không phải của Tinhte.
- **Trả lời:** ………

### L-10 · Chủ đề nhạy cảm và trang `/bot` (cùng Ban biên tập, xem E-01)
- **Câu hỏi:**
  - Duyệt danh sách chủ đề nhạy cảm. Mục tin nhạy cảm bị gắn cờ và sẽ không được đưa cho AI ở GĐ2.
  - Xem lại nội dung trang `/bot`, trang giới thiệu bot thu thập tin cho các website nguồn. Trang chỉ mô tả hành vi kỹ thuật, không có cam kết pháp lý.
- **Đang tạm làm:** `config/aggregation/sensitive-topics.json` là file mẫu nháp với 5 nhóm: chính trị, an ninh quốc gia, tôn giáo, tin đồn chưa kiểm chứng, thương vong.
- **Trả lời:** ………

---

## Phiếu 3 — Security

### S-01 · Giới hạn của bộ thu thập (A-4)
- **Câu hỏi:** xin duyệt các giới hạn đang áp dụng (`FeedUrlPolicy`, `FeedFetcher`):
  - Chỉ nhận https, cổng 443, URL không chứa tên đăng nhập hoặc mật khẩu.
  - Chặn tên nội bộ: `localhost`, `*.local`, `*.localhost`, `*.internal`, `*.intranet`, `*.lan`, `*.home.arpa`, `*.corp`, và tên không có dấu chấm.
  - Chặn dải IPv4: `0.0.0.0/8`, `10/8`, `100.64/10`, `127/8`, `169.254/16`, `172.16/12`, `192.0.0/24`, `192.0.2/24`, `192.88.99/24`, `192.168/16`, `198.18/15`, `198.51.100/24`, `203.0.113/24`, `224/4`, `240/4`.
  - Chặn dải IPv6: `::/96`, `64:ff9b::/96`, `64:ff9b:1::/48`, `100::/64`, `2001::/32`, `2001:db8::/32`, `fc00::/7`, `fe80::/10`, `fec0::/10`, `ff00::/8`, loopback, any. Địa chỉ 6to4 thì kiểm phần IPv4 nhúng bên trong.
  - Kiểm IP thật ngay lúc mở kết nối, để chặn cả DNS rebinding. Nếu DNS trả về bất kỳ địa chỉ nội bộ nào thì chặn cả tên miền. Không dùng proxy, không gửi cookie.
  - Tự theo redirect tối đa 3 lần, kiểm lại ở mỗi bước. Timeout 20 giây cho cả lượt. Đọc tối đa 5 MB sau giải nén. Chỉ nhận nội dung XML hoặc JSON.
  - Parser XML tắt DTD và không resolve tài nguyên ngoài.
  - `robots.txt` cache 24 giờ. Lỗi 4xx thì coi như được phép. Lỗi 5xx hoặc 429 thì coi như bị cấm 1 giờ. Lỗi mạng thì bỏ qua `robots.txt`.
  - Mỗi người thử đọc feed tối đa 10 lần/phút. Giới hạn đếm trong bộ nhớ của từng bản API, nên chạy nhiều bản thì tổng thực tế cao hơn.
- **Phương án:** duyệt nguyên trạng, hoặc ghi rõ điểm cần đổi.
- **Đề xuất:** duyệt nguyên trạng, **kèm một điều kiện hạ tầng**: worker chạy trong mạng riêng, tường lửa chỉ cho kết nối ra Internet cổng 443 và kết nối tới DB. Nếu code có lỗ hổng thì lớp mạng này vẫn chặn được truy cập vào hệ thống nội bộ.
- **Nếu chưa trả lời:** không được bật Radar trên môi trường có mạng nội bộ thật.
- **Trả lời:** ………

### S-02 · Lỗi riêng cho đích nội bộ phát hiện lúc thu thập
- **Câu hỏi:** có cần thêm giá trị `UNSAFE_TARGET` vào enum `AggregationFeedFailure` ở bản spec sau không?
- **Bối cảnh:** khi worker phát hiện đích nội bộ qua redirect hoặc DNS, lỗi hiện được ghi là `HTTP_STATUS` hoặc `DNS`, nên lẫn với lỗi thường.
- **Đề xuất:** có, để cảnh báo và log phân biệt được các lần thử SSRF.
- **Trả lời:** ………

### S-03 · Gửi dữ liệu qua YesScale (Q-AGG-05; cùng Legal, xem L-07)
- **Câu hỏi:** đánh giá bảo mật của YesScale:
  - Mã hóa đường truyền.
  - Quản lý API key: lưu ở đâu, xoay vòng thế nào.
  - Log và thời gian lưu dữ liệu phía YesScale.
  - Vùng xử lý.
  - Có cần hợp đồng xử lý dữ liệu không.
- **Trả lời:** ………

### S-04 · Bảo vệ toàn văn nguồn M2/M3 (B-2)
- **Câu hỏi:** có mã hóa toàn văn nguồn ở mức cột không? Ai được đọc?
- **Đề xuất:** chỉ worker và người có quyền nháp AI được đọc. Truy cập toàn văn có ghi audit.
- **Trả lời:** ………

---

## Phiếu 4 — TL, DBA

### T-01 · Worker là project riêng (A-1)
- **Câu hỏi:** đồng ý worker là project riêng `Tinhte.Worker`, deploy riêng và chạy được nhiều bản song song?
- **Đang tạm làm:** đã làm như vậy, gồm Dockerfile, liveness, nhịp tim, số đo OTLP và runbook.
- **Đề xuất:** đồng ý. Nguồn chậm hay lỗi không ảnh hưởng API phục vụ CMS; muốn đọc nhanh hơn chỉ cần chạy thêm bản worker.
- **Nếu chưa trả lời:** ADR-0008 chưa chuyển được sang ACCEPTED.
- **Trả lời:** ………

### T-02 · Bảng hồ sơ thu thập và thứ tự migration (A-2, ADR-0007 Q4; cùng DBA)
- **Câu hỏi:**
  - Đồng ý tách bảng `agg_source_profiles` quan hệ 1–1 với nguồn, thay vì thêm cột vào `content_sources`?
  - Đồng ý thứ tự migration 0340 → 0345?
  - Schema 04 đã có bảng nguồn và giấy phép chưa? Nếu có thì tên cột là gì?
- **Đang tạm làm:** đã làm như vậy và chạy qua PGlite.
- **Đề xuất:** đồng ý cả hai ý đầu. Với ý thứ ba, nếu schema 04 chưa có bảng nguồn và giấy phép thì giữ nguyên; nếu đã có thì đổi tên cột cho khớp trước khi lên production.
- **Trả lời:** ………

### T-03 · Lưu nguồn ở cấp revision (Q-AGG-09, ADR-0007 Q5)
- **Câu hỏi:** đồng ý lưu danh sách nguồn trên revision thay vì trên bài (sửa ADR-0007 §2)?
- **Đề xuất:** đồng ý, vì nháp AI và mỗi lần sửa có thể đổi nguồn.
- **Trả lời:** ………

### T-04 · Nhà cung cấp AI (Q-AGG-15)
- **Câu hỏi:** xác nhận dùng module kết nối AI qua **YesScale**, đang phân tích ở phiên riêng, làm cổng gọi AI cho GĐ2. Chọn model sinh văn bản và model embedding nào?
- **Lưu ý:** cần Legal trả lời L-07 và Security trả lời S-03 trước khi gửi nội dung thật qua YesScale.
- **Trả lời:** ………

### T-05 · Nền tảng giám sát và kênh cảnh báo (WBS-021)
- **Câu hỏi:**
  - Hệ thống nào nhận số đo OTLP? Ví dụ Grafana Cloud, Prometheus kèm collector, hay hệ thống khác.
  - Webhook cảnh báo gửi về kênh nào (Slack, Teams hay Google Chat)?
  - Ai trực các cảnh báo trong `infra/monitoring/aggregation-worker.rules.yml`?
- **Đang tạm làm:** chủ dự án đã chọn OTLP ngày 10/11/2026. Exporter chỉ bật khi có endpoint. Webhook chưa cấu hình.
- **Việc cần làm kèm:** chạy job CI `worker-image` lần đầu (build image, quét Trivy, `promtool`). Job chưa chạy lần nào vì máy dựng không có Docker.
- **Đề xuất:**
  - Nơi nhận số đo: nhóm nhỏ dùng Grafana Cloud (có gói miễn phí, nhận OTLP trực tiếp, không phải tự vận hành); đã có hạ tầng riêng thì dựng OpenTelemetry Collector, Prometheus và Grafana.
  - Kênh cảnh báo: webhook về công cụ chat team đang dùng.
  - Người trực: 1 người phía kỹ thuật.
- **Trả lời:** ………

### T-06 · Hàng đợi nháp AI (B-1)
- **Câu hỏi:** hàng đợi nháp AI đặt trong DB và chạy bằng worker hiện có, hay chờ outbox WBS-020?
- **Đề xuất:** dùng worker hiện có, thuê việc bằng `lease` giống cách worker đang thuê feed.
- **Trả lời:** ………

### T-07 · Merge nhánh Radar tin
- **Câu hỏi:** review nhánh `claude/agg-radar-slice2-029e01` (6 commit, từ `8a54754` tới `afe1405`) và merge vào nhánh nào?
- **Đề xuất:** mở PR vào nhánh tích hợp đang dùng (`sprint-0/scaffold` hoặc `main`) để review và merge, không merge thẳng vào `feat/article-page`. Module AI (YesScale) cũng sửa `rbac.json` và spec, nên merge Radar trước rồi nhánh AI rebase lên để giảm xung đột.
- **Trả lời:** ………

---

## Phiếu 5 — Ban biên tập

### E-01 · Chính sách biên tập cho Radar (Q-AGG-08)
- **Câu hỏi:** xin chốt các mục sau. Mọi thay đổi đi qua pull request.
  - Danh sách chủ đề nhạy cảm (cùng Legal, xem L-10).
  - Độ uy tín của từng nguồn.
  - Trọng số chấm điểm cụm (BR-32). Đang tạm đặt: số nguồn độc lập 35, uy tín 15, độ liên quan 15, tốc độ lan 15, độ mới 20.
  - Có cần cơ chế "chủ đề nóng trong ngày" để cộng điểm không? Ai đặt chủ đề nóng?
  - Quy tắc "cần ít nhất 2 nguồn" áp dụng ở đâu?
- **Đề xuất:**
  - Chủ đề nhạy cảm: giữ 5 nhóm có sẵn, thêm **pháp luật/vụ án**, **y tế/dịch bệnh**, **trẻ em**.
  - Trọng số: giữ nguyên trong thí điểm; sau 3 tuần BTV góp ý tin nào bị xếp sai thì chỉnh. Ý nghĩa từng yếu tố:
    - số nguồn độc lập (35): nhiều nguồn cùng đưa tin thì nhiều khả năng là tin thật và quan trọng;
    - độ mới (20): tin vừa ra được ưu tiên;
    - uy tín nguồn (15): nguồn chính thức xếp trên blog;
    - độ liên quan chuyên mục (15): tin càng đúng chuyên mục điểm càng cao;
    - tốc độ lan (15): nhiều nguồn đưa tin trong 2 giờ thì tin đang nóng.
  - "Chủ đề nóng" và quy tắc "ít nhất 2 nguồn": để sau thí điểm.
- **Trả lời:** ………

### E-02 · Danh sách nguồn ban đầu (cùng Legal, xem L-01)
- **Câu hỏi:** Ban biên tập đề xuất nguồn và chuyên mục tương ứng; Legal chốt chế độ khai thác.
- **Đề xuất:** đưa 10–20 trang mà BTV vẫn theo dõi hằng ngày, kèm chuyên mục tương ứng, theo mẫu ở L-01.
- **Trả lời:** ………

### E-03 · Gán nhãn trong giai đoạn thí điểm
- **Câu hỏi:** cử BTV dùng thao tác tách và gộp cụm trên Radar trong thời gian thí điểm (xem P-02), để có ít nhất 30 cặp nhãn mỗi loại.
- **Cách dùng nhãn:** lệnh `Tinhte.Worker calibrate` dùng các cặp này để đề xuất ngưỡng gom cụm. Ngưỡng hiện tại chưa được hiệu chỉnh bằng dữ liệu thật.
- **Đề xuất:** 2–3 BTV, mỗi người 10–15 phút/ngày rà Radar. Thấy cụm gom nhầm hai sự kiện thì **tách**; thấy hai cụm thực ra là một sự kiện thì **gộp**. Sau 3 tuần là đủ dữ liệu.
- **Trả lời:** ………

### E-04 · Kiểm duyệt bài AI (Q-AGG-16)
- **Câu hỏi:**
  - Nội dung checklist BTV (BR-81) và checklist người duyệt (BR-82).
  - Ngưỡng cảnh báo bài "gần như nguyên bản AI" (BR-84).
  - Tỷ lệ hậu kiểm hằng tuần (BR-85).
- **Trả lời:** ………

### E-05 · Style guide và bộ đánh giá (BA-01 §14)
- **Câu hỏi:**
  - Style guide cho nháp AI.
  - Chọn 100 cụm tin thật đã có bài do BTV viết, làm bộ đánh giá chất lượng AI trước khi mở cho người dùng nội bộ.
- **Trả lời:** ………

---

## Phiếu 6 — PM, Finance

### F-01 · Effort WBS-140…145
- **Câu hỏi:** ước lượng và ký effort cho các mục WBS-140…145.
- **Lưu ý:** phần code GĐ1 đã làm trên nhánh, nhưng nhóm dự án không tự điền effort.
- **Đề xuất:** ghi phần đã làm là effort thực tế; PM chỉ cần ước phần còn lại: review và merge, dựng staging cùng giám sát, hỗ trợ 3 tuần thí điểm, chỉnh ngưỡng sau thí điểm.
- **Trả lời:** ………

### F-02 · Ngân sách AI (Q-AGG-10)
- **Câu hỏi:** ngân sách AI mỗi tháng là bao nhiêu? Ngưỡng cứng mỗi ngày là bao nhiêu?
- **Tham khảo:** bậc độ lớn chi phí ở BA-01 phụ lục C. Đơn giá theo bảng giá YesScale khi TL chốt T-04.
- **Trả lời:** ………

---

## Phiếu 7 — SEO (không gấp)

### O-01 · Link nguồn và trang điểm tin (Q-AGG-11)
- **Câu hỏi:**
  - Thuộc tính `rel` cho link nguồn trên bài công khai.
  - Trang dạng "điểm tin" có đặt `noindex` không?
- **Ghi chú:** GĐ1 chỉ dùng nội bộ, nên câu này chưa gấp.
- **Đề xuất:** chưa cần trả lời; hỏi lại khi GĐ2 có bài công khai dẫn link nguồn.
- **Trả lời:** ………

---

## Ký nhận

| Phiếu | Người trả lời | Ngày | Chữ ký / xác nhận |
|---|---|---|---|
| 1 — PO | | | |
| 2 — Legal | | | |
| 3 — Security | | | |
| 4 — TL, DBA | | | |
| 5 — Ban biên tập | | | |
| 6 — PM, Finance | | | |
| 7 — SEO | | | |

---
id: ADR-0009
title: Radar tin GĐ2 — AI viết lại nháp có kiểm duyệt nhiều lớp
status: PROPOSED — ĐỀ XUẤT, CHƯA HIỆN THỰC, KHÔNG CÓ CODE. Chờ Legal (Q-AGG-01/02/04/05/13/14 — điều kiện bắt buộc), TL (kiến trúc, nhà cung cấp AI — Q-AGG-15), PO (phạm vi, ưu tiên), BA/PO (quyền), Security (gửi dữ liệu ra ngoài), Ban biên tập (style guide, checklist — Q-AGG-08/16), PM/Finance (ngân sách AI — Q-AGG-10)
date: 2026-11-10
related: BA-01 §7.5–7.8, §9–§12, ADR-0004, ADR-0005, ADR-0007, ADR-0008, FR-AI-004, FR-NEWS-005/006
---

# ADR-0009 — Radar tin GĐ2: AI viết lại nháp có kiểm duyệt

## Bối cảnh

- Chủ dự án đã chốt (D2, 09/11/2026): AI viết lại tin từ cụm sự kiện, **bắt buộc kiểm duyệt, không tự đăng** (BA-01 §7.8).
- GĐ1 (ADR-0008) đã hiện thực: nguồn có chế độ khai thác M0–M3 + giấy phép, thu thập, khử trùng, phân loại, gom cụm, Radar trên CMS, danh mục nguồn/giấy phép (ADR-0007 §1).
- **GĐ2 phụ thuộc quyết định pháp lý chưa có**: được viết bài mới từ sự kiện của báo khác không và trong giới hạn nào (Q-AGG-01/02), nhãn AI (Q-AGG-04), gửi nội dung nguồn ra nhà cung cấp AI ở nước ngoài (Q-AGG-05), độ trễ sau tin gốc (Q-AGG-13), thời hạn lưu toàn văn (Q-AGG-14). Vì vậy ADR này **chỉ là thiết kế đề xuất**; không có migration, code hay thay đổi spec chính cho tới khi các điều kiện ở mục "Điều kiện trước khi hiện thực" được đáp ứng.
- Hợp đồng nháp nằm riêng ở `doc/07_AI_REVIEW/v0.3/proposals/07_OPENAPI_AGGREGATION_GD2_PROPOSAL.yaml` (OpenAPI 3.1, hợp lệ độc lập). Spec v0.3 chính, `ErrorCatalog`, `FeatureFlagKeys`, `rbac.json` **không đổi**.

## Quyết định đề xuất

### 1. Phạm vi GĐ2

- BTV bấm "Soạn nháp AI" trên một cụm → AI tạo **nháp** (không phải bài) từ các mục tin của nguồn được phép → máy kiểm tra (Checker) → BTV xem, sửa, chuyển thành bài `DRAFT` → bài đi **đúng quy trình duyệt hiện có** (ADR-0005, WBS-024) cộng các lớp kiểm duyệt của §6.
- Không có: tự soạn khi chưa có người bấm (Q-AGG-12, xem lại ở GĐ4), tự xuất bản, AI phân loại/gom cụm (embedding đa ngôn ngữ để ADR riêng khi chọn nhà cung cấp).

### 2. Dữ liệu được gửi cho AI (BR-40, BR-52)

- Chỉ mục tin của nguồn **M2/M3** trong cụm; kiểu `FACT_REWRITE` cần ≥ 1 nguồn M2/M3, `LICENSED_REWRITE` cần **mọi** nguồn đầu vào là M3 và giấy phép cho phép biên soạn/dịch ⇒ ngược lại 422 `USAGE_MODE_INSUFFICIENT`. Nguồn M0/M1 không bao giờ vào prompt.
- Toàn văn nguồn chỉ thu (worker, cùng bộ thu thập an toàn của ADR-0008 §5, tôn trọng robots.txt) cho nguồn M2/M3, lưu tách bảng, xóa theo thời hạn Legal duyệt (Q-AGG-14).
- Cụm nhãn `SENSITIVE` (BR-34) ⇒ không soạn được (409 `STATE_CONFLICT`, CMS khóa nút kèm lý do).
- Không gửi dữ liệu nội bộ khác (bài chưa công bố, thông tin nhân sự, khóa).

### 3. Kiến trúc

- **Hàng đợi nháp trong DB, xử lý ở worker** (`Tinhte.Worker`): bảng `agg_draft_jobs` thuê bằng `FOR UPDATE SKIP LOCKED` + `lease_until` như feed (ADR-0008 §3). API chỉ ghi yêu cầu và trả 202; không gọi AI trong tiến trình API. Khi WBS-020 có outbox chung thì chuyển sang đó.
- **Adapter trung lập nhà cung cấp** `IDraftModel` (sinh nháp có cấu trúc + dẫn chứng) và `IGroundingJudge` (bộ chấm ngữ nghĩa thứ hai). Nhà cung cấp do TL + Legal + Security chọn (Q-AGG-05/15). Nếu chọn Claude: structured outputs (JSON schema của nháp), citations trên khối tài liệu nguồn, prompt caching cho hướng dẫn/style guide/danh sách chuyên mục; khóa API trong kho bí mật.
- **Chống prompt injection** (BR-47): nội dung nguồn là dữ liệu không tin cậy, đặt trong khối tài liệu tách khỏi hướng dẫn; model không có công cụ; đầu ra theo schema cố định; link trong nháp chỉ được là link nguồn của cụm (link lạ bị bỏ, gắn `INJECTION_SUSPECTED`). Ghi chú "góc viết" của BTV là chỉ dẫn tin cậy, tách khỏi dữ liệu nguồn, không tắt được ghi nguồn/nhãn/kiểm tra (BR-53).
- **Checker tất định chạy bằng code** (không dùng AI): độ giống n-gram/chuỗi ≥ 12 từ ngoài ngoặc kép (BR-44 → `SIMILARITY_TOO_HIGH`), đối chiếu số/tiền/đơn vị/tên riêng/ngày với đoạn dẫn chứng (BR-45 → `GROUNDING_FAILED`), link hợp lệ, đủ khối nguồn + nhãn AI. Bộ chấm AI chỉ được **thêm** chặn, không được gỡ chặn.
- **Ngân sách** (BR-49): hạn mức nháp/ngày theo site và BTV, ngân sách token/ngày cứng ⇒ 429 `AI_BUDGET_EXCEEDED` + `Retry-After` tới đầu ngày (giờ tòa soạn). Thu thập GĐ1 không bị ảnh hưởng.
- **Audit** (BR-50, FR-AI-004): mỗi lời gọi AI ghi chỉ thêm: người yêu cầu, model + phiên bản, phiên bản prompt, id mục đầu vào, token, chi phí, kết quả kiểm tra, thời gian. Không ghi toàn văn nguồn vào log.

### 4. Nháp, phiên bản, chuyển thành bài (BR-42, BR-51, BR-60)

- Nháp: tiêu đề, sapo, `bodyBlocks` đúng schema editor (WBS-030), chuyên mục/tag gợi ý, danh sách nguồn, ghi chú cho BTV (điểm chưa chắc, mâu thuẫn), dẫn chứng từng câu khẳng định → mục tin + đoạn trích (metadata nội bộ).
- Trạng thái nháp: `QUEUED → GENERATING → CHECKING → READY | CHECK_FAILED | FAILED`; BTV sửa/kiểm lại; `READY → CONVERTED | DISCARDED`. Soạn lại tạo **phiên bản mới**, không ghi đè.
- "Tạo bài từ nháp" dùng luồng tạo bài hiện có: bài `DRAFT`, chủ bài = BTV bấm, `origin = AGGREGATION`, `aiAssistance = ASSISTED`, liên kết `clusterId`/`draftId`. Chỉ chuyển khi nháp qua hết kiểm tra chặn.

### 5. Nguồn trên revision (sửa ADR-0007 §2 — Q-AGG-09)

- Bài có **danh sách nguồn** (≥ 1: nguồn, URL gốc, tác giả, thời điểm đăng gốc, vai trò `PRIMARY`/`SUPPORTING`) nằm **trên revision** (nội dung đổi thì nguồn có thể đổi), thay cho một `source_id` cấp bài của ADR-0007 §2. Cần TL chốt Q5 của ADR-0007 theo hướng này.
- Công khai (BR-63): khối "Nguồn tham khảo" + nhãn AI (câu chữ Legal duyệt) + metadata máy đọc; FE **không render** bài `origin = AGGREGATION` thiếu `sources`/`aiDisclosure` (fail-closed như nhãn tài trợ).

### 6. Kiểm duyệt nhiều lớp (BA-01 §7.8, D2)

| Lớp | Ai | Chặn bằng |
|---|---|---|
| 0 — Máy | Checker chạy lại trên **revision hiện tại** khi tạo bài, gửi duyệt, duyệt (BR-80) | 422 `SIMILARITY_TOO_HIGH` / `GROUNDING_FAILED` |
| 1 — Biên tập | BTV chủ bài: checklist khi gửi duyệt (BR-81) | 422 `VALIDATION_FAILED` |
| 2 — Duyệt nội dung | APPROVER, không phải chủ bài, **không phải người yêu cầu AI viết** (BR-83) | checklist (BR-82) + `SOD_VIOLATION` |
| 3 — Xác nhận quyền | `content.rights_verify` (ADR-0007 §3) — chế độ/giấy phép **từng** nguồn còn hiệu lực, kiểu viết lại phù hợp | 422 `RIGHTS_INVALID` khi xuất bản (BR-64) |
| 4 — Hậu kiểm | Thư ký tòa soạn đọc mẫu hằng tuần; job theo dõi URL nguồn 72 giờ (BR-70/85) | đính chính/gỡ theo luồng hiện có |

- Tỷ lệ BTV sửa so với nháp < ngưỡng (đề xuất 5%) ⇒ cảnh báo "Bài gần như nguyên bản AI" trên màn duyệt (không chặn, lưu vào hồ sơ duyệt — BR-84).
- Ràng buộc chặn ở DB (theo cách 0307/0334): revision `ASSISTED` không thành public nếu thiếu nguồn; nguồn < M2 không thể nằm trong đầu vào nháp; `LICENSED_REWRITE` chỉ M3; revision `ASSISTED` không sang `IN_REVIEW`/`APPROVED` khi thiếu checklist.

### 7. Cờ, quyền, mã lỗi (đề xuất, chưa thêm vào nguồn sự thật)

- Cờ `ai.aggregation_drafts`: production **OFF**, người duyệt bật PO + Legal, phạm vi ghi P2 tới khi PO xếp hạng (không tự nâng). Kiểm cả trong transaction chuyển thành bài; worker cũng dừng soạn khi OFF.
- Quyền `aggregation.draft` (yêu cầu/sửa/chuyển nháp) — vai trò do PO/BA chọn (Q-AGG-07); `content.rights_verify` theo ADR-0007 §3. Khi duyệt: sửa `rbac.json` + `Permissions` + test.
- 4 mã lỗi mới cho `x-error-catalog`: `USAGE_MODE_INSUFFICIENT` (422), `SIMILARITY_TOO_HIGH` (422), `GROUNDING_FAILED` (422), `AI_BUDGET_EXCEEDED` (429). Khi duyệt: cập nhật `ErrorCatalog` + test khớp spec (hiện test giữ 27 mã).

### 8. Dữ liệu (migration mới khi được duyệt)

`agg_item_contents` (toàn văn M2/M3, có hạn xóa), `agg_draft_jobs` (hàng đợi + lease), `agg_drafts` (phiên bản nháp: chế độ, ghi chú BTV, người yêu cầu, model, phiên bản prompt, id mục đầu vào, đầu ra có cấu trúc, dẫn chứng, kết quả kiểm tra, token, chi phí, bài đã chuyển), `agg_draft_feedback` (BR-72), `ai_usage_audit` (chỉ thêm, dùng chung FR-AI-004), mở rộng `article_revisions` (`source_refs`, `ai_assistance`, `ai_draft_id`, `ai_edit_ratio`, `submit_attestation`, `review_attestation`, `ai_check_result`) và `articles.origin`.

## Điều kiện trước khi hiện thực

1. Biên bản Legal trả lời Q-AGG-01/02/04/05/13/14 (GĐ0 của BA-01) — đặc biệt được gửi nội dung nguồn ra nhà cung cấp nào, vùng xử lý nào.
2. TL chọn nhà cung cấp AI (Q-AGG-15) và chốt nguồn trên revision (Q-AGG-09, ADR-0007 Q5).
3. ADR-0007 §2–§4 (metadata quyền trên bài, xác nhận quyền, cổng xuất bản) được duyệt và hiện thực.
4. Ban biên tập có style guide + checklist (Q-AGG-08/16); PM/Finance chốt ngân sách (Q-AGG-10).
5. Bộ đánh giá (BA-01 §14: 100 cụm thật đã có bài BTV) để đo chất lượng trước khi mở cho người dùng nội bộ.

## Hệ quả

- **Được**: lộ trình GĐ2 rõ, tái dùng hạ tầng GĐ1 (worker, lease, bộ thu thập an toàn, chế độ khai thác, danh mục giấy phép); không có mã gọi AI hay dữ liệu ra ngoài trước khi Legal đồng ý.
- **Mất**: chưa có giá trị GĐ2 cho tới khi các điều kiện trên xong; hợp đồng nháp có thể đổi nhiều sau khi Legal trả lời.

## Phương án đã loại

| Phương án | Lý do loại |
|---|---|
| Hiện thực ngay sau cờ OFF với nhà cung cấp mặc định | Đặt sẵn đường gửi dữ liệu ra ngoài trước khi Legal/Security đồng ý; dễ bị bật nhầm |
| Gọi AI đồng bộ trong request API | Request dài, phụ thuộc nhà cung cấp làm chậm/treo API; không áp được ngân sách và retry |
| Để AI tự kiểm tra (một model vừa viết vừa chấm) | Không tất định; không chứng minh được với Legal; bộ chấm AI chỉ được thêm chặn |
| Một `source_id` cấp bài (ADR-0007 §2 hiện tại) | Bài tổng hợp có nhiều nguồn, nguồn đổi theo revision |
| Gộp ngay hợp đồng GĐ2 vào spec v0.3 chính | Kéo theo đổi `ErrorCatalog`/`FeatureFlagKeys`/test khi phạm vi còn chờ Legal; để file đề xuất riêng |

## Câu hỏi cần quyết

Các câu Q-AGG-01…16 của BA-01 §18, cộng:

| # | Ai | Câu hỏi |
|---|---|---|
| B-1 | TL | Hàng đợi nháp trong DB ở worker hiện có (đề xuất) hay đợi outbox WBS-020? |
| B-2 | Security | Toàn văn nguồn M2/M3: mã hóa ở mức cột? Ai đọc được? |
| B-3 | PO | Phạm vi cờ `ai.aggregation_drafts` (P1/P2)? |

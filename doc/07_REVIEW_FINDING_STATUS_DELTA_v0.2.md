# Cập nhật trạng thái các phát hiện liên quan — Review 06 → Addendum 07

| ID | Trạng thái sau cập nhật | Bằng chứng / hành động còn thiếu |
|---|---|---|
| CRV-001 — Legal | **PARALLEL_DELIVERY_APPROVED; PUBLIC_RELEASE_PENDING** | PO cho phép code song song; chưa có giấy phép hoặc legal opinion chứng minh quyền vận hành công khai |
| CRV-002 — Domain/brand/content rights | **DOMAIN_OWNER_ASSERTED; RIGHTS_EVIDENCE_PENDING** | Chủ dự án xác nhận domain thuộc khách; chưa coi đó là quyền nhãn hiệu, bản quyền media |
| CRV-003 — Governance baseline | **PARTIAL** | 07 addendum chưa thay thế signed baseline 01–05, cần PO/TL/QA sign-off |
| CRV-004 — FR→WBS | **STATIC_MAPPING_REMEDIATED; QA_AND_EFFORT_REVIEW_PENDING** | 84/84 FR có WBS exact, gồm 42 child task WBS-089..130; cần owners, QA tests, phân bổ capacity |
| CRV-005 — CMS/API | **CONTRACT_DRAFT_EXPANDED; INTEGRATION_TEST_PENDING** | 93 paths/106 operations, schema/refs tĩnh PASS; kiểm thử server chưa chạy |
| CRV-006 — Community API | **CONTRACT_DRAFT_EXPANDED; LEGAL_GATED** | Comment/like/report/pin/case/API có specification; default OFF trong public production |
| CRV-007 — Ads scope/budget | **PROPOSED_RESOLUTION** | Gói P0-ADS độc lập cộng đồng, WBS-131..135; còn PO duyệt scope, PM duyệt cost |
| CRV-008 — DB invariants | **PARTIAL** | Ad SQL tham chiếu + mô tả invariant; chưa migration/test transaction thực |
| CRV-009 — Member/identity | **PARTIAL** | OIDC adapter/API hướng dẫn; provider, consent/retention/verification còn chờ |
| CRV-010 — Video playback DTO | **SCHEMA_DRAFT_FIXED** | PlaybackGrant oneOf HLS/EXTERNAL_EMBED, chưa FE/BE integration test |
| CRV-012 — WBS/budget | **OPEN** | Estimate 05 vẫn là giả định; add-on sponsored-lite không tự động cộng vào Excel |
| Các phát hiện còn lại | **GIỮ NGUYÊN TRẠNG THÁI THEO REVIEW 06** | Không tuyên bố đóng nếu chưa có bằng chứng nghiệm thu |

**Kết luận:** Không dùng từ `CLOSED` chỉ vì đã viết thêm tài liệu. Phải có test evidence/approval theo từng finding.

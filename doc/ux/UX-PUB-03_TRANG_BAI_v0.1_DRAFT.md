# UX-PUB-03 — Trang bài viết — thiết kế v0.1

**Trạng thái:** DRAFT, chờ UX/PO duyệt (WBS-004). Ngày 09/11/2026.
**Truy vết:** `FR-NEWS-001` (tiêu đề, sapo, nội dung, tác giả, ngày, phân loại) · `FR-NEWS-002` (URL ổn định, slug cũ → 308) · `FR-NEWS-008` (nhãn tài trợ) · `FR-DISC-004/007` (bài đã gỡ không lộ; deep link, 404) · `WBS-029` · `NFR-SEO-001`.
**Hợp đồng:** `GET /articles/{slug}` → `Article` (đã hiện thực), `GET /home` (Đọc thêm), `GET /categories`, `GET /ads/placements?placementCodes=article_bottom`.
**Code:** `frontend/apps/web/src/app/[site]/articles/[slug]/page.tsx`, `src/lib/article.ts`, `src/components/article/ArticleBody.tsx`, `src/app/[site]/not-found.tsx`.

## Bố cục

```
DESKTOP ≥ 1024px                                      MOBILE < 768px
┌───────────────────────────────────────────────┐     ┌──────────────────────┐
│ Header (chung)                                │     │ Header               │
│ Trang chủ › Chuyên mục                        │     │ Trang chủ › Chuyên mục│
│ ┌─────────────────────────────┐ ┌───────────┐ │     │ [Nội dung tài trợ]   │
│ │ [Nội dung tài trợ] (nếu có) │ │ ĐỌC THÊM  │ │     │ TIÊU ĐỀ (28px)       │
│ │ TIÊU ĐỀ H1 (38px)           │ │ [ảnh] tin │ │     │ Sapo (19px, đậm)     │
│ │ Sapo (20px, đậm)            │ │ [ảnh] tin │ │     │ Tác giả · giờ · …    │
│ │ Tác giả · 14:35 09/11 · …   │ │ … 6 bài   │ │     │ Nội dung 18px/1.75   │
│ │ ─────────────────────────── │ │ (sticky)  │ │     │ [Quảng cáo]          │
│ │ ảnh 16:9 (nếu có)           │ └───────────┘ │     │ ĐỌC THÊM             │
│ │ Nội dung 18px/1.75, ≤ 760px │               │     │ Footer               │
│ │ [Quảng cáo · article_bottom]│               │     └──────────────────────┘
│ └─────────────────────────────┘               │
│ Footer                                        │
└───────────────────────────────────────────────┘
```

## Quy tắc

- **Thân bài** chỉ hiển thị văn bản (React tự escape, không chèn HTML).
  - Khối `paragraph` hiển thị là `<p>`, giữ nguyên ngắt dòng.
  - `heading` cấp 2 hoặc 3 hiển thị là `<h2>`/`<h3>`.
  - `quote` hiển thị là `<blockquote>`.
  - Kiểu khối khác (ảnh, nhúng, sau editor UX-CMS-02) **bị bỏ qua**, không hiện sai.
- **Trạng thái:**
  - API 308 (slug cũ): chuyển hướng 308 sang `/articles/{slug-mới}`.
  - API 404 hoặc 410 (đã gỡ / hết quyền): trang "Không tìm thấy", HTTP **404**. Next.js không trả được 410 từ trang; nếu SEO cần đúng 410 thì xử lý ở edge.
  - Slug sai định dạng: 404, không gọi API.
  - API lỗi: khối "Chưa tải được bài viết".
- **Tài trợ:** dải "Nội dung tài trợ" ở đầu bài. Không có tùy chọn ẩn. Bài `SPONSORED` thiếu nhãn sẽ không hiển thị (fail-closed).
- **Đọc thêm:** tối đa 6 bài mới nhất lấy từ `GET /home`, trừ bài đang xem. Hợp đồng v0.3 chưa có API "bài liên quan theo chủ đề".
- **Video liên quan** (`relatedVideoIds`): ẩn tới khi có API video công khai (FR-DISC-005).
- **SEO:**
  - `<title>`, description = sapo, canonical = origin site + `/articles/{slug}`.
  - OpenGraph `article` có `publishedTime`.
  - JSON-LD `NewsArticle` + `BreadcrumbList`.
  - Dữ liệu mẫu và môi trường không index thì đặt `noindex`.
- **Local khi API tắt:** dựng bài từ dữ liệu mẫu của trang chủ, có dải "Dữ liệu mẫu", để link từ trang chủ mẫu không gãy.

## Điểm còn mở

1. URL công khai `/articles/{slug}` chưa chốt (AIR-026); đổi ở `src/lib/routes.ts`.
2. `Article` chưa có `updatedAt`, nên chưa hiện "cập nhật lúc…" (FR-NEWS-001 "khi áp dụng").
3. Chưa có nút chia sẻ và ảnh đại diện (media WBS). Trang chuyên mục `/categories/{slug}` (WBS-028) và Tin mới `/articles` đã có.
   - Dùng `getCategoriesSlugArticles` / `listPublicArticles`, phân trang bằng `?cursor=`.
   - Trang sau đặt `noindex, follow`, canonical về trang đầu.
   - Cursor hỏng thì chuyển về trang đầu; chuyên mục không có hoặc đã tắt trả 404.
4. Mã slot `article_bottom` chờ WBS-131 duyệt.

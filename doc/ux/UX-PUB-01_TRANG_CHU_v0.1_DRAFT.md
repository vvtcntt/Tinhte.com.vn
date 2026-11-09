# UX-PUB-01 — Trang chủ (site `www`) — thiết kế v0.1

**Trạng thái:** DRAFT, chờ UX/PO duyệt (WBS-004 khóa 18 màn hình). Ngày 09/11/2026.
**Truy vết:** `FR-DISC-003` (vùng tin mới, tin nổi bật, video nổi bật) · `WBS-027` (SSR, responsive, đổi block không cần deploy) · `FR-ID-001` (khách đọc không cần tài khoản) · `FR-DISC-004/005`, `FR-VID-010` (không lộ nội dung đã gỡ) · `FR-ADS-001/002`, `FR-NEWS-008`, `WBS-133` (nhãn tài trợ, 1–2 slot tĩnh, không CLS) · `NFR-SEO-001`, `WBS-052`.
**Hợp đồng:** `GET /home` → `HomePage`, `GET /categories`, `GET /ads/placements` (OpenAPI v0.3 DRAFT).
**Code:** `frontend/apps/web/src/app/[site]/page.tsx`, `frontend/apps/web/src/components/home/*`, `frontend/apps/web/src/lib/*`.

---

## Bước 1 — Mục tiêu và giới hạn

| | |
|---|---|
| Người dùng chính | Khách vãng lai đọc tin công nghệ, không đăng nhập (FR-ID-001). |
| Việc phải làm được trong 5 giây | Thấy tin quan trọng nhất, lướt tin mới, vào xem Shorts. |
| Đo thành công | Tỉ lệ click vào bài/video từ trang chủ (`article_impression`, `video_impression` — FR-ANL, làm sau). |
| Ngoài phạm vi | Bình luận, đăng nhập thành viên (P0*, cờ OFF), gợi ý cá nhân hóa (P1), quảng cáo video Shorts (P1). |

Ràng buộc từ tài liệu:
- **SSR toàn trang** (WBS-027). Không cần JS để đọc nội dung.
- **Không viết cứng host** (ADR-0001): link nội bộ dùng đường dẫn tương đối; gọi API theo site của request.
- **Nhãn tài trợ là bắt buộc** khi `contentKind = SPONSORED`; FE không được ẩn. Item `SPONSORED` thiếu `sponsorLabel`, hoặc thiếu/không rõ `contentKind` → **bỏ khỏi trang** (fail-closed).
- **Quảng cáo:** chỉ hiện slot mà API trả về. Cờ `ads.display_slots` OFF → API trả rỗng → không có khung trống. Có slot thì giữ chỗ theo `width/height` ngay từ SSR nên không gây CLS. Không popup, không autoplay.
- Không hiển thị số giấy phép khi chưa có (CLAUDE.md §3).

## Bước 2 — Kiến trúc thông tin

Thứ tự vùng, từ trên xuống:

1. **Header**: logo → `/`; thanh chuyên mục (`GET /categories`); link **Shorts**; nút **Tìm kiếm** (UX-PUB-07). Không có nút đăng nhập khi `memberLoginEnabled = false`.
2. **Tin nổi bật** (`featuredArticles`, tối đa 5): 1 bài chính lớn + 4 bài phụ.
3. **Video nổi bật** (`featuredVideos`, tối đa 8): dải thẻ dọc 9:16, cuộn ngang.
4. **Tin mới** (`recentArticles`, tối đa 12, loại trùng với Tin nổi bật) + cột phụ.
   - Slot quảng cáo `home_feed` chèn sau bài thứ 6 (desktop + mobile; số chẵn để khớp lưới 2 cột).
   - Cột phụ desktop: slot `home_sidebar` (dính khi cuộn).
   - Cuối danh sách: nút **Xem thêm tin mới** → trang danh sách có phân trang (`GET /articles`, cursor).
5. **Footer**: logo, link pháp lý (UX-PUB-09), dòng bản quyền. Khu giấy phép để trống tới khi Legal cấp.

Lý do thứ tự: tin nổi bật do biên tập chọn nên đứng đầu; video đặt ngay sau để Shorts (mảng mới) được thấy mà không chen giữa luồng đọc; tin mới là phần dài nhất nên ở cuối, có cột phụ cho quảng cáo.

## Bước 3 — Bố cục theo breakpoint

Container tối đa 1200px, lề 16px (mobile) / 24px (≥ 768px).

```
DESKTOP ≥ 1024px
┌──────────────────────────────────────────────────────────────┐
│ [Logo]  Chuyên mục 1 · 2 · 3 · 4 · 5 …        Shorts  [🔍]   │
├──────────────────────────────────────────────────────────────┤
│ TIN NỔI BẬT                                                   │
│ ┌───────────────────────────────┐ ┌──────────────────────┐   │
│ │  ảnh 16:9                     │ │ [ảnh] tiêu đề phụ 1  │   │
│ │                               │ │ [ảnh] tiêu đề phụ 2  │   │
│ │  TIÊU ĐỀ BÀI CHÍNH (H2 lớn)   │ │ [ảnh] tiêu đề phụ 3  │   │
│ │  sapo 2 dòng · thời gian      │ │ [ảnh] tiêu đề phụ 4  │   │
│ └───────────────────────────────┘ └──────────────────────┘   │
│ VIDEO NỔI BẬT                                    Xem Shorts → │
│ [9:16][9:16][9:16][9:16][9:16][9:16] → cuộn ngang            │
│ TIN MỚI                                                       │
│ ┌──────────────────────────────────────┐ ┌──────────────┐    │
│ │ [ảnh 16:9] tiêu đề · sapo · thời gian│ │ Quảng cáo    │    │
│ │ … 6 bài                              │ │ home_sidebar │    │
│ │ ┌ Quảng cáo · home_feed ──────────┐  │ │ (sticky)     │    │
│ │ … 6 bài                              │ └──────────────┘    │
│ │        [ Xem thêm tin mới ]          │                     │
│ └──────────────────────────────────────┘                     │
├──────────────────────────────────────────────────────────────┤
│ Footer: logo · Giới thiệu · Điều khoản · Quyền riêng tư · LH │
└──────────────────────────────────────────────────────────────┘

MOBILE < 768px
┌────────────────────────┐
│ [Logo]          [🔍]   │
│ ‹chip› ‹chip› ‹chip› → │  chuyên mục cuộn ngang
├────────────────────────┤
│ ảnh 16:9               │  bài chính
│ TIÊU ĐỀ                │
│ thời gian              │
│ [ảnh] phụ 1            │  4 bài phụ dạng danh sách
│ …                      │
│ VIDEO NỔI BẬT          │
│ [9:16][9:16][9:…  →    │  scroll-snap
│ TIN MỚI                │
│ [ảnh] tiêu đề          │
│ … (quảng cáo sau bài 6)│
│ [ Xem thêm tin mới ]   │
│ Footer                 │
└────────────────────────┘
```

Desktop không có slot `home_sidebar` (cờ quảng cáo OFF — mặc định production): Tin mới chia **lưới 2 cột** thay vì để trống cột phải.
Tablet 768–1023px: Tin nổi bật giữ 2 cột; Tin mới một cột, slot `home_sidebar` ẩn.

## Bước 4 — Hệ thống thị giác (tạm, chờ WBS-016)

Màu lấy từ logo; mọi màu là token CSS, có dark mode theo `prefers-color-scheme`.

| Token | Sáng | Tối | Dùng cho |
|---|---|---|---|
| `--brand` | `#1c6fc5` | `#5aa2f0` | logo, link, tiêu đề vùng |
| `--brand-2` | `#1f9e73` | `#3cc595` | điểm nhấn (gạch dưới tiêu đề vùng, badge Shorts) |
| `--bg` / `--surface` | `#f5f7fa` / `#fff` | `#0f1216` / `#171b21` | nền |
| `--text` / `--muted` | `#14181f` / `#5b6472` | `#e8ecf1` / `#9aa4b2` | chữ |
| `--sponsor-bg` / `--sponsor-text` | `#fff4d6` / `#7a4b00` | `#3a2c0b` / `#ffd479` | nhãn tài trợ |

Chữ: font hệ thống (CSP `font-src 'self'`, không tải font ngoài). Tiêu đề bài chính 28–32px/1.25, tiêu đề thẻ 17px/1.35, sapo 15px, meta 13px. Tiêu đề kẹp 2–3 dòng.
Bo góc 10px; ảnh luôn có tỉ lệ cố định (16:9, 4:3, 9:16) để không nhảy bố cục.

## Bước 5 — Thành phần

| Thành phần | Nội dung | Ghi chú |
|---|---|---|
| `SiteHeader` | Logo, nav chuyên mục, Shorts, Tìm kiếm | Nav cuộn ngang trên mobile; link "bỏ qua tới nội dung". |
| `LeadStory` | Ảnh 16:9, tiêu đề H2, sapo, thời gian, nhãn tài trợ | Ảnh `fetchpriority=high` (LCP). |
| `StoryCard` (`compact` / `row`) | Ảnh nhỏ, tiêu đề, thời gian, (sapo ở `row`) | Ảnh `loading=lazy`. |
| `VideoCard` | Poster 9:16, biểu tượng phát, thời lượng `m:ss`, tiêu đề | Không autoplay trên trang chủ (FR-VID-011/012). |
| `SponsorBadge` | Chữ `sponsorLabel` do backend trả | Không có props để ẩn. |
| `AdSlot` | Chữ "Quảng cáo" + ảnh creative, `rel="sponsored noopener"` | Chỉ render khi có slot; `aspect-ratio` theo width/height. |
| `RelativeTime` | "5 phút trước" / "hôm qua" / ngày | Luôn kèm `<time datetime>`. |
| `Thumb` (không ảnh) | Nền gradient thương hiệu | Dùng khi `thumbnailUrl`/`posterUrl` null. |

## Bước 6 — Trạng thái

| Tình huống | Hiển thị |
|---|---|
| Đủ dữ liệu | Như bố cục trên. |
| Một vùng rỗng | Ẩn cả vùng đó (kể cả tiêu đề). |
| Tất cả rỗng | Khối trống: "Chưa có bài viết nào được xuất bản." |
| `/home` lỗi (staging/prod) | Khối lỗi kèm nút "Tải lại"; header/footer vẫn hiện. |
| `/home` lỗi (môi trường `local`) | Dữ liệu mẫu + dải cảnh báo "Dữ liệu mẫu" để dựng giao diện khi API chưa có. Không bao giờ dùng ở staging/production. |
| `/categories` lỗi/rỗng | Ẩn nav chuyên mục, giữ Shorts + Tìm kiếm. |
| `/ads/placements` lỗi hoặc cờ OFF | Không có slot, không khung trống. |
| Ảnh lỗi/null | Thumb gradient, bố cục giữ nguyên. |
| Item `SPONSORED` thiếu nhãn / `contentKind` lạ | Bỏ item (fail-closed). |

## Bước 7 — Dữ liệu, cache, SEO, truy cập

- **Gọi API phía server**: `{TT_API_ORIGIN}/api/v1/home` kèm `X-Forwarded-Host` = host của site (suy ra từ Site Registry). `revalidate: 60s` — biên tập đổi tin nổi bật thì tối đa 1 phút sau trang chủ cập nhật; gỡ bài phải purge cache theo SLA (FR-DISC-004, việc của WBS-112).
- **SEO**: `<title>` = nhãn site; canonical = origin của site + `/`; OpenGraph; JSON-LD `WebSite` + `Organization`. Môi trường không index (staging/local) đã có `X-Robots-Tag` từ proxy.
- **Truy cập (WCAG 2.1 AA)**: một `<h1>` (ẩn trực quan, tên site), mỗi vùng là `<section aria-labelledby>`, thẻ bài là `<article>` với link bao tiêu đề; ảnh có `alt` (ảnh minh họa dùng `alt=""` vì tiêu đề đã nằm cạnh); dải video cuộn ngang dùng được bằng bàn phím; focus ring rõ; tương phản ≥ 4.5:1; tôn trọng `prefers-reduced-motion`.

## Bước 8 — Điểm còn mở (cần PO/TL/UX quyết)

1. **Block cấu hình qua CMS (WBS-027).** Đã có phần ghim **Tin nổi bật** theo site: `PUT /admin/home/{siteKey}/featured-articles`, migration 0322; thiếu thì bù bằng bài mới nhất. Bố cục vẫn là 3 vùng cố định. Nếu PO cần thêm/bớt/sắp xếp cả block thì phải sửa spec v0.3 trước. Màn hình CMS để ghim bài chưa làm.
2. **Đường dẫn công khai** bài/video/chuyên mục chưa chốt (AIR-026 OPEN). Code gom ở `src/lib/routes.ts`: `/articles/{slug}`, `/shorts/{videoId}`, `/categories/{slug}`, `/search`.
3. **Mã placement quảng cáo** `home_feed`, `home_sidebar` và vị trí "sau bài thứ 6" là đề xuất; WBS-131 (PO + Commercial + Legal) chốt.
4. **Số lượng mỗi vùng** (5 / 8 / 12) là mặc định FE; nên để backend quyết bằng cấu hình biên tập.
5. **Tên chuyên mục trên thẻ bài**: `ArticleSummary` chưa có `categorySlug/categoryName`. Muốn hiện chuyên mục trên thẻ thì phải thêm vào hợp đồng.
6. **Backend:** đã có `GET /home` và `GET /categories`. `featuredVideos` trả rỗng tới khi có feed video công khai (WBS-041/046).
7. **Analytics impression** (FR-ANL-001/002) chưa gắn.

# UX-PUB-01 — Trang chủ (site `www`) — thiết kế v0.1

**Trạng thái:** DRAFT, chờ UX/PO duyệt (WBS-004 khóa 18 màn hình). Ngày 09/11/2026.
**Cập nhật 10/11/2026 — giao diện đợt 1:** font Be Vietnam Pro + token chữ/khoảng cách, bỏ hộp thẻ, cột phải 300px, logo SVG có bản tối, menu "Thêm", nút ‹ › cho dải video, footer nhiều cột, luôn có đường sang danh sách tin.
**Cập nhật 10/11/2026 — đợt 2, vùng quảng cáo:** dựng sẵn 6 vùng banner ①–⑥ phía frontend (Bước 3 "Vùng quảng cáo"). Khi backend có `GET /ads/placements` và CMS tạo placement, banner tự hiện, không cần deploy. Không có banner thì trang y hệt đợt 1. Mã placement vẫn là đề xuất, chờ WBS-131 chốt.
**Cập nhật 10/11/2026 — đợt 3 (DRAFT, chờ TL/PO/BA duyệt):** hợp đồng + DB quảng cáo theo site, tách vị trí khỏi đặt chỗ, nhiều kích thước theo breakpoint (OpenAPI v0.3 changelog §16, migration 0350); backend có `GET /ads/placements`; FE chuyển sang `creatives[]`, giữ 10 mã đề xuất và hành vi "không có banner thì không có khung".
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

Thứ tự vùng, từ trên xuống (①–⑥ là vùng quảng cáo, chỉ hiện khi có banner — xem Bước 3):

1. **Header**: logo → `/`; thanh chuyên mục (`GET /categories`, mục đang mở có `aria-current="page"`); link **Shorts**; nút **Tìm kiếm** (UX-PUB-07). Desktop hiện 6 chuyên mục đầu, phần còn lại vào menu **Thêm**; mobile cuộn ngang toàn bộ. Không có nút đăng nhập khi `memberLoginEnabled = false`.
   - ① `home_top` / `home_top_mobile`: dải banner ngay dưới header.
2. **Tin nổi bật** (`featuredArticles`, tối đa 5): 1 bài chính lớn + 4 bài phụ.
   - ② `home_hero_side`: có banner thì cột phải còn 3 bài phụ + banner 300×250; tin nổi bật thứ 5 chuyển xuống Tin mới.
3. **Video nổi bật** (`featuredVideos`, tối đa 8): dải thẻ dọc 9:16, cuộn ngang; từ 768px có nút ‹ › ở tiêu đề vùng (chỉ hiện khi dải tràn).
   - ③ `home_mid` / `home_mid_mobile`: giữa Video nổi bật và Tin mới.
4. **Tin mới** (`recentArticles`, tối đa 12, loại trùng với Tin nổi bật) + cột phụ.
   - ④ `home_feed` / `home_feed_mobile` chèn sau bài thứ 6 (số chẵn để khớp lưới 2 cột).
   - ⑤ cột phụ desktop: `home_sidebar` (dính khi cuộn).
   - Tiêu đề vùng có link **Xem tất cả**; cuối danh sách: nút **Xem thêm tin mới** → trang danh sách có phân trang (`GET /articles`, cursor).
   - Tin mới rỗng (mọi bài đang nằm ở Tin nổi bật): vẫn hiện nút **Xem tất cả tin** để trang chủ luôn có đường sang danh sách.
   - ⑥ `home_bottom` / `home_bottom_mobile`: dải banner cuối nội dung, trên footer.
5. **Footer**: logo + mô tả ngắn; cột Chuyên mục, Khám phá (Tin mới, Shorts, Tìm kiếm), Thông tin (link pháp lý UX-PUB-09); dòng bản quyền. Khu giấy phép để trống tới khi Legal cấp.

Lý do thứ tự: tin nổi bật do biên tập chọn nên đứng đầu; video đặt ngay sau để Shorts (mảng mới) được thấy mà không chen giữa luồng đọc; tin mới là phần dài nhất nên ở cuối, có cột phụ cho quảng cáo.

## Bước 3 — Bố cục theo breakpoint

Container tối đa 1200px, lề 16px (mobile) / 24px (≥ 768px).

```
DESKTOP ≥ 1024px  (①–⑥ chỉ có khi API trả banner)
┌──────────────────────────────────────────────────────────────────┐
│ [Logo]  Chuyên mục 1 · 2 · 3 · 4 · 5 · 6  Thêm ▾  Shorts [Tìm]   │
├──────────────────────────────────────────────────────────────────┤
│ ┌ QUẢNG CÁO ───────────────────────────────────────────────────┐ │
│ │ ① home_top 970×90, căn giữa khung, không lazy-load           │ │
│ └──────────────────────────────────────────────────────────────┘ │
│ ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ │
│ Tin nổi bật                                                      │
│ ┌─────────────────────────────────────┐ ┌──── 300px ────┐        │
│ │  ảnh 16:9                           │ │ [ảnh] phụ 1   │        │
│ │                                     │ │ [ảnh] phụ 2   │        │
│ │                                     │ │ [ảnh] phụ 3   │        │
│ │  TIÊU ĐỀ BÀI CHÍNH (36px)           │ │┌QUẢNG CÁO────┐│        │
│ │  sapo · thời gian                   │ ││② hero_side  ││        │
│ │                                     │ ││   300×250   ││        │
│ └─────────────────────────────────────┘ └┴─────────────┴┘        │
│ ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ │
│ Video nổi bật                             [‹][›] Xem Shorts →    │
│ [9:16][9:16][9:16][9:16][9:16][9:16] → cuộn ngang                │
│ ┌ QUẢNG CÁO ───────────────────────────────────────────────────┐ │
│ │ ③ home_mid 970×250                                           │ │
│ └──────────────────────────────────────────────────────────────┘ │
│ ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ │
│ Tin mới                                        Xem tất cả →      │
│ ┌──────────────────────────────────────┐ ┌──────────────┐        │
│ │ [ảnh 16:9] tiêu đề · sapo · thời gian│ │ QUẢNG CÁO    │        │
│ │ … 6 bài                              │ │ ⑤ home_      │        │
│ │ ┌ QUẢNG CÁO ──────────────────────┐  │ │   sidebar    │        │
│ │ │ ④ home_feed 728×90              │  │ │   300×600    │        │
│ │ └─────────────────────────────────┘  │ │ (dính khi    │        │
│ │ … 6 bài                              │ │  cuộn)       │        │
│ │        [ Xem thêm tin mới ]          │ └──────────────┘        │
│ └──────────────────────────────────────┘                         │
│ ┌ QUẢNG CÁO ───────────────────────────────────────────────────┐ │
│ │ ⑥ home_bottom 970×90                                         │ │
│ └──────────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────────┤
│ Footer: logo · Chuyên mục · Khám phá · Thông tin · ©             │
└──────────────────────────────────────────────────────────────────┘

MOBILE < 768px
┌──────────────────────────┐
│ [Logo]     Shorts [Tìm]  │
│ ‹chip› ‹chip› ‹chip› →   │  chuyên mục cuộn ngang
├──────────────────────────┤
│ ┌QUẢNG CÁO─────────────┐ │
│ │① home_top_mobile     │ │  320×100, không lazy-load
│ └──────────────────────┘ │
│ ảnh 16:9                 │  bài chính
│ TIÊU ĐỀ · thời gian      │
│ [ảnh] phụ 1 … 3 (4)      │  3 bài phụ khi có ②, 4 khi không
│ ┌QUẢNG CÁO─────────────┐ │
│ │② home_hero_side      │ │  300×250 (cùng mã với desktop)
│ └──────────────────────┘ │
│ VIDEO NỔI BẬT            │
│ [9:16][9:16][9:…  →      │  scroll-snap
│ ┌QUẢNG CÁO─────────────┐ │
│ │③ home_mid_mobile     │ │  300×250
│ └──────────────────────┘ │
│ TIN MỚI                  │
│ [ảnh] tiêu đề × 6        │
│ ┌QUẢNG CÁO─────────────┐ │
│ │④ home_feed_mobile    │ │  300×250
│ └──────────────────────┘ │
│ [ảnh] tiêu đề × 6        │
│ [ Xem thêm tin mới ]     │
│ ┌QUẢNG CÁO─────────────┐ │
│ │⑥ home_bottom_mobile  │ │  320×100
│ └──────────────────────┘ │
│ Footer                   │
└──────────────────────────┘
⑤ home_sidebar không có trên mobile/tablet (< 1024px).
```

Cột phải desktop rộng cố định **300px** ở mọi vùng (khớp banner 300×250 / 300×600). Ở Tin nổi bật, không có ② thì tin phụ đầu tiên hiện ảnh lớn phía trên tiêu đề để cột phải cao gần bằng bài chính; có ② thì cả 3 tin phụ dạng ảnh nhỏ, banner ở cuối cột (đo ở 1440px với dữ liệu mẫu: hai cột cao bằng nhau, 682px).
Desktop không có slot ⑤ `home_sidebar` (cờ quảng cáo OFF — mặc định production): Tin mới chia **lưới 2 cột** thay vì để trống cột phải.
Tablet 768–1023px: bài chính trải hết chiều ngang, 4 tin phụ xếp lưới 2 cột bên dưới (có ②: 3 tin phụ xếp dọc bên trái, banner 300px bên phải); Tin mới một cột, ⑤ ẩn; ① ③ ④ ⑥ dùng banner desktop thu nhỏ vừa khung.

### Vùng quảng cáo ①–⑥ (đợt 2)

Mã placement là **đề xuất, chờ WBS-131 (PO + Commercial + Legal) chốt** — "Điểm còn mở" số 3. Nguồn trong code: `HOME_AD_CODES`, `HOME_AD_SIZES` ở `frontend/apps/web/src/lib/home-model.ts`.
Đợt 2 tách mã desktop/mobile (hậu tố `_mobile`) vì hợp đồng cũ chỉ cho một kích thước mỗi mã. **Đợt 3 (DRAFT):** mỗi slot có `creatives[]`, tối đa một banner cho mỗi breakpoint `MOBILE` (< 768px), `TABLET` (768–1023px), `DESKTOP` (≥ 1024px). FE **vẫn giữ 10 mã** tới khi WBS-131 chốt; `HOME_AD_BREAKPOINTS` (`lib/home-model.ts`) quy định mã nào được hiện ở breakpoint nào: mã `_mobile` chỉ MOBILE, mã không hậu tố TABLET + DESKTOP, ② mọi màn hình, ⑤ chỉ DESKTOP. Banner API trả cho breakpoint mà mã không được hiện thì bị bỏ qua. Nếu WBS-131 gộp cặp mã, chỉ cần sửa hai hằng số này.

| # | Mã (đề xuất) | Vị trí | ≥ 768px (desktop, tablet) | < 768px (mobile) | Tải ảnh |
|---|---|---|---|---|---|
| ① | `home_top` · `home_top_mobile` | dưới header, trên Tin nổi bật | 970×90 | 320×100 | **ngay** (màn hình đầu, không lazy; không `fetchpriority=high` để không tranh với ảnh LCP) |
| ② | `home_hero_side` (một mã) | cuối cột phải Tin nổi bật | 300×250 | 300×250, sau các tin phụ | lazy |
| ③ | `home_mid` · `home_mid_mobile` | giữa Video nổi bật và Tin mới | 970×250 | 300×250 | lazy |
| ④ | `home_feed` · `home_feed_mobile` | sau tin mới thứ 6 | 728×90 | 300×250 | lazy |
| ⑤ | `home_sidebar` | cột phải Tin mới, dính khi cuộn | 300×600, **chỉ ≥ 1024px** | không có | lazy |
| ⑥ | `home_bottom` · `home_bottom_mobile` | cuối nội dung, trên footer | 970×90 | 320×100 | lazy |

Mười mã = đúng mức tối đa `placementCodes` (`maxItems: 10`) của một lần gọi `GET /ads/placements`; thêm vùng nữa thì phải gọi hai lần hoặc sửa spec (test `home-model.test.ts` chặn khi vượt).
Kích thước ở bảng là đề xuất khi tạo placement. FE không ép: khung giữ chỗ theo `width/height` của từng banner API trả. Với hợp đồng đợt 3, một vị trí có thể cho phép nhiều kích thước (ví dụ ① DESKTOP 970×90 **và** 970×250, TABLET 728×90): chiến dịch lớn chỉ cần đặt chỗ với banner 970×250, không cần deploy (khi đó bài chính bị đẩy xuống thêm ~160px — Commercial cân nhắc).
Thiếu banner TABLET thì khung TABLET dùng banner DESKTOP của cùng mã thu nhỏ vừa khung (và ngược lại) — giữ hành vi đợt 2. MOBILE không mượn banner desktop.

**Hiển thị**
- Khung nền `--surface-2` bo góc, nhãn **QUẢNG CÁO** (chữ `label` do API trả; rỗng thì dùng "Quảng cáo") luôn ở trên creative. Thẻ bài không có nền/khung nên không nhầm quảng cáo với tin.
- Creative hiện đúng cỡ (không phóng to), căn giữa khung, chỉ thu nhỏ khi khung hẹp hơn. Ở cột 300px (②⑤) khung bỏ lề ngang để banner 300px không bị thu còn 284px.
- Link creative: `target="_blank" rel="sponsored noopener"`, chỉ https. Không popup, không autoplay, không âm thanh.
- Khoảng cách dùng token sẵn có: ① cách Tin nổi bật `--sp-5`; ③ ⑥ cách vùng kề `--sp-7` (bằng khoảng cách giữa các vùng); ④ theo nhịp `--sp-4` của danh sách.

**Không tải cả hai ảnh, không CLS**
- Mỗi banner khác nhau là một `<aside>` riêng; banner giống hệt nhau ở nhiều breakpoint gộp một khung (`resolveAdZone`, `lib/ads.ts`). Khung mang lớp `ad-slot--no-{mobile|tablet|desktop}` cho breakpoint nó không hiện; CSS viết mobile-first bằng `min-width` ở mốc 768/1024px nên tổ hợp lớp nào cũng đúng, không có độ rộng nào hiện hai khung của cùng vùng.
- Ảnh nằm trong `<picture>` có `<source media="(các khoảng bị ẩn)">` trỏ GIF 1×1 `data:` → ở màn hình không hiện, trình duyệt chọn GIF và **không tải creative**, kể cả ① không lazy. Đã kiểm lại sau đợt 3 (banner mẫu, Chromium của app): 375px chỉ tải `home_top_mobile`; 768px và 820px chỉ tải `home_top`; 1440px chỉ tải `home_top` (ảnh lazy dưới màn hình đầu chưa tải); khung hiện đúng 5 vùng mobile / 5 vùng tablet (không ⑤) / 6 vùng desktop.
- `width`/`height` + `aspect-ratio` có ngay từ SSR → khung giữ chỗ trước khi ảnh tải.

**Quy tắc vùng nào được hiện** (`homeAdPlan`, có test)

| Điều kiện | Kết quả |
|---|---|
| API không trả slot của mã đó (cờ `ads.display_slots` OFF, chưa tạo placement, hết lịch, API lỗi, URL không an toàn) | Không render gì — không khung, không khoảng trống. |
| Vùng chỉ có banner cho một số breakpoint (ví dụ chỉ bản desktop, hoặc chỉ mã `_mobile`) | Breakpoint còn lại không có gì ở vùng đó (TABLET/DESKTOP mượn banner của nhau trong cùng mã). |
| ② có banner nhưng không phủ đủ MOBILE + TABLET + DESKTOP | Coi như không có ② (bố cục cột phải đổi ở mọi màn hình, thiếu một màn hình sẽ để trống cột). |
| Trang không có nội dung (khối "Chưa có bài viết") | Không quảng cáo nào. |
| ② có banner | Tin nổi bật còn 4 bài (1 chính + 3 phụ); bài thứ 5 chuyển xuống Tin mới đúng vị trí theo thời gian xuất bản (cũ hơn cả 12 bài thì chỉ còn ở trang danh sách). |
| ② có banner nhưng không có tin phụ nào | Không hiện ② (cột phải không chỉ có quảng cáo). |
| ③: Tin mới dưới 10 bài (`HOME_MID_AD_MIN_RECENT`) | Bỏ ③ để giữ mật độ mobile ≤ ~30%. |
| ③: không có Video nổi bật mà có ② | Bỏ ③ — hai banner sẽ đứng sát nhau. Không có ② thì ③ nằm giữa Tin nổi bật và Tin mới. |
| ④: Tin mới ≤ 6 bài | Bỏ ④ (banner phải nằm giữa các tin, không ở cuối danh sách). |
| ⑤: Tin mới rỗng | Bỏ ⑤ (đi cùng vùng Tin mới). |

**Mật độ quảng cáo trên mobile** (đo ở 390px, dữ liệu mẫu, tổng chiều cao khung quảng cáo / chiều cao `<main>`): đủ 5 vùng với 11 tin mới **27,8%**; với 7 tin mới và còn ③ thì 31,2% → sinh ra quy tắc ③ cần ≥ 10 tin mới (7 tin không ③: 25,7%). Backend trả đủ 12 tin mới thì ③ luôn hiện.
**Ảnh LCP:** ① đẩy bài chính xuống ~150px (desktop) / ~160px (mobile); ảnh bài chính vẫn nằm trọn trong màn hình đầu ở 1440×900 và 390×844.

**Trạng thái "không có banner"** — mặc định production vì cờ `ads.display_slots` OFF và backend chưa có `GET /ads/placements`: trang **y hệt đợt 1** (không khung, không khoảng trống, 4 tin phụ, Tin mới lưới 2 cột ở desktop). Đã so: không có `.ad-slot` nào trong HTML.

**Xem thử trên máy dev:** `/?demo-ads=1` (hoặc liệt kê mã: `/?demo-ads=home_top,home_mid`) thay slot bằng banner SVG mẫu ghi rõ vùng/mã/kích thước, kèm dải cảnh báo "Quảng cáo mẫu". Chỉ có tác dụng khi `TT_SITE_ENV=local`; route ảnh `/demo-ads/{mã}.svg` trả 404 ở mọi môi trường khác (`lib/demo-ads.ts`).
**Không làm ở đợt này:** skin hai bên trang, banner dính đáy màn hình mobile (P1).

## Bước 4 — Hệ thống thị giác (tạm, chờ WBS-016)

Màu lấy từ logo; mọi màu, cỡ chữ, khoảng cách là token CSS (`frontend/apps/web/src/app/globals.css`), có dark mode theo `prefers-color-scheme`.

| Token | Sáng | Tối | Dùng cho |
|---|---|---|---|
| `--brand` | `#1c6fc5` | `#5aa2f0` | **màu tương tác duy nhất**: link, nút chính, mục đang chọn |
| `--brand-2` / `--brand-2-strong` | `#1f9e73` / `#17805c` | `#3cc595` | chỉ cho Shorts/video (nút Shorts dùng bản đậm để chữ trắng đạt 4.5:1) |
| `--bg` / `--surface` / `--surface-2` | `#fff` / `#fff` / `#f3f5f8` | `#0f1216` / `#151a20` / `#1c222a` | nền trang / header, menu / chip, ảnh thiếu, khung quảng cáo |
| `--text` / `--muted` | `#111418` / `#5b6472` | `#e8ecf1` / `#9aa4b2` | chữ |
| `--border` / `--border-strong` | `#e4e8ee` / `#c9d1dc` | `#262d36` / `#3a4350` | đường kẻ giữa thẻ / viền nút |
| `--sponsor-bg` / `--sponsor-text` | `#fff4d6` / `#7a4b00` | `#3a2c0b` / `#ffd479` | nhãn tài trợ |
| `--logo-blue` / `--logo-green` / `--logo-word` | `#1c6fc5` / `#1f9e73` / `#1c6fc5` | `#3d8de0` / `#2fb383` / `#f1f4f8` | logo SVG (chữ TINHTE sáng lên ở dark mode) |

**Chữ:** Be Vietnam Pro, tự host qua `next/font` (tải lúc build, phục vụ từ `/_next/static` → vẫn đúng CSP `font-src 'self'`). Chỉ 3 độ đậm: 400 nội dung, 600 giao diện, 700 tiêu đề.

| Token | Cỡ | Dùng cho |
|---|---|---|
| `--fs-2xs` | 12px | nhãn, badge, thời lượng video |
| `--fs-xs` | 13px | meta (thời gian) |
| `--fs-sm` | 14px | menu, nút, sapo thẻ, tiêu đề video, footer |
| `--fs-base` | 16px | nội dung, tiêu đề thẻ nhỏ |
| `--fs-md` | 18px | tiêu đề thẻ danh sách (≥768px), tin phụ lớn |
| `--fs-lg` | 22px | tiêu đề vùng, bài chính (mobile) |
| `--fs-xl` / `--fs-2xl` | 28 / 36px | bài chính tablet / desktop, tiêu đề trang danh sách |

Tiêu đề kẹp 2–3 dòng, line-height 1.2–1.4 (đủ chỗ cho dấu tiếng Việt chồng tầng).
**Khoảng cách:** chỉ dùng các bậc `--sp-1…8` = 4, 8, 12, 16, 24, 32, 48, 64px. Giữa các vùng 48px.
**Thẻ bài không có hộp/bóng:** phân cách bằng đường kẻ mảnh `--border`; tiêu đề vùng có vạch đậm 2px phía trên. Chỉ khung quảng cáo có nền `--surface-2` → người đọc không nhầm quảng cáo với tin.
Bo góc ảnh 6px (video 10px); ảnh luôn có tỉ lệ cố định (16:9, 4:3, 9:16) để không nhảy bố cục.

## Bước 5 — Thành phần

| Thành phần | Nội dung | Ghi chú |
|---|---|---|
| `SiteHeader` | Logo, nav chuyên mục, Shorts, Tìm kiếm | Link "bỏ qua tới nội dung". |
| `Logo` / `LogoMark` | Logo SVG nội tuyến, màu theo token | Bản vector dựng lại từ `assets/logo.png` (PNG vẫn dùng cho JSON-LD). Thay bằng SVG gốc khi có bộ nhận diện (WBS-016). |
| `SiteNav` | Chuyên mục, `aria-current`, menu **Thêm** (`<details>`) | Render đủ ở server; JS chỉ đóng menu khi bấm ra ngoài / Esc / chuyển trang. |
| `RailControls` | Nút ‹ › cho dải video | Chỉ hiện khi đã có JS và dải tràn; tôn trọng `prefers-reduced-motion`. |
| `LeadStory` | Ảnh 16:9, tiêu đề H2, sapo, thời gian, nhãn tài trợ | Ảnh `fetchpriority=high` (LCP). |
| `StoryCard` (`compact` / `row`) | Ảnh nhỏ, tiêu đề, thời gian, (sapo ở `row`) | Ảnh `loading=lazy`. |
| `VideoCard` | Poster 9:16, badge "▶ m:ss" góc dưới trái, tiêu đề | Không autoplay trên trang chủ (FR-VID-011/012). |
| `SponsorBadge` | Chữ `sponsorLabel` do backend trả | Không có props để ẩn. |
| `AdSlot` | Một khung: nhãn (`label`, mặc định "Quảng cáo") + một banner, `rel="sponsored noopener"` | `width/height` + `aspect-ratio` của banner từ SSR. Hiện ở các breakpoint của banner (`ad-slot--no-*` ẩn phần còn lại, ở đó không tải creative). `eager` cho ①. |
| `AdZone` | Một vùng (①–⑥, cuối bài): các khung do `resolveAdZone` chọn | Mỗi breakpoint tối đa một khung; rỗng → không render gì. |
| `RelativeTime` | "5 phút trước" / "hôm qua" / ngày | Luôn kèm `<time datetime>`. |
| `Thumb` (không ảnh) | Nền `--surface-2` + biểu tượng logo mờ | Dùng khi `thumbnailUrl`/`posterUrl` null. |

## Bước 6 — Trạng thái

| Tình huống | Hiển thị |
|---|---|
| Đủ dữ liệu | Như bố cục trên. |
| Một vùng rỗng | Ẩn cả vùng đó (kể cả tiêu đề). Riêng Tin mới rỗng: thay bằng nút "Xem tất cả tin". |
| Tất cả rỗng | Khối trống: "Chưa có bài viết nào được xuất bản." |
| `/home` lỗi (staging/prod) | Khối lỗi kèm nút "Tải lại"; header/footer vẫn hiện. |
| `/home` lỗi (môi trường `local`) | Dữ liệu mẫu + dải cảnh báo "Dữ liệu mẫu" để dựng giao diện khi API chưa có. Không bao giờ dùng ở staging/production. |
| `/categories` lỗi/rỗng | Ẩn nav chuyên mục và cột Chuyên mục ở footer, giữ Shorts + Tìm kiếm. |
| Không có banner nào (`/ads/placements` lỗi, cờ `ads.display_slots` OFF, chưa tạo placement) | Không có slot, không khung trống — trang y hệt bản không quảng cáo (đợt 1). |
| Có banner ở một số vùng | Chỉ các vùng đó hiện; quy tắc vùng nào được hiện ở Bước 3 "Vùng quảng cáo". |
| Slot có URL creative/target không an toàn, kích thước ≤ 0 | Bỏ slot đó (fail-closed). |
| `?demo-ads` (chỉ `local`) | Banner mẫu + dải cảnh báo "Quảng cáo mẫu". Ở staging/production tham số bị bỏ qua. |
| Ảnh lỗi/null | Thumb nền trung tính + biểu tượng logo mờ, bố cục giữ nguyên. |
| Item `SPONSORED` thiếu nhãn / `contentKind` lạ | Bỏ item (fail-closed). |

## Bước 7 — Dữ liệu, cache, SEO, truy cập

- **Gọi API phía server**: `{TT_API_ORIGIN}/api/v1/home` kèm `X-Forwarded-Host` = host của site (suy ra từ Site Registry). `revalidate: 60s` — biên tập đổi tin nổi bật thì tối đa 1 phút sau trang chủ cập nhật; gỡ bài phải purge cache theo SLA (FR-DISC-004, việc của WBS-112).
- **SEO**: `<title>` = nhãn site; canonical = origin của site + `/`; OpenGraph; JSON-LD `WebSite` + `Organization`. Môi trường không index (staging/local) đã có `X-Robots-Tag` từ proxy.
- **Truy cập (WCAG 2.1 AA)**: một `<h1>` (ẩn trực quan, tên site), mỗi vùng là `<section aria-labelledby>`, thẻ bài là `<article>` với link bao tiêu đề; ảnh có `alt` (ảnh minh họa dùng `alt=""` vì tiêu đề đã nằm cạnh); dải video cuộn ngang dùng được bằng bàn phím; focus ring rõ; tương phản ≥ 4.5:1; tôn trọng `prefers-reduced-motion`.

## Bước 8 — Điểm còn mở (cần PO/TL/UX quyết)

1. **Block cấu hình qua CMS (WBS-027).** Đã có phần ghim **Tin nổi bật** theo site: `PUT /admin/home/{siteKey}/featured-articles`, migration 0322; thiếu thì bù bằng bài mới nhất. Bố cục vẫn là 3 vùng cố định. Nếu PO cần thêm/bớt/sắp xếp cả block thì phải sửa spec v0.3 trước. Màn hình CMS để ghim bài chưa làm.
2. **Đường dẫn công khai** bài/video/chuyên mục chưa chốt (AIR-026 OPEN). Code gom ở `src/lib/routes.ts`: `/articles/{slug}`, `/shorts/{videoId}`, `/categories/{slug}`, `/search`.
3. **Mã placement quảng cáo — chờ WBS-131 (PO + Commercial + Legal) chốt.** Mười mã đề xuất (`home_top`, `home_top_mobile`, `home_hero_side`, `home_mid`, `home_mid_mobile`, `home_feed`, `home_feed_mobile`, `home_sidebar`, `home_bottom`, `home_bottom_mobile`), kích thước, vị trí "sau bài thứ 6", quy tắc ②/③/④ (Bước 3) và việc chuyển tin nổi bật thứ 5 xuống Tin mới khi có ② đều là đề xuất. Đổi mã: chỉ sửa `HOME_AD_CODES` (`frontend/apps/web/src/lib/home-model.ts`). Lưu ý: `home_feed` trước đây dùng cho cả mobile, nay chỉ ≥ 768px; mobile là `home_feed_mobile`.
   **Phạm vi:** WBS-133 (P0-ADS) chỉ cam kết "nhãn tài trợ + 1–2 slot tĩnh desktop/mobile". FE dựng sẵn 6 vùng nhưng vùng nào hiện là do placement nào được tạo và duyệt. Mở quá 2 vùng cần PO xác nhận phạm vi (phần vượt có thể thuộc WBS-085, P1). Đợt 2 không cộng effort.
4. **Số lượng mỗi vùng** (5 / 8 / 12) là mặc định FE; nên để backend quyết bằng cấu hình biên tập.
5. **Tên chuyên mục trên thẻ bài**: `ArticleSummary` chưa có `categorySlug/categoryName`. Muốn hiện chuyên mục trên thẻ thì phải thêm vào hợp đồng.
6. **Backend:** đã có `GET /home` và `GET /categories`. `featuredVideos` trả rỗng tới khi có feed video công khai (WBS-041/046).
7. **Analytics impression** (FR-ANL-001/002) chưa gắn — kể cả impression/click của banner.
8. **Hợp đồng/DB quảng cáo — đợt 3 đã soạn bản nháp, chờ duyệt** (OpenAPI v0.3 changelog §16, migration `0350_ad_site_bookings`, `db/README.md`):
   - **Đã soạn (DRAFT, TL/PO/BA duyệt):** vị trí có `site_key` + unique `(site_key, code)`, `GET /ads/placements` lọc theo site của Host; tách **vị trí** (mã, site, kích thước cho phép theo breakpoint) khỏi **đặt chỗ** (banner theo breakpoint, link, lịch, ưu tiên, duyệt); `PublicAdSlot.creatives[]` nhiều kích thước. Backend đã có `GET /api/v1/ads/placements` (module Ads, test `[DbFact]`). FE đã chuyển sang hợp đồng mới.
   - **Cần Commercial/PO quyết:** quy tắc chọn banner khi nhiều đặt chỗ trùng giờ (đang: ưu tiên cao nhất → bắt đầu muộn nhất → id; **không xoay vòng**, xoay vòng/tần suất thuộc P1 `ads.campaign_advanced`); TABLET mượn banner DESKTOP khi thiếu (đang bật).
   - **Chưa làm — API quản trị + CMS:** spec đã có `/admin/ad-placements*`, `/admin/ad-bookings/{bookingId}`, `/admin/ad-placements/{placementId}/bookings`; backend và màn CMS **chưa làm** vì cần quyền mới. Đề xuất (chờ BA/PO duyệt ma trận RBAC DRAFT): `ads.manage` (tạo/sửa vị trí + đặt chỗ, không MFA) và `ads.approve` (duyệt/mở vị trí và đặt chỗ, MFA), vai trò mới `AD_OPS` hoặc gán cho vai trò có sẵn. Khi duyệt phải sửa cùng lúc `config/rbac/rbac.json`, hằng số `Permissions`/`StaffRoles` và CHECK `ck_user_roles_role` (migration mới) — ADR-0004.
   - **URL công khai của banner:** backend tạm dựng `{origin site media trong Registry}/{storage_key}` (không viết cứng host). Đường dẫn công khai/CDN của media chưa chốt (WBS media) — đổi ở `AdCreativeUrls` khi có.
   - **Cột banner cũ** trên `tt.ad_placements` (media, alt, link, kích thước, lịch) vẫn còn để down an toàn; API mới không đọc. Gỡ bằng migration riêng khi TL duyệt.
   - ④ "dạng tin" (native): `PublicAdCreative` chỉ có ảnh. Bài tài trợ dạng tin hiện đi qua `contentKind: SPONSORED` trong `recentArticles`; muốn native ad riêng thì phải thêm kiểu creative vào hợp đồng.
   - **CSP**: `img-src` chỉ cho `'self' data:` + origin media (đang Report-Only). Creative phải nằm trên origin media của Tinhte, hoặc thêm origin ad server vào CSP khi chuyển sang enforce (WBS-071).
   - Mật độ/khoảng cách quảng cáo hiện do FE tự giữ (`homeAdPlan`); nếu Commercial cần đổi vị trí mà không deploy thì phải có cấu hình bố cục từ backend (liên quan điểm 1).

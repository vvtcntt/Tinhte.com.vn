---
id: ADR-0006
title: Tải media lên qua upload intent + adapter kho lưu trữ; bản dev lưu cục bộ và giả lập xử lý
status: PROPOSED (Sprint 0) — chờ Tech Lead duyệt; vendor lưu trữ/transcode chờ WBS-035
date: 2026-11-09
related: ADR-0001 (D5, media site), ADR-0003, ADR-0005, WBS-035…040, WBS-046, FR-VID-005/006/007/008, FR-CMS-009
---

# ADR-0006 — Upload intent + adapter kho lưu trữ media

## Bối cảnh

- WBS-046 (màn Video CMS + thư viện media) cần tải tệp lên, theo dõi trạng thái xử lý và gắn media vào video.
- Hợp đồng v0.3 đã có `createUploadIntent` → `finalizeUpload` → `getAdminMedia`, kiểu URL ký sẵn (pre-signed) của object storage.
- Chưa chọn vendor lưu trữ/transcode/CDN (WBS-035). Pipeline thật (WBS-036…039) chưa có. Quyền media/giấy phép chưa có quy trình (WBS-023).

## Quyết định

### 1. Luồng tải lên (hợp đồng v0.3)

1. `POST /admin/media/upload-intents` (`media.upload`, Idempotency-Key): kiểm loại tệp (MIME DRAFT: MP4/MOV/WebM, JPEG/PNG/WebP, WebVTT) và kích thước (`Media:MaxVideoBytes` 2 GiB, ảnh 20 MiB, phụ đề 1 MiB — DRAFT). Tạo `tt.media_assets` trạng thái `UPLOADING`, trả `uploadUrl` ngắn hạn (15 phút) + `uploadToken` một lần. **DB chỉ giữ sha256 của token.**
2. Client `PUT` tệp lên `uploadUrl`.
3. `POST …/{id}/finalize`: HEAD object, so **kích thước + sha256** với intent. Thiếu tệp → 422 `MEDIA_NOT_READY`; khác → xóa object, 422 `VALIDATION_FAILED` (client tải lại lên cùng URL). Đúng → `UPLOADED`, xóa token. Gọi lại sau khi đã finalize trả trạng thái hiện tại (202).
4. Pipeline → `VALIDATING` → `PROCESSING` (video) → `READY`, hoặc `REJECTED`/`FAILED`. CMS hỏi `GET /admin/media/{id}` theo chu kỳ (1 s giãn tới 10 s).
- Object key do server sinh (`media/yyyy/MM/{mediaId}`), không chứa tên tệp người dùng. Tên tệp chỉ để hiển thị (đã bỏ đường dẫn/ký tự điều khiển).
- **Tải lên không cấp quyền:** media mới luôn `PRIVATE` + `UNVERIFIED`; PUBLIC cần CLEARED (CHECK 0305). Gắn vào video bị chặn khi media hỏng/bị từ chối hoặc quyền `RESTRICTED`/`EXPIRED`/`REVOKED`; chặn publish khi chưa CLEARED là việc của WBS-040.

### 2. Adapter `IMediaStorage` (`Media:StorageProvider`)

| Provider | Môi trường | Hành vi |
|---|---|---|
| `Local` | **chỉ Development** (cấu hình ở môi trường khác → API không khởi động) | Thư mục `Media:LocalRoot` (mặc định `%TEMP%/tinhte-media`). `uploadUrl` = `PUT /api/v1/admin/media/{id}/dev-upload?expires=&signature=` trên chính site cms, ký HMAC (media + kích thước + hạn, khóa ngẫu nhiên theo tiến trình). Ghi ra tệp tạm rồi đổi tên; vượt kích thước khai báo → 413 |
| `None` | mặc định ngoài Development | upload intent/finalize trả 503 `DEPENDENCY_UNAVAILABLE` (fail-closed). Đọc trạng thái/thư viện vẫn chạy |
| (S3/GCS/vendor) | sau WBS-035 | URL ký sẵn trỏ thẳng tới kho, giới hạn key + `Content-Length` + checksum; finalize dùng HEAD/checksum của kho |

- Endpoint `dev-upload` **nằm ngoài hợp đồng**, chỉ trả nội dung khi provider là Local (ngược lại 404). Cùng origin cms nên trình duyệt gửi cookie phiên → vẫn cần `media.upload` + `X-CSRF-Token`. CMS chỉ gửi CSRF/cookie khi `uploadUrl` cùng origin; URL của kho ngoài không nhận gì thêm.

### 3. Xử lý giả lập (`SimulatedMediaProcessor`, dev-only)

- Không chạy nền, không gọi dịch vụ ngoài: mỗi lần CMS đọc trạng thái media (chi tiết hoặc thư viện), media đứng yên ≥ `Media:SimulatedStepDelay` (mặc định 2 s; test đặt 0) tiến **một** bước.
- Bước kiểm tra đọc 16 byte đầu tệp, so chữ ký định dạng (MP4/MOV `ftyp`, WebM EBML, JPEG, PNG, WebP, `WEBVTT`): sai → `REJECTED` + `errorCode = UNSUPPORTED_FORMAT`; mất tệp → `FAILED`/`OBJECT_MISSING`.
- **`READY` ở dev KHÔNG có nghĩa phát được:** không transcode, không poster/rendition, không quét mã độc. Production dùng `NoMediaProcessor` (media đứng ở `UPLOADED` cho tới khi có worker WBS-038).

### 4. Video trong CMS (migration 0331)

- Cùng mô hình revision với bài viết (ADR-0005): `tt.video_revisions.status`, trigger đóng băng nội dung revision đã rời DRAFT hoặc đang public, không xóa revision. PATCH sửa tại chỗ revision DRAFT, ngược lại tạo revision mới.
- `tt.videos.media_id` = media đang gắn; một media chỉ gắn một video. Đổi media của video từng public → 409 (media revision thuộc WBS-040). Slug video sinh lúc tạo (tiêu đề + 8 hex của id) vì hợp đồng không có slug video.

## Còn thiếu

| Việc | WBS |
|---|---|
| Chọn vendor lưu trữ/transcode/CDN, adapter thật, URL ký sẵn | 035, 036, 039 |
| Quét mã độc/quarantine, profile rendition, poster, transcode thật, worker hàng đợi + retry/poison | 036…038 |
| Gửi duyệt/duyệt/publish video, chặn publish khi media chưa READY hoặc quyền chưa CLEARED, media revision | 040 |
| Xem trước/phát trong CMS (playback grant qua site `media`) | 039, 045 |
| Rate limit (429) cho upload intent; hạn mức theo người dùng | 036 |
| Dọn object mồ côi (intent hết hạn chưa finalize) | 038 |

## Hệ quả

- **Được:** CMS và hợp đồng chạy trọn luồng trên máy dev mà không cần dịch vụ ngoài; đổi vendor chỉ thêm một adapter, không đổi API/CMS.
- **Mất:** dev-upload đi qua API (tệp lớn tốn băng thông API, chỉ chấp nhận ở dev); token idempotency của upload intent lưu response (có `uploadToken`) 24 giờ trong `tt.idempotency_keys` — token chỉ dùng được trong 15 phút và một lần. Bộ giả lập ghi DB khi đọc (chỉ ở dev).

## Phương án đã loại

- **Multipart POST tệp thẳng vào API ở mọi môi trường:** API phải gánh băng thông/bộ nhớ video, trái với hợp đồng (upload intent + object storage).
- **BackgroundService giả lập pipeline:** chạy nền đụng DB của test và PGlite một kết nối; giả lập theo lượt đọc đủ để CMS thấy chuyển trạng thái.
- **Coi tải lên là đã có quyền (CLEARED/PUBLIC):** trái FR-CMS-009 và quy tắc dự án (không coi giấy phép là đã có).

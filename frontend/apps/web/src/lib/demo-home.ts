import type { ArticleSummary, Category, HomePage, VideoSummary } from './contracts';

// Dữ liệu mẫu CHỈ để dựng giao diện ở môi trường `local` khi API /home chưa có (xem home.ts).
// Không bao giờ dùng ở staging/production. Không có ảnh: thẻ hiện nền thay thế.

const minutesAgo = (now: number, m: number) => new Date(now - m * 60_000).toISOString();

export function demoHome(now: number = Date.now()): HomePage {
  const article = (n: number, title: string, summary: string, m: number, extra: Partial<ArticleSummary> = {}): ArticleSummary => ({
    id: `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
    slug: `bai-mau-${n}`,
    title,
    summary,
    publishedAt: minutesAgo(now, m),
    thumbnailUrl: null,
    contentKind: 'EDITORIAL',
    sponsorLabel: null,
    ...extra,
  });
  const video = (n: number, title: string, sec: number, m: number): VideoSummary => ({
    id: `00000000-0000-4000-9000-${String(n).padStart(12, '0')}`,
    slug: `video-mau-${n}`,
    title,
    publishedAt: minutesAgo(now, m),
    posterUrl: null,
    durationMs: sec * 1000,
    contentKind: 'EDITORIAL',
    sponsorLabel: null,
  });

  return {
    featuredArticles: [
      article(1, 'Trải nghiệm nhanh mẫu điện thoại gập mới: bản lề chắc hơn, nếp gấp gần như biến mất',
        'Sau một tuần dùng làm máy chính, đây là những điểm cải thiện rõ nhất và vài thứ vẫn cần hoàn thiện.', 35),
      article(2, 'So sánh pin 5 laptop mỏng nhẹ phổ biến nhất cuối năm', 'Bài đo xem video liên tục ở độ sáng 200 nit.', 120),
      article(3, 'Hướng dẫn bật xác thực hai lớp cho mọi tài khoản quan trọng', 'Mất 10 phút, tránh được phần lớn rủi ro mất tài khoản.', 180),
      article(4, 'Sạc nhanh 100W có làm chai pin điện thoại nhanh hơn?', 'Chúng tôi đo nhiệt độ và dung lượng sau 300 chu kỳ sạc.', 260, {
        contentKind: 'SPONSORED',
        sponsorLabel: 'Nội dung tài trợ',
      }),
      article(5, 'Wi-Fi 7 đã đáng nâng cấp cho gia đình chưa?', 'Tốc độ thực tế khác xa con số trên hộp.', 400),
    ],
    recentArticles: [
      article(11, 'Cập nhật hệ điều hành tháng 11 sửa lỗi hao pin khi chờ', 'Bản vá đã phát hành cho các dòng máy từ 2023 trở lên.', 8),
      article(12, 'Tai nghe chống ồn giá tầm trung: 3 lựa chọn đáng tiền', 'Chất âm, độ ồn và thời lượng pin được đo trong cùng điều kiện.', 22),
      article(13, 'Mẹo chụp ảnh đêm bằng điện thoại không cần chân máy', 'Giữ máy đúng cách và chọn thời gian phơi sáng hợp lý.', 47),
      article(14, 'Ổ SSD gắn ngoài tốc độ cao: cổng kết nối quan trọng hơn bạn nghĩ', 'Cùng một ổ, tốc độ chênh gấp ba lần tùy cổng.', 75),
      article(15, 'Đồng hồ thông minh đo huyết áp: chính xác đến đâu?', 'Đối chiếu với máy đo y tế trong hai tuần.', 130),
      article(16, 'Cách dọn dung lượng iCloud và Google Drive hiệu quả', 'Tìm và xóa tệp lớn, ảnh trùng, bản sao lưu cũ.', 200),
      article(17, 'Màn hình 4K 27 inch cho dân văn phòng: có cần 144Hz?', 'Khi nào tần số quét cao thực sự có ích.', 320),
      article(18, 'Robot hút bụi tự giặt giẻ: điểm cộng và điểm trừ sau 1 tháng', 'Ồn, tốn nước hay thực sự tiện?', 600),
      article(19, 'Bảo mật router gia đình: 6 việc nên làm ngay', 'Đổi mật khẩu quản trị mới chỉ là bước đầu.', 1500),
      article(20, 'Xe điện cỡ nhỏ: chi phí sạc thực tế mỗi tháng', 'Tính theo giá điện bậc thang và quãng đường đi làm.', 3000),
    ],
    featuredVideos: [
      video(1, 'Mở hộp nhanh trong 60 giây', 62, 50),
      video(2, '3 cài đặt camera nên bật ngay', 45, 140),
      video(3, 'Thử độ bền bản lề máy gập', 88, 300),
      video(4, 'Sạc 100W: nóng đến đâu?', 54, 500),
      video(5, 'Laptop mỏng nhất năm nay', 71, 900),
      video(6, 'Mẹo pin cho điện thoại cũ', 39, 1400),
      video(7, 'Bàn phím cơ cho người mới', 95, 2000),
    ],
  };
}

export const demoCategories: Category[] = [
  { id: 'c1', slug: 'dien-thoai', name: 'Điện thoại' },
  { id: 'c2', slug: 'may-tinh', name: 'Máy tính' },
  { id: 'c3', slug: 'am-thanh', name: 'Âm thanh' },
  { id: 'c4', slug: 'nha-thong-minh', name: 'Nhà thông minh' },
  { id: 'c5', slug: 'xe', name: 'Xe' },
  { id: 'c6', slug: 'thu-thuat', name: 'Thủ thuật' },
];

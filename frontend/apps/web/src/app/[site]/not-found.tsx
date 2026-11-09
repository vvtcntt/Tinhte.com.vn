import Link from 'next/link';
import { routes } from '../../lib/routes';

// 404 trong khung site (header/footer vẫn hiện). Bài đã gỡ (API 410) cũng về đây: không lộ nội dung đã gỡ (FR-DISC-004).
export default function SiteNotFound() {
  return (
    <div className="container">
      <div className="state-block">
        <h1>Không tìm thấy trang</h1>
        <p>Bài viết có thể đã bị gỡ, đổi địa chỉ hoặc chưa từng tồn tại.</p>
        <Link className="button" href={routes.home()}>
          Về trang chủ
        </Link>
      </div>
    </div>
  );
}

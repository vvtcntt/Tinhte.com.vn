import Link from 'next/link';
import type { SiteDefinition } from '@tinhte/sites';
import logo from '../assets/logo.png';
import { routes } from '../lib/routes';

// Trang pháp lý/hỗ trợ: UX-PUB-09 (WBS-075). Thông tin giấy phép chỉ hiện khi Legal cấp — không tự điền (CLAUDE.md §3).
export function SiteFooter({ site }: { site: SiteDefinition }) {
  const year = new Date().getFullYear();
  return (
    <footer className="site-footer">
      <div className="container site-footer__inner">
        <img src={logo.src} width={110} height={40} alt={site.label} className="site-footer__logo" />
        <nav aria-label="Thông tin">
          <ul className="site-footer__links">
            <li><Link href={routes.page('gioi-thieu')}>Giới thiệu</Link></li>
            <li><Link href={routes.page('dieu-khoan')}>Điều khoản sử dụng</Link></li>
            <li><Link href={routes.page('quyen-rieng-tu')}>Chính sách quyền riêng tư</Link></li>
            <li><Link href={routes.page('lien-he')}>Liên hệ</Link></li>
          </ul>
        </nav>
        <p className="site-footer__copy">
          © {year} {site.label}
        </p>
      </div>
    </footer>
  );
}

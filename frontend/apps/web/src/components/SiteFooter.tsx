import Link from 'next/link';
import type { SiteDefinition } from '@tinhte/sites';
import type { Category } from '../lib/contracts';
import { routes } from '../lib/routes';
import { Logo } from './Logo';

// Trang pháp lý/hỗ trợ: UX-PUB-09 (WBS-075). Thông tin giấy phép chỉ hiện khi Legal cấp — không tự điền (CLAUDE.md §3).
export function SiteFooter({ site, categories }: { site: SiteDefinition; categories: Category[] }) {
  const year = new Date().getFullYear();
  return (
    <footer className="site-footer">
      <div className="container site-footer__grid">
        <div className="site-footer__brand">
          <Link href={routes.home()} aria-label={`${site.label} — Trang chủ`}>
            <Logo height={34} />
          </Link>
          <p>Tin tức, đánh giá và video ngắn về công nghệ.</p>
        </div>

        {categories.length > 0 && (
          <nav className="site-footer__col site-footer__col--categories" aria-labelledby="chan-trang-chuyen-muc">
            <h2 id="chan-trang-chuyen-muc">Chuyên mục</h2>
            <ul>
              {categories.map((c) => (
                <li key={c.id}>
                  <Link href={routes.category(c.slug)}>{c.name}</Link>
                </li>
              ))}
            </ul>
          </nav>
        )}

        <nav className="site-footer__col" aria-labelledby="chan-trang-kham-pha">
          <h2 id="chan-trang-kham-pha">Khám phá</h2>
          <ul>
            <li><Link href={routes.latest()}>Tin mới</Link></li>
            <li><Link href={routes.shorts()}>Shorts</Link></li>
            <li><Link href={routes.search()}>Tìm kiếm</Link></li>
          </ul>
        </nav>

        <nav className="site-footer__col" aria-labelledby="chan-trang-thong-tin">
          <h2 id="chan-trang-thong-tin">Thông tin</h2>
          <ul>
            <li><Link href={routes.page('gioi-thieu')}>Giới thiệu</Link></li>
            <li><Link href={routes.page('dieu-khoan')}>Điều khoản sử dụng</Link></li>
            <li><Link href={routes.page('quyen-rieng-tu')}>Chính sách quyền riêng tư</Link></li>
            <li><Link href={routes.page('lien-he')}>Liên hệ</Link></li>
          </ul>
        </nav>
      </div>
      <div className="container site-footer__bottom">
        <p>
          © {year} {site.label}
        </p>
      </div>
    </footer>
  );
}

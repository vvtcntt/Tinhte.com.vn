import Link from 'next/link';
import type { SiteDefinition } from '@tinhte/sites';
import logo from '../assets/logo.png';
import type { Category } from '../lib/contracts';
import { routes } from '../lib/routes';

export function SiteHeader({ site, categories }: { site: SiteDefinition; categories: Category[] }) {
  return (
    <header className="site-header" data-site={site.key}>
      <a className="skip-link" href="#noi-dung">
        Bỏ qua tới nội dung
      </a>
      <div className="container site-header__bar">
        <Link href={routes.home()} className="site-header__logo" aria-label={`${site.label} — Trang chủ`}>
          {/* Ảnh tĩnh qua /_next/static (không đi qua proxy phân giải site). */}
          <img src={logo.src} width={132} height={48} alt="" />
        </Link>
        {categories.length > 0 && (
          <nav className="site-nav" aria-label="Chuyên mục">
            <ul>
              {categories.map((c) => (
                <li key={c.id}>
                  <Link href={routes.category(c.slug)}>{c.name}</Link>
                </li>
              ))}
            </ul>
          </nav>
        )}
        <div className="site-header__actions">
          <Link href={routes.shorts()} className="pill pill--shorts">
            <PlayIcon /> Shorts
          </Link>
          <Link href={routes.search()} className="icon-button" aria-label="Tìm kiếm">
            <SearchIcon />
          </Link>
        </div>
      </div>
    </header>
  );
}

export function PlayIcon({ size = 14 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path fill="currentColor" d="M8 5.14v13.72a1 1 0 0 0 1.52.85l11-6.86a1 1 0 0 0 0-1.7l-11-6.86A1 1 0 0 0 8 5.14Z" />
    </svg>
  );
}

function SearchIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        d="m20 20-4.2-4.2M18 11a7 7 0 1 1-14 0 7 7 0 0 1 14 0Z"
      />
    </svg>
  );
}

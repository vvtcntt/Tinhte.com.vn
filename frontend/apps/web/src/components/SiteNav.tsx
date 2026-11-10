'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useEffect, useRef } from 'react';
import type { Category } from '../lib/contracts';
import { routes } from '../lib/routes';

/** Desktop (≥1024px) hiện N chuyên mục đầu trên thanh, phần còn lại vào menu "Thêm". Mobile: cuộn ngang toàn bộ. */
export const NAV_INLINE_COUNT = 6;
/** Menu "Thêm" dài hơn mức này thì chia nhiều cột thay vì một cột dài. */
const MENU_SINGLE_COLUMN_MAX = 8;

function isCurrent(pathname: string | null, href: string): boolean {
  return pathname === href || (pathname?.startsWith(`${href}/`) ?? false);
}

/**
 * Thanh chuyên mục. Render đủ trên server (không cần JS để dùng); JS chỉ đóng menu "Thêm"
 * khi bấm ra ngoài, nhấn Esc hoặc chuyển trang.
 */
export function SiteNav({ categories }: { categories: Category[] }) {
  const pathname = usePathname();
  const moreRef = useRef<HTMLDetailsElement>(null);
  const overflow = categories.slice(NAV_INLINE_COUNT);
  const overflowCurrent = overflow.some((c) => isCurrent(pathname, routes.category(c.slug)));

  useEffect(() => {
    if (moreRef.current) moreRef.current.open = false;
  }, [pathname]);

  useEffect(() => {
    const onPointer = (event: PointerEvent) => {
      const more = moreRef.current;
      if (more?.open && !more.contains(event.target as Node)) more.open = false;
    };
    const onKey = (event: KeyboardEvent) => {
      const more = moreRef.current;
      if (event.key !== 'Escape' || !more?.open) return;
      more.open = false;
      more.querySelector('summary')?.focus();
    };
    document.addEventListener('pointerdown', onPointer);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('pointerdown', onPointer);
      document.removeEventListener('keydown', onKey);
    };
  }, []);

  return (
    <nav className="site-nav" aria-label="Chuyên mục">
      <ul className="site-nav__list">
        {categories.map((c, i) => {
          const href = routes.category(c.slug);
          return (
            <li key={c.id} className={i >= NAV_INLINE_COUNT ? 'site-nav__item site-nav__item--overflow' : 'site-nav__item'}>
              <Link href={href} aria-current={isCurrent(pathname, href) ? 'page' : undefined}>
                {c.name}
              </Link>
            </li>
          );
        })}
      </ul>
      {overflow.length > 0 && (
        <details className="site-nav__more" ref={moreRef}>
          <summary className={overflowCurrent ? 'is-current' : undefined}>
            Thêm
            <svg width="12" height="12" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
              <path fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" d="m6 9 6 6 6-6" />
            </svg>
          </summary>
          <ul className={overflow.length > MENU_SINGLE_COLUMN_MAX ? 'site-nav__menu site-nav__menu--columns' : 'site-nav__menu'}>
            {overflow.map((c) => {
              const href = routes.category(c.slug);
              return (
                <li key={c.id}>
                  <Link href={href} aria-current={isCurrent(pathname, href) ? 'page' : undefined}>
                    {c.name}
                  </Link>
                </li>
              );
            })}
          </ul>
        </details>
      )}
    </nav>
  );
}

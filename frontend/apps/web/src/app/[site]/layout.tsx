import type { ReactNode } from 'react';
import { notFound } from 'next/navigation';
import { findSite } from '@tinhte/sites';
import { SiteFooter } from '../../components/SiteFooter';
import { SiteHeader } from '../../components/SiteHeader';
import { loadNavCategories } from '../../lib/home';

// Layout chung cho mọi site public-web. Site cần giao diện riêng: thêm src/sites/<key>/ (ADR-0001 runbook bước 2).
export default async function SiteLayout({ children, params }: { children: ReactNode; params: Promise<{ site: string }> }) {
  const { site: key } = await params;
  const site = findSite(key);
  if (!site || site.app !== 'web') notFound();
  const categories = await loadNavCategories(site);
  return (
    <>
      <SiteHeader site={site} categories={categories} />
      <main id="noi-dung" tabIndex={-1}>
        {children}
      </main>
      <SiteFooter site={site} />
    </>
  );
}

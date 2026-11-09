import type { ReactNode } from 'react';
import { notFound } from 'next/navigation';
import { findSite } from '@tinhte/sites';

// Layout chung cho mọi site public-web. Site cần giao diện riêng: thêm src/sites/<key>/ (ADR-0001 runbook bước 2).
export default async function SiteLayout({ children, params }: { children: ReactNode; params: Promise<{ site: string }> }) {
  const { site: key } = await params;
  const site = findSite(key);
  if (!site || site.app !== 'web') notFound();
  return (
    <>
      <header data-site={site.key}>
        <strong>{site.label}</strong>
      </header>
      <main>{children}</main>
    </>
  );
}

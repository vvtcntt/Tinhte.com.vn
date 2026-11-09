import type { ReactNode } from 'react';
import { notFound } from 'next/navigation';
import { findSite } from '@tinhte/sites';

export const metadata = { robots: { index: false, follow: false } };

// CMS tách app (ADR-0001 D5): chỉ phục vụ site kind "cms"; phiên staff cookie __Host-tt_cms_session.
export default async function CmsLayout({ children, params }: { children: ReactNode; params: Promise<{ site: string }> }) {
  const { site: key } = await params;
  const site = findSite(key);
  if (!site || site.kind !== 'cms') notFound();
  return (
    <>
      <header data-site={site.key}>
        <strong>{site.label}</strong>
      </header>
      <main>{children}</main>
    </>
  );
}

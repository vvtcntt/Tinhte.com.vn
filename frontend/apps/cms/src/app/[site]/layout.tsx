import type { ReactNode } from 'react';
import { notFound } from 'next/navigation';
import { findSite } from '@tinhte/sites';
import { CmsApp } from '../../components/CmsApp';

export const metadata = { title: 'CMS', robots: { index: false, follow: false } };

// CMS tách app (ADR-0001 D5): chỉ phục vụ site kind "cms". Phiên staff (cookie __Host-tt_cms_session, HttpOnly) được kiểm
// phía trình duyệt qua GET /api/v1/admin/auth/session cùng origin — trang CMS luôn noindex, không cần SSR dữ liệu.
export default async function CmsLayout({ children, params }: { children: ReactNode; params: Promise<{ site: string }> }) {
  const { site: key } = await params;
  const site = findSite(key);
  if (!site || site.kind !== 'cms') notFound();
  return (
    <div data-site={site.key}>
      <CmsApp siteLabel={site.label}>{children}</CmsApp>
    </div>
  );
}

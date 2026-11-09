import { findSite, sessionCookieName } from '@tinhte/sites';

// Sprint 0: trang giữ chỗ. Đăng nhập staff qua OIDC (GET /api/v1/auth/staff/oidc/start) làm ở WBS-017.
export default async function CmsHome({ params }: { params: Promise<{ site: string }> }) {
  const { site: key } = await params;
  const site = findSite(key);
  return (
    <section>
      <h1>CMS</h1>
      <p>
        Khung Sprint 0. Phiên staff dùng cookie host-only <code>{site ? sessionCookieName(site) : ''}</code>.
      </p>
    </section>
  );
}

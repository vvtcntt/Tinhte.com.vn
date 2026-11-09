import { findSite, parseSiteEnv } from '@tinhte/sites';

// Sprint 0: trang chủ giữ chỗ. Nội dung editorial theo WBS-027 (GET /home của hợp đồng v0.3).
export default async function HomePage({ params }: { params: Promise<{ site: string }> }) {
  const { site: key } = await params;
  const site = findSite(key);
  return (
    <section>
      <h1>{site?.label}</h1>
      <p>
        Khung Sprint 0 — site <code>{key}</code>, môi trường <code>{parseSiteEnv(process.env.TT_SITE_ENV)}</code>.
      </p>
    </section>
  );
}

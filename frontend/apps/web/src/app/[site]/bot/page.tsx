import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound } from 'next/navigation';
import { findSite, isIndexable, originFor, parseSiteEnv } from '@tinhte/sites';
import { routes } from '../../../lib/routes';

// Trang giới thiệu bot thu thập tin (BA-01 BR-03, ADR-0008 §5): User-Agent của TinhteNewsBot trỏ về đây —
// "TinhteNewsBot/<phiên bản> (+<origin site>/bot)". URL suy từ Site Registry (Aggregation:BotInfoPath = /bot), không viết cứng host.
// Nội dung chỉ mô tả hành vi đã hiện thực; chính sách sử dụng nội dung do Legal duyệt (BA-01 mục 5) — không cam kết thay Legal.
export const dynamic = 'force-dynamic';

type Props = { params: Promise<{ site: string }> };

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { site: key } = await params;
  const site = findSite(key);
  if (!site) return {};
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  return {
    title: `TinhteNewsBot — bot thu thập tin của ${site.label}`,
    description: `TinhteNewsBot đọc RSS/Atom công khai để ban biên tập ${site.label} theo dõi tin mới. Cách nhận diện, tần suất và cách chặn bot.`,
    alternates: { canonical: `${originFor(site, env)}${routes.bot()}` },
    robots: isIndexable(site, env) ? undefined : { index: false, follow: false },
  };
}

export default async function BotPage({ params }: Props) {
  const { site: key } = await params;
  const site = findSite(key);
  if (!site) notFound();
  const env = parseSiteEnv(process.env.TT_SITE_ENV);
  const userAgent = `TinhteNewsBot/1.0 (+${originFor(site, env)}${routes.bot()})`;
  return (
    <div className="container">
      <article className="article bot-page">
        <h1 className="article__title">TinhteNewsBot</h1>
        <p className="article__lead">
          TinhteNewsBot là chương trình tự động của {site.label}. Bot đọc các nguồn tin RSS/Atom/JSON Feed công khai để ban biên tập
          biết tin mới trong ngày. Bot không tự đăng bài: biên tập viên đọc bài gốc và tự viết.
        </p>

        <div className="article-body">
          <h2>Nhận diện bot</h2>
          <p>Mọi yêu cầu của bot gửi User-Agent dạng:</p>
          <pre>
            <code>{userAgent}</code>
          </pre>
          <p>
            Số phiên bản có thể thay đổi. Bot không giả trình duyệt, không đăng nhập, không vượt tường phí (paywall) hay captcha.
          </p>

          <h2>Bot đọc gì</h2>
          <ul>
            <li>Chỉ các địa chỉ feed đã được ban biên tập khai báo và duyệt cho từng nguồn — không dò quét cả website.</li>
            <li>Từ feed, bot lưu tiêu đề, đường dẫn bài gốc, đoạn tóm tắt có sẵn trong feed, tác giả, thời điểm đăng và chuyên mục của feed.</li>
            <li>Bot không tải toàn văn bài viết. Danh sách tin chỉ hiển thị nội bộ cho ban biên tập, kèm đường dẫn về bài gốc.</li>
          </ul>

          <h2>Tần suất và tải</h2>
          <ul>
            <li>Mỗi feed được đọc tối đa một lần mỗi 30 phút theo mặc định (không dưới 5 phút), và thưa hơn nếu feed khai báo <code>ttl</code> hoặc <code>sy:updatePeriod</code>.</li>
            <li>Bot gửi yêu cầu có điều kiện (<code>If-None-Match</code>, <code>If-Modified-Since</code>): feed không đổi thì máy chủ chỉ cần trả <code>304</code>.</li>
            <li>Khi máy chủ trả <code>429</code> hoặc <code>503</code>, bot tôn trọng <code>Retry-After</code> và giãn lịch tới tối đa 6 giờ. Mỗi lần đọc giới hạn 20 giây và 5 MB.</li>
          </ul>

          <h2>Chặn hoặc giới hạn bot</h2>
          <p>
            Bot tuân theo <code>robots.txt</code> (RFC 9309) với tên <code>TinhteNewsBot</code>, đọc lại tệp này tối đa 24 giờ một lần. Ví dụ chặn toàn bộ:
          </p>
          <pre>
            <code>{'User-agent: TinhteNewsBot\nDisallow: /'}</code>
          </pre>
          <p>
            Khi robots.txt chặn đường dẫn feed, bot ngừng đọc feed đó và báo cho ban biên tập. Nếu bạn là chủ website và muốn trao đổi về việc
            bot đọc feed của bạn, vui lòng <Link href={routes.page('lien-he')}>liên hệ {site.label}</Link>.
          </p>
        </div>
      </article>
    </div>
  );
}

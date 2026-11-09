import Link from 'next/link';
import type { VideoSummary } from '../../lib/contracts';
import { duration } from '../../lib/format';
import { routes } from '../../lib/routes';
import { PlayIcon } from '../SiteHeader';
import { SponsorBadge, Thumb } from './parts';

/** Dải Video nổi bật: thẻ 9:16 cuộn ngang. Không autoplay ở trang chủ (FR-VID-011/012). */
export function VideoRail({ videos }: { videos: VideoSummary[] }) {
  return (
    <ul className="video-rail" aria-label="Danh sách video nổi bật">
      {videos.map((v) => {
        const len = duration(v.durationMs);
        return (
          <li key={v.id}>
            <article className="video-card">
              <Link href={routes.video(v.id)} className="card-link">
                <div className="video-card__media">
                  <Thumb src={v.posterUrl} ratio="9/16" sizes="180px" />
                  <span className="video-card__play" aria-hidden="true">
                    <PlayIcon size={18} />
                  </span>
                  {len && (
                    <span className="video-card__duration">
                      <span className="visually-hidden">Thời lượng </span>
                      {len}
                    </span>
                  )}
                </div>
                <SponsorBadge kind={v.contentKind} label={v.sponsorLabel} />
                <h3 className="video-card__title">{v.title}</h3>
              </Link>
            </article>
          </li>
        );
      })}
    </ul>
  );
}

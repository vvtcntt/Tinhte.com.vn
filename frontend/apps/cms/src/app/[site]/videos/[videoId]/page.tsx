import '../../../videos.css';
import { VideoDetailPage } from '../../../../components/Videos';

export default async function Page({ params }: { params: Promise<{ videoId: string }> }) {
  const { videoId } = await params;
  return <VideoDetailPage videoId={videoId} />;
}

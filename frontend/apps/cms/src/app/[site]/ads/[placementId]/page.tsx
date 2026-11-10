import '../../../videos.css';
import '../../../ads.css';
import { AdPlacementDetailPage } from '../../../../components/Ads';

export default async function Page({ params }: { params: Promise<{ placementId: string }> }) {
  const { placementId } = await params;
  return <AdPlacementDetailPage placementId={placementId} />;
}

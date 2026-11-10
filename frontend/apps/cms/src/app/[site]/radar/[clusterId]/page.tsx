import '../../../aggregation.css';
import { ClusterDetailPage } from '../../../../components/Radar';

export default async function Page({ params }: { params: Promise<{ clusterId: string }> }) {
  const { clusterId } = await params;
  return <ClusterDetailPage clusterId={clusterId} />;
}

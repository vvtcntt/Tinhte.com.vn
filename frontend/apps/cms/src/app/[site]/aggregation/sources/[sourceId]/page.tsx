import '../../../../aggregation.css';
import { SourceDetailPage } from '../../../../../components/AggregationSources';

export default async function Page({ params }: { params: Promise<{ sourceId: string }> }) {
  const { sourceId } = await params;
  return <SourceDetailPage sourceId={sourceId} />;
}

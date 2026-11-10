import '../../../../../videos.css';
import '../../../../../ads.css';
import { AdBookingPage } from '../../../../../../components/Ads';

// bookingId = "new" → tạo đặt chỗ mới cho vị trí.
export default async function Page({ params }: { params: Promise<{ placementId: string; bookingId: string }> }) {
  const { placementId, bookingId } = await params;
  return <AdBookingPage placementId={placementId} bookingId={bookingId} />;
}

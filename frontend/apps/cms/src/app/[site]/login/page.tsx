import { LoginRedirect } from '../../../components/Pages';

// /login: khi chưa đăng nhập, CmsApp hiển thị màn đăng nhập (kèm lỗi ?error=&reason= nếu có).
// Khi đã có phiên, chuyển về returnTo.
export default function LoginPage() {
  return <LoginRedirect />;
}

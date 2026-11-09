// Lỗi đăng nhập mà API chuyển về /login?error={code}&reason={reason}[&returnTo=] (x-browser-error-redirect, ADR-0004 A-4).
import { sanitizeReturnTo } from './api.ts';

export interface LoginError {
  title: string;
  message: string;
  /** Có nên mời đăng nhập lại ngay không (vô ích khi tài khoản chưa có vai trò hoặc bị khóa). */
  retry: boolean;
}

const BY_REASON: Record<string, LoginError> = {
  state_invalid: {
    title: 'Phiên đăng nhập đã hết hạn',
    message: 'Không xác minh được phản hồi đăng nhập (có thể do mở lại liên kết cũ hoặc chờ quá lâu). Hãy đăng nhập lại.',
    retry: true,
  },
  idp_error: {
    title: 'Dịch vụ đăng nhập báo lỗi',
    message: 'Nhà cung cấp đăng nhập trả về lỗi. Thử lại sau ít phút; nếu vẫn lỗi, báo cho đội kỹ thuật.',
    retry: true,
  },
  denied: {
    title: 'Đăng nhập chưa hoàn tất',
    message: 'Bạn đã hủy đăng nhập hoặc nhà cung cấp đăng nhập từ chối yêu cầu.',
    retry: true,
  },
  no_role: {
    title: 'Tài khoản chưa có quyền CMS',
    message: 'Bạn đã đăng nhập thành công nhưng chưa được cấp vai trò trên CMS. Liên hệ quản trị viên để được cấp quyền, sau đó đăng nhập lại.',
    retry: false,
  },
  inactive: {
    title: 'Tài khoản đang bị đình chỉ',
    message: 'Tài khoản của bạn đang bị đình chỉ hoặc ngừng hoạt động. Liên hệ quản trị viên nếu bạn cho rằng đây là nhầm lẫn.',
    retry: false,
  },
};

const BY_CODE: Record<string, LoginError> = {
  OIDC_STATE_INVALID: BY_REASON.state_invalid,
  BAD_REQUEST: BY_REASON.idp_error,
  FORBIDDEN: { title: 'Không thể đăng nhập', message: 'Tài khoản không được phép truy cập CMS.', retry: false },
};

/** Từ query của /login → thông điệp hiển thị; null khi không có lỗi. Mã/lý do lạ vẫn có thông điệp chung. */
export function describeLoginError(search: string): { error: LoginError; code: string } | null {
  const params = new URLSearchParams(search);
  const code = params.get('error');
  if (!code) return null;
  const reason = params.get('reason') ?? '';
  const error = BY_REASON[reason] ?? BY_CODE[code] ?? { title: 'Không thể đăng nhập', message: 'Đã xảy ra lỗi khi đăng nhập. Hãy thử lại.', retry: true };
  return { error, code };
}

/** Trang cần quay lại sau đăng nhập: trên /login lấy tham số returnTo, nơi khác lấy chính trang hiện tại. */
export function returnToFor(pathname: string, search: string): string {
  if (pathname === '/login') return sanitizeReturnTo(new URLSearchParams(search).get('returnTo'));
  return sanitizeReturnTo(pathname + search);
}

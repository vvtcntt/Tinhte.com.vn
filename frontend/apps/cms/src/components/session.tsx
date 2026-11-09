'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ApiError, apiFetch, loginUrl } from '../lib/api';
import { returnToFor } from '../lib/loginErrors';
import type { StaffSession } from '../lib/rbac';

export type SessionState =
  | { status: 'loading' }
  | { status: 'anonymous' }
  | { status: 'ready'; session: StaffSession }
  | { status: 'error'; error: ApiError | Error };

interface SessionContextValue {
  state: SessionState;
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
  signIn: () => void;
  logoutError: string | null;
}

const SessionContext = createContext<SessionContextValue | null>(null);

/** Phiên staff đọc từ GET /admin/auth/session (cookie HttpOnly nên JS không tự đọc được). */
export function SessionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<SessionState>({ status: 'loading' });
  const [logoutError, setLogoutError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const { data } = await apiFetch<StaffSession>('/admin/auth/session');
      setState(data ? { status: 'ready', session: data } : { status: 'anonymous' });
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) setState({ status: 'anonymous' });
      else setState({ status: 'error', error: e instanceof Error ? e : new Error(String(e)) });
    }
  }, []);

  const logout = useCallback(async () => {
    setLogoutError(null);
    try {
      await apiFetch('/auth/staff/logout', { method: 'POST' });
      setState({ status: 'anonymous' });
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) setState({ status: 'anonymous' });
      else setLogoutError(e instanceof ApiError ? e.problem.detail ?? e.problem.title : 'Không đăng xuất được. Thử lại.');
    }
  }, []);

  const signIn = useCallback(() => {
    window.location.assign(loginUrl(returnToFor(window.location.pathname, window.location.search)));
  }, []);

  useEffect(() => {
    void refresh();
    // Quay lại tab sau một lúc: kiểm lại phiên (hết hạn idle 30', bị thu hồi…).
    const onFocus = () => document.visibilityState === 'visible' && void refresh();
    document.addEventListener('visibilitychange', onFocus);
    return () => document.removeEventListener('visibilitychange', onFocus);
  }, [refresh]);

  const value = useMemo(() => ({ state, refresh, logout, signIn, logoutError }), [state, refresh, logout, signIn, logoutError]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (!value) throw new Error('useSession must be used inside <SessionProvider>');
  return value;
}

/** Chỉ dùng bên trong khung đã đăng nhập. */
export function useStaff(): StaffSession {
  const { state } = useSession();
  if (state.status !== 'ready') throw new Error('useStaff requires a ready session');
  return state.session;
}

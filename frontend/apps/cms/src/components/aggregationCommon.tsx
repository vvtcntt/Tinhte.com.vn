'use client';

import { useEffect, useState, type ReactNode } from 'react';
import { registry, type SiteDefinition } from '@tinhte/sites';
import { ApiError, apiFetch } from '../lib/api';
import type { CategoryRule } from '../lib/aggregation';

// Phần dùng chung của màn Radar tin + Quản lý nguồn (WBS-144).

/** Thông báo lỗi tiếng Việt cho Problem của API (mã theo x-error-catalog). */
export function aggErrorText(e: unknown): string {
  if (e instanceof ApiError) {
    switch (e.code) {
      case 'FEATURE_DISABLED':
        return 'Radar tin chưa được bật ở môi trường này (cờ aggregation.radar đang tắt).';
      case 'REVISION_CONFLICT':
        return 'Dữ liệu vừa được người khác (hoặc worker) thay đổi. Tải lại để xem bản mới nhất rồi thử lại.';
      case 'STATE_CONFLICT':
        return e.problem.detail ?? 'Trạng thái hiện tại không cho phép thao tác này.';
      case 'UNSAFE_URL':
        return `URL không an toàn: ${e.problem.detail ?? 'trỏ vào địa chỉ nội bộ hoặc không phải https.'}`;
      case 'RATE_LIMITED':
        return e.problem.detail ?? 'Thao tác quá nhanh. Đợi một chút rồi thử lại.';
      case 'FORBIDDEN':
        return e.problem.detail ?? 'Bạn không có quyền thực hiện thao tác này.';
      case 'SOD_VIOLATION':
        return 'Người đề xuất nguồn không được tự duyệt chế độ M2/M3 cho nguồn đó.';
      default: {
        const fields = e.problem.errors ? Object.values(e.problem.errors).flat().join(' ') : '';
        return [e.problem.detail ?? e.problem.title, fields].filter(Boolean).join(' ');
      }
    }
  }
  return 'Không kết nối được API. Thử lại.';
}

export function isConflict(e: unknown): boolean {
  return e instanceof ApiError && e.code === 'REVISION_CONFLICT';
}

/** Site nội dung (public-web, chưa retired) của Site Registry — Radar, luật chuyên mục và hồ sơ nguồn đều theo site (ADR-0001). */
export function contentSites(): SiteDefinition[] {
  return registry.sites.filter((s) => s.kind === 'public-web' && s.status !== 'retired');
}

export function newIdempotencyKey(prefix: string): string {
  return `${prefix}-${crypto.randomUUID()}`;
}

/** Chuyên mục đang bật của một site (qua luật phân loại — mỗi chuyên mục một dòng). */
export function useSiteCategories(siteKey: string): { categories: CategoryRule[]; error: string | null } {
  const [categories, setCategories] = useState<CategoryRule[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let cancelled = false;
    setError(null);
    apiFetch<{ items: CategoryRule[] }>(`/admin/aggregation/category-rules?siteKey=${encodeURIComponent(siteKey)}`)
      .then((r) => !cancelled && setCategories(r.data?.items ?? []))
      .catch((e) => !cancelled && setError(aggErrorText(e)));
    return () => {
      cancelled = true;
    };
  }, [siteKey]);
  return { categories, error };
}

export function Banner({ kind, children, onRetry }: { kind: 'ok' | 'warn' | 'error'; children: ReactNode; onRetry?: () => void }) {
  return (
    <div className={`banner ${kind}`} role={kind === 'error' ? 'alert' : 'status'}>
      <span>{children}</span>
      {onRetry && (
        <button type="button" className="button small" onClick={onRetry}>
          Tải lại
        </button>
      )}
    </div>
  );
}

export function SiteSelect({ value, onChange }: { value: string; onChange: (key: string) => void }) {
  const sites = contentSites();
  if (sites.length <= 1) return null;
  return (
    <label className="field">
      <span>Site</span>
      <select value={value} onChange={(e) => onChange(e.target.value)}>
        {sites.map((s) => (
          <option key={s.key} value={s.key}>
            {s.label}
          </option>
        ))}
      </select>
    </label>
  );
}

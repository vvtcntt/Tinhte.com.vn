'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { ApiError, apiFetch } from '../lib/api';
import { formatTime, statusLabel, type AdminArticle } from '../lib/articles';
import { access, type PermissionKey } from '../lib/rbac';
import { badgeText, isOverdue, queueUrl, visibleQueues, waitingFor, type QueueDef, type QueueKey } from '../lib/review';
import { RequirePermission } from './CmsApp';
import { useStaff } from './session';

// WBS-031: hàng đợi duyệt theo vai trò + số việc trên menu và trang Tổng quan.

interface Page {
  items: AdminArticle[];
  nextCursor: string | null;
}

function errorText(e: unknown): string {
  return e instanceof ApiError ? e.problem.detail ?? e.problem.title : 'Không kết nối được API. Thử lại.';
}

function useQueues(): QueueDef[] {
  const staff = useStaff();
  return visibleQueues((p: PermissionKey) => access(staff, p));
}

// ---------------------------------------------------------------- số việc (menu, Tổng quan)

export interface QueueCount {
  queue: QueueDef;
  count: number;
  hasMore: boolean;
}

/**
 * Một nguồn đếm dùng chung cho mọi nơi hiển thị (menu, Tổng quan, tab): một lượt gọi API cho mỗi hàng đợi (tối đa 100 dòng),
 * tải lại mỗi 60 giây, khi quay lại tab và khi có sự kiện 'tt:review-changed' (sau thao tác duyệt/xuất bản).
 */
const store = {
  key: '',
  counts: null as QueueCount[] | null,
  listeners: new Set<(c: QueueCount[] | null) => void>(),
  stop: null as (() => void) | null,
};

function startStore(key: string, queues: QueueDef[], userId: string) {
  store.stop?.();
  store.key = key;
  store.counts = null;
  const load = async () => {
    try {
      const results = await Promise.all(
        queues.map(async (queue) => {
          const { data } = await apiFetch<Page>(queueUrl(queue, userId, 100));
          return { queue, count: data?.items.length ?? 0, hasMore: !!data?.nextCursor };
        }),
      );
      if (store.key !== key) return; // phiên/quyền đã đổi trong lúc chờ
      store.counts = results;
      store.listeners.forEach((l) => l(results));
    } catch {
      // Số đếm chỉ để gợi ý; lỗi tạm thời không chặn giao diện.
    }
  };
  void load();
  const timer = window.setInterval(() => void load(), 60_000);
  const onVisible = () => document.visibilityState === 'visible' && void load();
  const onChanged = () => void load();
  document.addEventListener('visibilitychange', onVisible);
  window.addEventListener('tt:review-changed', onChanged);
  store.stop = () => {
    window.clearInterval(timer);
    document.removeEventListener('visibilitychange', onVisible);
    window.removeEventListener('tt:review-changed', onChanged);
  };
}

export function useReviewCounts(): QueueCount[] | null {
  const staff = useStaff();
  const queues = useQueues();
  const key = `${staff.userId}|${queues.map((q) => q.key).join(',')}`;
  const [counts, setCounts] = useState<QueueCount[] | null>(store.key === key ? store.counts : null);

  useEffect(() => {
    if (store.key !== key) startStore(key, queues, staff.userId);
    else setCounts(store.counts);
    store.listeners.add(setCounts);
    return () => {
      store.listeners.delete(setCounts);
      if (store.listeners.size === 0) {
        store.stop?.();
        store.stop = null;
        store.key = '';
      }
    };
    // queues thay đổi đúng khi key thay đổi.
  }, [key]); // eslint-disable-line react-hooks/exhaustive-deps

  return counts;
}

/** Tổng việc đang chờ chính người này (cho huy hiệu menu "Duyệt bài"). */
export function ReviewBadge() {
  const counts = useReviewCounts();
  if (!counts) return null;
  const actionable = counts.filter((c) => c.queue.actionable);
  const total = actionable.reduce((sum, c) => sum + c.count, 0);
  const text = badgeText(total, actionable.some((c) => c.hasMore));
  return text ? (
    <span className="count" aria-label={`${text} việc đang chờ`}>
      {text}
    </span>
  ) : null;
}

/** Thẻ "Việc của bạn" trên trang Tổng quan. */
export function ReviewSummaryCard() {
  const counts = useReviewCounts();
  if (counts && counts.length === 0) return null;
  return (
    <div className="card">
      <h2>Việc của bạn</h2>
      {!counts ? (
        <p className="muted small">Đang tải…</p>
      ) : (
        <ul className="perm-list">
          {counts.map(({ queue, count, hasMore }) => (
            <li key={queue.key}>
              <Link href={`/review?tab=${queue.key}`}>{queue.label}</Link>
              <span className={count > 0 && queue.actionable ? 'badge warn' : 'badge'}>{badgeText(count, hasMore) ?? '0'}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- màn hàng đợi

export function ReviewPage() {
  return (
    <RequirePermission permission="content.read">
      <ReviewQueues />
    </RequirePermission>
  );
}

function ReviewQueues() {
  const queues = useQueues();
  const counts = useReviewCounts();
  const [tab, setTab] = useState<QueueKey | null>(null);

  // Tab ban đầu: ?tab= nếu hợp lệ, không thì hàng đợi đầu tiên hiển thị được.
  useEffect(() => {
    const wanted = new URLSearchParams(window.location.search).get('tab');
    setTab((queues.find((q) => q.key === wanted) ?? queues[0])?.key ?? null);
  }, [queues.map((q) => q.key).join(',')]);

  const select = (key: QueueKey) => {
    setTab(key);
    const url = new URL(window.location.href);
    url.searchParams.set('tab', key);
    window.history.replaceState(null, '', url);
  };

  if (queues.length === 0) {
    return (
      <section className="stack">
        <h1>Duyệt bài</h1>
        <div className="card empty">
          <p>Vai trò của bạn không tham gia quy trình duyệt bài.</p>
        </div>
      </section>
    );
  }

  const current = queues.find((q) => q.key === tab);
  return (
    <section className="stack">
      <div>
        <h1>Duyệt bài</h1>
        <p className="muted">Các việc trong quy trình soạn → duyệt → xuất bản, lọc theo vai trò của bạn. Mở bài để duyệt, trả lại hoặc xuất bản.</p>
      </div>
      <div className="tabs-bar" role="tablist" aria-label="Hàng đợi">
        {queues.map((q) => {
          const c = counts?.find((x) => x.queue.key === q.key);
          const text = c ? badgeText(c.count, c.hasMore) : null;
          return (
            <button key={q.key} type="button" role="tab" aria-selected={q.key === tab} className={q.key === tab ? 'tab active' : 'tab'} onClick={() => select(q.key)}>
              {q.label}
              {text && <span className={q.actionable ? 'count' : 'count muted-count'}>{text}</span>}
            </button>
          );
        })}
      </div>
      {current && <QueueList key={current.key} queue={current} />}
    </section>
  );
}

function QueueList({ queue }: { queue: QueueDef }) {
  const staff = useStaff();
  const [items, setItems] = useState<AdminArticle[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => new Date());

  const load = useCallback(
    async (after: string | null) => {
      setLoading(true);
      setError(null);
      try {
        const { data } = await apiFetch<Page>(queueUrl(queue, staff.userId, 20, after));
        setItems((prev) => (after ? [...prev, ...(data?.items ?? [])] : data?.items ?? []));
        setCursor(data?.nextCursor ?? null);
        setNow(new Date());
      } catch (e) {
        setError(errorText(e));
      } finally {
        setLoading(false);
      }
    },
    [queue, staff.userId],
  );

  useEffect(() => void load(null), [load]);

  return (
    <div className="stack-sm">
      <p className="muted small">{queue.description}</p>
      {error && (
        <div className="banner error" role="alert">
          <span>{error}</span>
          <button type="button" className="button small" onClick={() => void load(null)}>
            Thử lại
          </button>
        </div>
      )}
      <div className="card flush">
        <table className="table">
          <thead>
            <tr>
              <th scope="col">Bài viết</th>
              <th scope="col">Trạng thái</th>
              <th scope="col">Đã chờ</th>
            </tr>
          </thead>
          <tbody>
            {items.map((a) => {
              const mine = a.ownerId === staff.userId;
              const overdue = queue.actionable && isOverdue(a.updatedAt, now);
              return (
                <tr key={a.id}>
                  <td>
                    <Link href={`/articles/${a.id}`}>{a.title}</Link>
                    <div className="muted small">
                      {mine ? 'Bài của bạn' : 'Bài của người khác'}
                      {a.contentKind === 'SPONSORED' && ' · Tài trợ'}
                      {a.publicRevisionId && ' · Đang có bản công khai'}
                    </div>
                    {mine && queue.key === 'to-review' && <div className="small warn-text">Không tự duyệt được bài của mình.</div>}
                  </td>
                  <td>
                    <span className="badge">{statusLabel(a.status)}</span>
                  </td>
                  <td>
                    <span className={overdue ? 'badge warn' : 'muted'} title={formatTime(a.updatedAt)}>
                      {waitingFor(a.updatedAt, now)}
                    </span>
                  </td>
                </tr>
              );
            })}
            {!loading && items.length === 0 && !error && (
              <tr>
                <td colSpan={3} className="muted center">
                  {queue.empty}
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      <div className="row-end">
        {loading && <span className="muted">Đang tải…</span>}
        {!loading && cursor && (
          <button type="button" className="button" onClick={() => void load(cursor)}>
            Xem thêm
          </button>
        )}
      </div>
    </div>
  );
}

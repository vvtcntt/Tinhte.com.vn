'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from '../lib/api';
import {
  ALERT_LABEL,
  CLUSTER_STATUS_LABEL,
  CLUSTER_STATUSES,
  DEFAULT_CLUSTER_STATUSES,
  DEFAULT_RADAR_WINDOW,
  type RadarWindowMode,
  ago,
  allowedClusterActions,
  clusterBadges,
  clustersQuery,
  dateTimeVi,
  etagOf,
  isUuid,
  label,
  percent,
  reasonError,
  safeExternalUrl,
  splitError,
  type ClusterDetail,
  type ClusterSummary,
  type Page,
  type RadarFilter,
  type Stats,
} from '../lib/aggregation';
import { RequirePermission } from './CmsApp';
import { Banner, SiteSelect, aggErrorText, contentSites, isConflict, newIdempotencyKey, useSiteCategories } from './aggregationCommon';
import { useStaff } from './session';

// Màn Radar tin + chi tiết cụm (WBS-144, BA-01 mục 9; ADR-0008). Mọi thao tác trên cụm chỉ cần aggregation.read (hợp đồng);
// API kiểm trạng thái (409), If-Match (412) và cờ aggregation.radar (403 FEATURE_DISABLED).

const PAGE_SIZE = 20;

function statusBadge(status: string) {
  const cls = status === 'OPEN' ? 'badge ok' : status === 'DISMISSED' || status === 'CLOSED' ? 'badge' : 'badge warn';
  return <span className={cls}>{label(CLUSTER_STATUS_LABEL, status)}</span>;
}

function ScoreBadge({ score }: { score: number }) {
  const level = score >= 60 ? 'high' : score >= 35 ? 'mid' : 'low';
  return (
    <span className={`score score-${level}`} title="Điểm ưu tiên 0–100 (BR-32)">
      {Math.round(score)}
    </span>
  );
}

// ---------------------------------------------------------------- Radar

export function RadarPage() {
  return (
    <RequirePermission permission="aggregation.read">
      <Radar />
    </RequirePermission>
  );
}

function Radar() {
  const staff = useStaff();
  const [siteKey, setSiteKey] = useState(() => contentSites()[0]?.key ?? 'www');
  const [windowMode, setWindowMode] = useState<RadarWindowMode>(DEFAULT_RADAR_WINDOW);
  const [date, setDate] = useState('');
  const [categoryId, setCategoryId] = useState('');
  const [statuses, setStatuses] = useState<string[]>([...DEFAULT_CLUSTER_STATUSES]);
  const [minScore, setMinScore] = useState('');
  const [items, setItems] = useState<ClusterSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [stats, setStats] = useState<Stats | null>(null);
  const { categories } = useSiteCategories(siteKey);
  const categoryName = useMemo(() => new Map(categories.map((c) => [c.categoryId, c.categoryName])), [categories]);

  const filter: RadarFilter = useMemo(
    () => ({
      siteKey,
      window: windowMode === 'LAST_24H' ? 'LAST_24H' : 'DAY',
      date: windowMode === 'DATE' && date ? date : null,
      categoryId: categoryId || null,
      statuses,
      minScore: minScore.trim() === '' ? null : Number(minScore),
    }),
    [siteKey, windowMode, date, categoryId, statuses, minScore],
  );

  const load = useCallback(
    async (after: string | null) => {
      setLoading(true);
      setError(null);
      try {
        const { data } = await apiFetch<Page<ClusterSummary>>(clustersQuery(filter, PAGE_SIZE, after));
        setItems((prev) => (after ? [...prev, ...(data?.items ?? [])] : data?.items ?? []));
        setCursor(data?.nextCursor ?? null);
      } catch (e) {
        setError(aggErrorText(e));
      } finally {
        setLoading(false);
      }
    },
    [filter],
  );

  const loadStats = useCallback(async () => {
    try {
      const params = new URLSearchParams({ siteKey });
      if (windowMode === 'DATE' && date) params.set('date', date);
      setStats((await apiFetch<Stats>(`/admin/aggregation/stats?${params}`)).data);
    } catch {
      setStats(null);   // thống kê lỗi không chặn Radar
    }
  }, [siteKey, windowMode, date]);

  useEffect(() => void load(null), [load]);
  useEffect(() => void loadStats(), [loadStats]);

  const toggleStatus = (s: string) => setStatuses((cur) => (cur.includes(s) ? cur.filter((x) => x !== s) : [...cur, s]));
  const replace = (c: ClusterSummary) => setItems((cur) => cur.map((x) => (x.id === c.id ? c : x)));

  return (
    <section className="stack">
      <div className="title-row spread">
        <div>
          <h1>Radar tin</h1>
          <p className="muted">
            Tin mới từ các nguồn đã duyệt, đã khử trùng và gom theo sự kiện. Không có AI viết lại: đọc bài gốc rồi tự viết. Điểm cao ở trên cùng.
          </p>
        </div>
        <Link href="/aggregation" className="button">
          Nguồn thu thập
        </Link>
      </div>

      <RadarHealth stats={stats} />

      <form className="toolbar" onSubmit={(e) => e.preventDefault()} aria-label="Lọc cụm">
        <SiteSelect value={siteKey} onChange={setSiteKey} />
        <label className="field">
          <span>Thời gian</span>
          <select value={windowMode} onChange={(e) => setWindowMode(e.target.value as typeof windowMode)}>
            <option value="TODAY">Hôm nay</option>
            <option value="LAST_24H">24 giờ qua</option>
            <option value="DATE">Chọn ngày</option>
          </select>
        </label>
        {windowMode === 'DATE' && (
          <label className="field">
            <span>Ngày</span>
            <input type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          </label>
        )}
        <label className="field">
          <span>Chuyên mục</span>
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">Tất cả</option>
            {categories.map((c) => (
              <option key={c.categoryId} value={c.categoryId}>
                {c.categoryName}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Điểm từ</span>
          <input type="number" min={0} max={100} step={5} value={minScore} onChange={(e) => setMinScore(e.target.value)} className="narrow-input" />
        </label>
        <fieldset className="checks inline-checks">
          <legend>Trạng thái</legend>
          {CLUSTER_STATUSES.map((s) => (
            <label key={s} className="check inline">
              <input type="checkbox" checked={statuses.includes(s)} onChange={() => toggleStatus(s)} />
              <span>{CLUSTER_STATUS_LABEL[s]}</span>
            </label>
          ))}
        </fieldset>
      </form>

      {error && <Banner kind="error" onRetry={() => void load(null)}>{error}</Banner>}

      <ul className="cluster-list">
        {items.map((c) => (
          <ClusterCard key={c.id} cluster={c} userId={staff.userId} categoryName={c.primaryCategoryId ? categoryName.get(c.primaryCategoryId) : undefined}
            onChanged={replace} />
        ))}
      </ul>
      {!loading && items.length === 0 && !error && <div className="card empty muted">Không có cụm nào khớp bộ lọc.</div>}

      <div className="row-end">
        {loading && <span className="muted">Đang tải…</span>}
        {!loading && cursor && (
          <button type="button" className="button" onClick={() => void load(cursor)}>
            Xem thêm
          </button>
        )}
      </div>
    </section>
  );
}

/** Tóm tắt thu thập + worker + cảnh báo nguồn (BR-06: nguồn tự tạm dừng/suy giảm). */
function RadarHealth({ stats }: { stats: Stats | null }) {
  if (!stats) return null;
  const workers = stats.workers ?? [];
  const healthy = workers.filter((w) => w.healthy).length;
  const alerts = (stats.alerts ?? []).slice(0, 5);
  return (
    <div className="stack-sm">
      <div className="stat-strip">
        <div className="stat"><strong>{stats.itemsToday}</strong><span>mục tin</span></div>
        <div className="stat"><strong>{stats.clustersToday}</strong><span>cụm</span></div>
        <div className="stat"><strong>{stats.openClustersToday}</strong><span>cụm mới</span></div>
        <div className="stat"><strong>{percent(stats.fetchSuccessRate24h)}</strong><span>tải thành công 24 giờ</span></div>
        <div className="stat">
          <strong className={healthy > 0 ? 'ok-text' : 'warn-text'}>{healthy}</strong>
          <span>worker hoạt động</span>
        </div>
      </div>
      {healthy === 0 && (
        <Banner kind="warn">
          Không thấy worker thu thập nào hoạt động trong 5 phút qua{workers[0] ? ` (lần cuối ${ago(workers[0].lastSeenAt)})` : ''}. Tin mới sẽ không về
          Radar cho tới khi worker chạy lại.
        </Banner>
      )}
      {alerts.length > 0 && (
        <div className="banner warn stacked" role="status">
          <strong>Cảnh báo nguồn</strong>
          {alerts.map((a) => (
            <span key={`${a.sourceId}-${a.createdAt}`}>
              {label(ALERT_LABEL, a.kind)}: <Link href={`/aggregation/sources/${a.sourceId}`}>{a.sourceName}</Link> — {a.message}{' '}
              <span className="muted">({ago(a.createdAt)})</span>
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

function ClusterCard({ cluster, userId, categoryName, onChanged }: {
  cluster: ClusterSummary;
  userId: string;
  categoryName?: string;
  onChanged: (c: ClusterSummary) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const actions = allowedClusterActions(cluster);

  const act = async (action: 'watch' | 'claim') => {
    setBusy(true);
    setError(null);
    try {
      const r = await apiFetch<ClusterSummary>(`/admin/aggregation/clusters/${cluster.id}/${action}`, { method: 'POST', ifMatch: etagOf(cluster.rowVersion) });
      if (r.data) onChanged(r.data);
    } catch (e) {
      setError(aggErrorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <li className="cluster-card">
      <ScoreBadge score={cluster.score} />
      <div className="cluster-body">
        <Link href={`/radar/${cluster.id}`} className="cluster-title">
          {cluster.title}
        </Link>
        <div className="cluster-meta">
          {statusBadge(cluster.status)}
          {clusterBadges(cluster).map((b) => (
            <span key={b} className={b === 'Nhạy cảm' ? 'badge warn' : 'badge'}>{b}</span>
          ))}
          {cluster.assigneeId && <span className="badge ok">{cluster.assigneeId === userId ? 'Bạn đang xử lý' : 'Đang có người xử lý'}</span>}
          {cluster.coveredArticleId && <span className="badge ok">Đã đưa tin</span>}
        </div>
        <p className="muted small">
          {cluster.independentSourceCount} nguồn ({cluster.itemCount} mục) · sớm nhất {ago(cluster.firstSeenAt)} · mới nhất {ago(cluster.lastItemAt)}
          {categoryName && ` · ${categoryName}`}
          {cluster.categoryNeedsReview && ' (cần xem)'}
        </p>
        {error && <p className="field-error">{error}</p>}
      </div>
      <div className="cluster-actions">
        {actions.includes('watch') && (
          <button type="button" className="button small" disabled={busy} onClick={() => void act('watch')}>Theo dõi</button>
        )}
        {actions.includes('claim') && (
          <button type="button" className="button small" disabled={busy} onClick={() => void act('claim')}>Nhận việc</button>
        )}
      </div>
    </li>
  );
}

// ---------------------------------------------------------------- chi tiết cụm

export function ClusterDetailPage({ clusterId }: { clusterId: string }) {
  return (
    <RequirePermission permission="aggregation.read">
      <ClusterView clusterId={clusterId} />
    </RequirePermission>
  );
}

function ClusterView({ clusterId }: { clusterId: string }) {
  const staff = useStaff();
  const router = useRouter();
  const [cluster, setCluster] = useState<ClusterDetail | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string; conflict?: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [selected, setSelected] = useState<string[]>([]);
  const [reason, setReason] = useState('');
  const [articleId, setArticleId] = useState('');
  const [targetId, setTargetId] = useState('');
  const { categories } = useSiteCategories(cluster?.siteKey ?? contentSites()[0]?.key ?? 'www');

  const reload = useCallback(async () => {
    setLoadError(null);
    try {
      const r = await apiFetch<ClusterDetail>(`/admin/aggregation/clusters/${encodeURIComponent(clusterId)}`);
      setCluster(r.data);
      setEtag(r.etag);
      setSelected([]);
    } catch (e) {
      setLoadError(aggErrorText(e));
    }
  }, [clusterId]);

  useEffect(() => void reload(), [reload]);

  if (loadError) {
    return (
      <section className="stack">
        <Link href="/radar" className="back">← Radar tin</Link>
        <Banner kind="error">{loadError}</Banner>
      </section>
    );
  }

  if (!cluster) return <p className="muted">Đang tải…</p>;
  const actions = allowedClusterActions(cluster);
  const category = categories.find((c) => c.categoryId === cluster.primaryCategoryId)?.categoryName;

  /** Gửi thao tác; split trả 201 + cụm mới → chuyển sang cụm mới; merge trả cụm đích → chuyển sang cụm đích. */
  const run = async (action: string, body?: unknown, okText?: string) => {
    setBusy(true);
    setMessage(null);
    try {
      const r = await apiFetch<ClusterDetail>(`/admin/aggregation/clusters/${cluster.id}/${action}`, {
        method: 'POST',
        body,
        ifMatch: etag ?? etagOf(cluster.rowVersion),
        idempotencyKey: action === 'split' ? newIdempotencyKey('split') : undefined,
      });
      setReason('');
      if (r.data && r.data.id !== cluster.id) {
        router.push(`/radar/${r.data.id}`);
        return;
      }
      if (r.data) {
        setCluster(r.data);
        setEtag(r.etag);
        setSelected([]);
      }
      setMessage({ kind: 'ok', text: okText ?? 'Đã cập nhật cụm.' });
    } catch (e) {
      setMessage({ kind: 'error', text: aggErrorText(e), conflict: isConflict(e) });
    } finally {
      setBusy(false);
    }
  };

  const onDismiss = (e: FormEvent) => {
    e.preventDefault();
    const err = reasonError(reason);
    if (err) return setMessage({ kind: 'error', text: err });
    void run('dismiss', { reason: reason.trim() }, 'Đã bỏ qua cụm.');
  };

  const onCover = (e: FormEvent) => {
    e.preventDefault();
    if (!isUuid(articleId)) return setMessage({ kind: 'error', text: 'Nhập mã bài viết (UUID) trên CMS.' });
    void run('coverage', { articleId: articleId.trim() }, 'Đã gắn bài đưa tin.');
  };

  const onSplit = () => {
    const err = splitError(selected.length, cluster.items.length);
    if (err) return setMessage({ kind: 'error', text: err });
    void run('split', { itemIds: selected, reason: reason.trim() || undefined });
  };

  const onMerge = (e: FormEvent) => {
    e.preventDefault();
    if (!isUuid(targetId)) return setMessage({ kind: 'error', text: 'Nhập mã cụm đích (UUID, lấy ở địa chỉ trang chi tiết cụm).' });
    if (targetId.trim().toLowerCase() === cluster.id.toLowerCase()) return setMessage({ kind: 'error', text: 'Không gộp một cụm vào chính nó.' });
    void run('merge', { targetClusterId: targetId.trim(), reason: reason.trim() || undefined });
  };

  return (
    <section className="stack">
      <Link href="/radar" className="back">← Radar tin</Link>
      <div className="title-row">
        <ScoreBadge score={cluster.score} />
        <h1>{cluster.title}</h1>
      </div>
      <div className="cluster-meta">
        {statusBadge(cluster.status)}
        {clusterBadges(cluster).map((b) => (
          <span key={b} className={b === 'Nhạy cảm' ? 'badge warn' : 'badge'}>{b}</span>
        ))}
      </div>

      {cluster.labels.includes('SENSITIVE') && (
        <Banner kind="warn">Chủ đề nhạy cảm (BR-34): viết theo quy trình thường, kiểm chứng kỹ trước khi đưa tin.</Banner>
      )}
      {cluster.labels.includes('NEW_SINCE_COVERED') && (
        <Banner kind="warn">Có thông tin mới sau khi đã đưa tin — cân nhắc cập nhật bài.</Banner>
      )}

      <div className="card">
        <dl className="facts">
          <dt>Nguồn</dt>
          <dd>{cluster.independentSourceCount} nguồn độc lập · {cluster.sourceCount} feed · {cluster.itemCount} mục</dd>
          <dt>Chuyên mục gợi ý</dt>
          <dd>{category ?? (cluster.primaryCategoryId ? <code>{cluster.primaryCategoryId}</code> : 'Chưa xác định')}{cluster.categoryNeedsReview && ' (cần xem)'}</dd>
          <dt>Thời gian</dt>
          <dd>Sớm nhất {dateTimeVi(cluster.firstSeenAt)} · mới nhất {dateTimeVi(cluster.lastItemAt)}</dd>
          <dt>Chế độ khai thác cao nhất</dt>
          <dd>{cluster.maxUsageMode}</dd>
          <dt>Người xử lý</dt>
          <dd>{cluster.assigneeId ? (cluster.assigneeId === staff.userId ? 'Bạn' : <code>{cluster.assigneeId}</code>) : 'Chưa có'}</dd>
          <dt>Bài đã đưa</dt>
          <dd>{cluster.coveredArticleId ? <Link href={`/articles/${cluster.coveredArticleId}`}>Mở bài trên CMS</Link> : '—'}</dd>
        </dl>
      </div>

      {message && (
        <div className={message.kind === 'ok' ? 'banner ok' : 'banner error'} role={message.kind === 'ok' ? 'status' : 'alert'}>
          <span>{message.text}</span>
          {message.conflict && (
            <button type="button" className="button small" onClick={() => void reload().then(() => setMessage(null))}>Tải lại</button>
          )}
        </div>
      )}

      <div className="card stack-sm">
        <h2>Thao tác</h2>
        <div className="row-start">
          {actions.includes('watch') && <button type="button" className="button" disabled={busy} onClick={() => void run('watch', undefined, 'Đang theo dõi cụm.')}>Theo dõi</button>}
          {actions.includes('reopen') && <button type="button" className="button" disabled={busy} onClick={() => void run('reopen', undefined, 'Đã mở lại cụm.')}>Mở lại</button>}
          {actions.includes('claim') && <button type="button" className="button primary" disabled={busy} onClick={() => void run('claim', undefined, 'Bạn đã nhận việc cụm này.')}>Nhận việc</button>}
        </div>
        <label className="field">
          <span>Lý do (bắt buộc khi bỏ qua; ghi kèm khi tách/gộp)</span>
          <textarea rows={2} maxLength={2000} value={reason} onChange={(e) => setReason(e.target.value)} />
        </label>
        <div className="grid-2">
          {actions.includes('dismiss') && (
            <form onSubmit={onDismiss} className="inline-form">
              <button type="submit" className="button danger" disabled={busy}>Bỏ qua cụm</button>
            </form>
          )}
          {actions.includes('coverage') && (
            <form onSubmit={onCover} className="inline-form">
              <label className="field grow">
                <span>Mã bài đã đưa tin</span>
                <input value={articleId} onChange={(e) => setArticleId(e.target.value)} placeholder="UUID bài viết" />
              </label>
              <button type="submit" className="button" disabled={busy}>Đánh dấu đã đưa tin</button>
            </form>
          )}
          {actions.includes('merge') && (
            <form onSubmit={onMerge} className="inline-form">
              <label className="field grow">
                <span>Gộp vào cụm</span>
                <input value={targetId} onChange={(e) => setTargetId(e.target.value)} placeholder="UUID cụm đích" />
              </label>
              <button type="submit" className="button" disabled={busy}>Gộp</button>
            </form>
          )}
        </div>
      </div>

      <div className="card stack-sm">
        <div className="title-row spread">
          <h2>Dòng thời gian ({cluster.items.length})</h2>
          {actions.includes('split') && cluster.items.length > 1 && (
            <button type="button" className="button small" disabled={busy || selected.length === 0} onClick={onSplit}>
              Tách {selected.length || ''} mục sang cụm mới
            </button>
          )}
        </div>
        <ol className="timeline">
          {cluster.items.map((item) => {
            const href = safeExternalUrl(item.url);
            return (
              <li key={item.id} className="timeline-item">
                {actions.includes('split') && cluster.items.length > 1 && (
                  <input type="checkbox" aria-label={`Chọn để tách: ${item.title}`} checked={selected.includes(item.id)}
                    onChange={() => setSelected((s) => (s.includes(item.id) ? s.filter((x) => x !== item.id) : [...s, item.id]))} />
                )}
                <div>
                  <p className="muted small">
                    <strong>{item.sourceName}</strong> · {dateTimeVi(item.publishedAt ?? item.firstSeenAt)}
                    {!item.timeReliable && <span className="badge warn" title="Thời điểm của nguồn thiếu/không tin được — dùng lúc thu thập">giờ không chắc</span>}
                    {item.version > 1 && <span className="badge">nguồn đã sửa (v{item.version})</span>}
                    {item.sensitive && <span className="badge warn">nhạy cảm</span>}
                  </p>
                  {href ? (
                    <a href={href} target="_blank" rel="noopener noreferrer nofollow" className="timeline-title">{item.title}</a>
                  ) : (
                    <span className="timeline-title">{item.title}</span>
                  )}
                  {item.summary && <p className="small">{item.summary}</p>}
                </div>
              </li>
            );
          })}
          {cluster.items.length === 0 && <li className="muted">Cụm đã được gộp sang cụm khác (xem lịch sử).</li>}
        </ol>
      </div>

      <div className="card stack-sm">
        <h2>Lịch sử thao tác</h2>
        {cluster.actions.length === 0 ? (
          <p className="muted">Chưa có thao tác nào.</p>
        ) : (
          <ul className="history">
            {cluster.actions.map((a, i) => (
              <li key={`${a.at}-${i}`}>
                <strong>{ACTION_LABEL[a.action] ?? a.action}</strong> · {dateTimeVi(a.at)} · <code>{a.actorId === staff.userId ? 'bạn' : a.actorId.slice(0, 8)}</code>
                {a.reason && <> — {a.reason}</>}
                {a.relatedClusterId && <> · <Link href={`/radar/${a.relatedClusterId}`}>cụm liên quan</Link></>}
                {a.articleId && <> · <Link href={`/articles/${a.articleId}`}>bài viết</Link></>}
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

const ACTION_LABEL: Record<string, string> = {
  DISMISS: 'Bỏ qua',
  WATCH: 'Theo dõi',
  REOPEN: 'Mở lại',
  CLAIM: 'Nhận việc',
  COVER: 'Đã đưa tin',
  SPLIT: 'Tách',
  MERGE: 'Gộp',
};

// Radar tin trên CMS (WBS-144) — quy tắc thuần, cùng quy tắc với AggregationClusterService/AggregationRadarService (C#).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  DEFAULT_RADAR_WINDOW,
  DEFAULT_SCHEDULE_MINUTES,
  ago,
  allowedClusterActions,
  approvalError,
  clusterBadges,
  clustersQuery,
  feedUrlError,
  parseKeywords,
  percent,
  reasonError,
  safeExternalUrl,
  splitError,
} from '../src/lib/aggregation.ts';
import { SECTIONS, sectionFor } from '../src/lib/nav.ts';

test('quy tắc quét chốt 10/11/2026: feed mới 30 phút, Radar mở "Hôm nay"', () => {
  assert.equal(DEFAULT_SCHEDULE_MINUTES, 30);
  assert.equal(DEFAULT_RADAR_WINDOW, 'TODAY');
});

test('cụm: thao tác theo trạng thái khớp vòng đời của API', () => {
  assert.deepEqual(allowedClusterActions({ status: 'OPEN', assigneeId: null }), ['watch', 'dismiss', 'claim', 'coverage', 'split', 'merge']);
  assert.deepEqual(allowedClusterActions({ status: 'WATCHING', assigneeId: 'u2' }), ['reopen', 'dismiss', 'coverage', 'split', 'merge']);   // đã có người nhận
  assert.deepEqual(allowedClusterActions({ status: 'DISMISSED', assigneeId: null }), ['reopen', 'coverage', 'split', 'merge']);
  assert.deepEqual(allowedClusterActions({ status: 'COVERED', assigneeId: null }), ['split']);
  assert.deepEqual(allowedClusterActions({ status: 'CLOSED', assigneeId: null }), ['coverage']);
});

test('cụm: tách phải chọn ≥ 1 mục và chừa lại ≥ 1 mục', () => {
  assert.ok(splitError(0, 3));
  assert.ok(splitError(3, 3));
  assert.equal(splitError(1, 3), null);
});

test('cụm: nhãn thẻ (BA-01 mục 9) gồm "Chỉ M0/M1"', () => {
  assert.deepEqual(clusterBadges({ labels: ['SINGLE_SOURCE', 'SENSITIVE'], maxUsageMode: 'M1' }), ['Một nguồn', 'Nhạy cảm', 'Chỉ M0/M1']);
  assert.deepEqual(clusterBadges({ labels: [], maxUsageMode: 'M2' }), []);
  assert.deepEqual(clusterBadges({ labels: ['FUTURE_LABEL'], maxUsageMode: 'M3' }), ['FUTURE_LABEL']);   // giá trị lạ vẫn hiện
});

test('radar: query danh sách cụm theo hợp đồng', () => {
  const base = { siteKey: 'www', window: 'DAY' as const, date: null, categoryId: null, statuses: ['OPEN', 'WATCHING'], minScore: null };
  assert.equal(clustersQuery(base, 20), '/admin/aggregation/clusters?siteKey=www&window=DAY&limit=20&status=OPEN&status=WATCHING');
  const q = new URL(`http://x${clustersQuery({ ...base, date: '2026-11-10', categoryId: 'c1', minScore: 40 }, 10, 'abc')}`).searchParams;
  assert.equal(q.get('date'), '2026-11-10');
  assert.equal(q.get('minScore'), '40');
  assert.equal(q.get('cursor'), 'abc');
  assert.equal(new URL(`http://x${clustersQuery({ ...base, window: 'LAST_24H', date: '2026-11-10' }, 10)}`).searchParams.get('date'), null);
});

test('nhập liệu: lý do, URL feed, từ khóa, duyệt', () => {
  assert.ok(reasonError(' abc '));
  assert.equal(reasonError('Tin quảng cáo'), null);
  assert.ok(feedUrlError('http://news.example.org/rss'));
  assert.ok(feedUrlError('không phải url'));
  assert.equal(feedUrlError('https://news.example.org/rss'), null);
  assert.deepEqual(parseKeywords('iPhone, Galaxy\n iphone ,, ốp lưng').keywords, ['iPhone', 'Galaxy', 'ốp lưng']);
  assert.ok(parseKeywords('x'.repeat(101)).error);
  assert.ok(parseKeywords(Array.from({ length: 201 }, (_, i) => `k${i}`).join(',')).error);
  assert.ok(approvalError('M2', '', 'dms://t'));
  assert.equal(approvalError('M2', '00000000-0000-0000-0000-000000000001', 'dms://t'), null);
  assert.equal(approvalError('M1', '', 'dms://t'), null);
  assert.ok(approvalError('M0', '', ' '));
});

test('an toàn: chỉ mở link http(s) của nguồn', () => {
  assert.equal(safeExternalUrl('javascript:alert(1)'), null);
  assert.equal(safeExternalUrl('data:text/html,x'), null);
  assert.equal(safeExternalUrl('https://news.example.org/a'), 'https://news.example.org/a');
});

test('hiển thị: thời gian tương đối, phần trăm', () => {
  const now = new Date('2026-11-10T10:00:00Z');
  assert.equal(ago('2026-11-10T09:58:00Z', now), '2 phút trước');
  assert.equal(ago('2026-11-10T07:00:00Z', now), '3 giờ trước');
  assert.equal(ago(null, now), '—');
  assert.equal(percent(0.954), '95%');
  assert.equal(percent(null), '—');
});

test('nav: Radar tin và Nguồn thu thập dùng quyền aggregation.read', () => {
  assert.equal(SECTIONS.find((s) => s.href === '/radar')?.permission, 'aggregation.read');
  assert.equal(sectionFor('/radar/123')?.href, '/radar');
  assert.equal(sectionFor('/aggregation/sources/1')?.href, '/aggregation');
  assert.equal(sectionFor('/aggregation/rules')?.href, '/aggregation');
});

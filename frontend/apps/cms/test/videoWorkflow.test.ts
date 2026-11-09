// Duyệt / xuất bản / gỡ video trong CMS (WBS-040) — cùng quy tắc với VideoService.Workflow (C#).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ApiError } from '../src/lib/api.ts';
import {
  canSubmitVideo, isApproved, isInReview, isPendingDecision, publishBlockers, reasonError, revisionStatusLabel, videoWorkflowErrorText,
} from '../src/lib/videos.ts';

const base = { status: 'DRAFT', revisionStatus: 'DRAFT', workingRevisionId: 'r2', publicRevisionId: null as string | null, mediaId: 'm1' };

test('video: lý do trả/từ chối/gỡ 5–2000 ký tự (REASON_REQUIRED)', () => {
  assert.ok(reasonError(''));
  assert.ok(reasonError('  ok  '));
  assert.equal(reasonError('Khiếu nại bản quyền'), null);
  assert.ok(reasonError('x'.repeat(2001)));
});

test('video: gửi duyệt khi bản nháp chưa công khai, có tệp, video sửa được', () => {
  assert.equal(canSubmitVideo(base), true);
  assert.equal(canSubmitVideo({ ...base, mediaId: null }), false);                                        // chưa gắn tệp
  assert.equal(canSubmitVideo({ ...base, status: 'PUBLISHED', publicRevisionId: 'r1' }), true);           // bản sửa của video đang công khai
  assert.equal(canSubmitVideo({ ...base, status: 'PUBLISHED', publicRevisionId: 'r2' }), false);          // chính là bản công khai
  assert.equal(canSubmitVideo({ ...base, status: 'UNPUBLISHED' }), true);                                 // đã gỡ, sửa rồi gửi lại
  assert.equal(canSubmitVideo({ ...base, status: 'IN_REVIEW', revisionStatus: 'IN_REVIEW' }), false);
  assert.equal(canSubmitVideo({ ...base, status: 'CHANGES_REQUESTED', revisionStatus: 'CHANGES_REQUESTED' }), false);   // phải sửa trước
});

test('video: duyệt khi working revision chờ duyệt; xuất bản khi đã duyệt; khóa sửa khi chờ quyết định', () => {
  const review = { ...base, status: 'IN_REVIEW', revisionStatus: 'IN_REVIEW' };
  assert.equal(isInReview(review), true);
  assert.equal(isInReview({ ...review, status: 'UNPUBLISHED' }), true);   // gỡ trong lúc bản mới chờ duyệt: vẫn duyệt tiếp được
  assert.equal(isApproved(review), false);
  assert.equal(isApproved({ ...base, status: 'APPROVED', revisionStatus: 'APPROVED' }), true);
  assert.equal(isPendingDecision(review), true);
  assert.equal(isPendingDecision({ ...base, revisionStatus: 'PUBLISHED', publicRevisionId: 'r2' }), false);
  assert.equal(revisionStatusLabel(null), '—');
});

test('video: chốt chặn xuất bản — tệp READY, quyền tệp + video CLEARED còn hạn', () => {
  const now = new Date('2026-11-09T00:00:00Z');
  const video = { mediaId: 'm1', rightsStatus: 'CLEARED', rightsExpiresAt: null };
  const media = { mediaId: 'm1', status: 'READY', mediaType: 'VIDEO', rightsStatus: 'CLEARED', rightsExpiresAt: null };
  assert.deepEqual(publishBlockers(video, media, now), []);
  assert.equal(publishBlockers(video, { ...media, status: 'PROCESSING' }, now).length, 1);
  assert.equal(publishBlockers(video, { ...media, rightsStatus: 'UNVERIFIED' }, now).length, 1);
  assert.equal(publishBlockers({ ...video, rightsExpiresAt: '2026-11-08T00:00:00Z' }, media, now).length, 1);
  assert.equal(publishBlockers({ ...video, mediaId: null }, null, now).length, 1);
  assert.deepEqual(publishBlockers(video, { ...media, mediaId: 'khac' }, now), []);   // trạng thái tệp cũ chưa cập nhật: không đoán
});

test('video: thông điệp lỗi workflow', () => {
  const err = (code: string, status = 422) => new ApiError({ type: 'about:blank', title: code, status, code });
  assert.match(videoWorkflowErrorText(err('SOD_VIOLATION', 403)), /tự duyệt/);
  assert.match(videoWorkflowErrorText(err('MEDIA_NOT_READY')), /chưa xử lý xong/);
  assert.match(videoWorkflowErrorText(err('RIGHTS_INVALID')), /quyền/);
  assert.match(videoWorkflowErrorText(err('FEATURE_DISABLED', 403)), /tài trợ/);
});

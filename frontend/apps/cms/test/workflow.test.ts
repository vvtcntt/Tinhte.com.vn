// Quy trình duyệt bài trong CMS (WBS-024) — cùng quy tắc với ArticleService.Workflow (C#).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { canSubmit, isPendingReview, reasonError } from '../src/lib/articles.ts';

test('duyệt: lý do trả bài/từ chối 5–2000 ký tự (REASON_REQUIRED)', () => {
  assert.ok(reasonError(''));
  assert.ok(reasonError('  ok  '));
  assert.equal(reasonError('Thiếu nguồn ảnh'), null);
  assert.ok(reasonError('x'.repeat(2001)));
});

test('duyệt: chỉ gửi duyệt bản nháp chưa công khai, bài ở trạng thái sửa được', () => {
  const draft = { revisionId: 'r2', status: 'DRAFT' };
  assert.equal(canSubmit({ status: 'DRAFT', publicRevisionId: null }, draft), true);
  assert.equal(canSubmit({ status: 'PUBLISHED', publicRevisionId: 'r1' }, draft), true);     // bản sửa của bài đang công khai
  assert.equal(canSubmit({ status: 'PUBLISHED', publicRevisionId: 'r2' }, draft), false);    // chính là bản công khai
  assert.equal(canSubmit({ status: 'IN_REVIEW', publicRevisionId: null }, draft), false);
  assert.equal(canSubmit({ status: 'CHANGES_REQUESTED', publicRevisionId: null }, { revisionId: 'r2', status: 'CHANGES_REQUESTED' }), false); // phải sửa trước
  assert.equal(canSubmit({ status: 'DRAFT', publicRevisionId: null }, null), false);
});

test('duyệt: chỉ duyệt working revision đang IN_REVIEW', () => {
  const article = { status: 'IN_REVIEW', workingRevisionId: 'r2' };
  assert.equal(isPendingReview(article, { revisionId: 'r2', status: 'IN_REVIEW' }), true);
  assert.equal(isPendingReview(article, { revisionId: 'r1', status: 'IN_REVIEW' }), false);
  assert.equal(isPendingReview({ ...article, status: 'APPROVED' }, { revisionId: 'r2', status: 'APPROVED' }), false);
});

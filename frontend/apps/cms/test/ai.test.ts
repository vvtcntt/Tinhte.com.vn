// Kiểm thư viện nút "Gợi ý bằng AI" (FR-AI-001; quyền ĐỀ XUẤT ai.assist, cờ ai.assistance — DRAFT v0.3 §21).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ApiError, type ApiRequest, type ApiResponse } from '../src/lib/api.ts';
import { AI_SUGGEST_PATH, aiErrorText, normalizeSuggestions, requestSuggestions, tagsToText } from '../src/lib/ai.ts';
import { access } from '../src/lib/rbac.ts';

test('gọi đúng API với body theo hợp đồng và khóa idempotency mới mỗi lần', async () => {
  const calls: { path: string; request?: ApiRequest }[] = [];
  let n = 0;
  const fetcher = async <T>(path: string, request?: ApiRequest): Promise<ApiResponse<T>> => {
    calls.push({ path, request });
    return { status: 201, etag: null, data: { suggestions: ['Apple', ' iPhone  17 ', 'apple', 42, ''], requiresHumanApproval: true } as T };
  };
  const result = await requestSuggestions('c1', 'TAG', fetcher, () => `key-${++n}`);
  assert.deepEqual(result, ['Apple', 'iPhone 17']);
  await requestSuggestions('c1', 'SUMMARY', fetcher, () => `key-${++n}`);
  assert.equal(calls[0].path, AI_SUGGEST_PATH);
  assert.deepEqual(calls[0].request, { method: 'POST', body: { contentId: 'c1', task: 'TAG' }, idempotencyKey: 'key-1' });
  assert.equal(calls[1].request?.idempotencyKey, 'key-2');
  assert.deepEqual(calls[1].request?.body, { contentId: 'c1', task: 'SUMMARY' });
});

test('dữ liệu lạ từ API không làm vỡ màn hình', () => {
  assert.deepEqual(normalizeSuggestions(null), []);
  assert.deepEqual(normalizeSuggestions({ suggestions: 'x' }), []);
  assert.deepEqual(normalizeSuggestions({}), []);
});

test('thông báo lỗi theo mã của hợp đồng', () => {
  const err = (status: number, code: string, detail?: string) => new ApiError({ title: code, status, code, detail });
  assert.match(aiErrorText(err(403, 'FEATURE_DISABLED')), /đang tắt/);
  assert.match(aiErrorText(err(429, 'RATE_LIMITED')), /hết lượt/);
  assert.match(aiErrorText(err(503, 'DEPENDENCY_UNAVAILABLE')), /không phản hồi/);
  assert.match(aiErrorText(err(422, 'VALIDATION_FAILED')), /chưa có nội dung/);
  assert.match(aiErrorText(err(404, 'NOT_FOUND')), /Không tìm thấy/);
  assert.equal(aiErrorText(err(409, 'STATE_CONFLICT', 'Request in progress')), 'Request in progress');
  assert.match(aiErrorText(new TypeError('fetch failed')), /Không kết nối/);
});

test('tag sao chép phân cách bằng dấu phẩy', () => {
  assert.equal(tagsToText(['Apple', 'iPhone 17']), 'Apple, iPhone 17');
});

test('nút chỉ hiện cho vai trò có ai.assist (ma trận ĐỀ XUẤT trong rbac.json)', () => {
  assert.equal(access({ roles: ['AUTHOR'], mfaSatisfied: false }, 'ai.assist'), 'allowed');
  assert.equal(access({ roles: ['EDITOR'], mfaSatisfied: false }, 'ai.assist'), 'allowed');
  for (const role of ['APPROVER', 'MODERATOR', 'ADMIN', 'AD_OPS']) {
    assert.equal(access({ roles: [role], mfaSatisfied: true }, 'ai.assist'), 'denied', role);
  }
});

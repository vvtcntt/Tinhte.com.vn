// Kiểm thư viện màn Video + thư viện media (WBS-046).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { ApiError } from '../src/lib/api.ts';
import {
  Sha256, sha256Blob, canAttachToVideo, draftFromVideo, guessContentType, isSameOrigin, mediaListQuery, mediaTone, pollDelayMs,
  titleFromFileName, videoErrorText, mediaErrorLabel, videoListQuery, videoPatchBody, formatBytes, isMediaInFlight, isMediaFailed,
  type AdminVideo, type MediaStatus,
} from '../src/lib/videos.ts';

const sha = (b: Uint8Array) => createHash('sha256').update(b).digest('hex');

test('sha256: khớp node:crypto với mọi độ dài biên khối và khi băm theo khúc', () => {
  for (const n of [0, 1, 55, 56, 63, 64, 65, 119, 120, 127, 128, 1000, 70_001]) {
    const data = randomBytes(n);
    assert.equal(new Sha256().update(data).digestHex(), sha(data), `len ${n}`);
    // Băm theo khúc lẻ (3, 61, 64…) phải ra cùng kết quả.
    const h = new Sha256();
    for (let i = 0, step = 3; i < n; i += step, step = (step * 7) % 97 + 1) h.update(data.subarray(i, Math.min(n, i + step)));
    assert.equal(h.digestHex(), sha(data), `chunked ${n}`);
  }
  assert.equal(new Sha256().update(new TextEncoder().encode('abc')).digestHex(), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
});

test('sha256Blob: băm Blob theo khúc 4 MB, báo tiến độ tới 1', async () => {
  const data = randomBytes(9 * 1024 * 1024 + 17);
  const progress: number[] = [];
  assert.equal(await sha256Blob(new Blob([data]), (p) => progress.push(p)), sha(data));
  assert.equal(progress.at(-1), 1);
  assert.ok(progress.every((p, i) => i === 0 || p >= progress[i - 1]));
  const ctrl = new AbortController();
  ctrl.abort();
  await assert.rejects(sha256Blob(new Blob([data]), undefined, ctrl.signal), /Abort/);
});

test('tệp: đoán MIME theo trình duyệt rồi theo đuôi, gợi ý tiêu đề, định dạng dung lượng', () => {
  assert.equal(guessContentType('a.mp4', 'video/mp4'), 'video/mp4');
  assert.equal(guessContentType('phu-de.vtt', ''), 'text/vtt');                 // .vtt thường không có type
  assert.equal(guessContentType('clip.MOV', 'application/octet-stream'), 'video/quicktime');
  assert.equal(guessContentType('setup.exe', 'application/x-msdownload'), null);
  assert.equal(titleFromFileName('Mo_hop_Galaxy.S26.mp4'), 'Mo hop Galaxy.S26');
  assert.equal(formatBytes(null), '—');
  assert.equal(formatBytes(512), '512 B');
  assert.equal(formatBytes(1536), '1.5 KB');
  assert.equal(formatBytes(250 * 1024 * 1024), '250 MB');
});

test('upload: chỉ gửi cookie/CSRF tới URL cùng origin CMS', () => {
  const origin = 'http://cms.tinhte.localhost:3001';
  assert.equal(isSameOrigin('/api/v1/admin/media/1/dev-upload?x=1', origin), true);
  assert.equal(isSameOrigin('http://cms.tinhte.localhost:3001/api/v1/admin/media/1/dev-upload', origin), true);
  assert.equal(isSameOrigin('https://bucket.storage.example/obj?sig=1', origin), false);
  assert.equal(isSameOrigin('http://cms.tinhte.localhost:5080/x', origin), false);   // khác cổng = khác origin
  assert.equal(isSameOrigin('::not a url', 'nope'), false);
});

const media = (over: Partial<MediaStatus> = {}): MediaStatus => ({
  mediaId: 'm1', status: 'READY', generation: 1, errorCode: null, mediaType: 'VIDEO', visibility: 'PRIVATE', rightsStatus: 'UNVERIFIED',
  licenseId: null, rightsExpiresAt: null, createdAt: '2026-11-09T00:00:00Z', ...over,
});

test('media: trạng thái pipeline, sắc thái badge, điều kiện gắn vào video khớp API', () => {
  assert.equal(isMediaInFlight('PROCESSING'), true);
  assert.equal(isMediaInFlight('READY'), false);
  assert.equal(isMediaFailed('REJECTED'), true);
  assert.equal(mediaTone('READY'), 'ok');
  assert.equal(mediaTone('VALIDATING'), 'warn');
  assert.equal(mediaTone('QUARANTINED'), 'error');
  assert.equal(mediaTone('UPLOADING'), 'neutral');

  const now = new Date('2026-11-09T12:00:00Z');
  assert.equal(canAttachToVideo(media(), now), true);
  assert.equal(canAttachToVideo(media({ status: 'UPLOADING' }), now), true);          // đang tải vẫn gắn được vào bản nháp
  assert.equal(canAttachToVideo(media({ mediaType: 'IMAGE' }), now), false);
  assert.equal(canAttachToVideo(media({ status: 'REJECTED' }), now), false);
  assert.equal(canAttachToVideo(media({ rightsStatus: 'REVOKED' }), now), false);
  assert.equal(canAttachToVideo(media({ rightsStatus: 'CLEARED', rightsExpiresAt: '2026-11-01T00:00:00Z' }), now), false);
  assert.equal(canAttachToVideo(media({ rightsStatus: 'CLEARED', rightsExpiresAt: '2027-01-01T00:00:00Z' }), now), true);
});

test('polling: nhanh lúc đầu, giãn dần, tối đa 10 giây', () => {
  assert.equal(pollDelayMs(0), 1000);
  const delays = Array.from({ length: 20 }, (_, i) => pollDelayMs(i));
  assert.ok(delays.every((d, i) => i === 0 || d >= delays[i - 1]));
  assert.equal(Math.max(...delays), 10000);
});

test('truy vấn danh sách: chỉ gửi bộ lọc đang dùng, theo tên tham số của hợp đồng', () => {
  assert.equal(videoListQuery({ status: '', q: ' ', mine: false, sort: 'updatedAt_desc' }, 'u1', null), '/admin/videos?limit=20');
  assert.equal(videoListQuery({ status: 'DRAFT', q: 'galaxy', mine: true, sort: 'title_asc' }, 'u1', 'c.1'),
    '/admin/videos?limit=20&status=DRAFT&q=galaxy&ownerId=u1&sort=title_asc&cursor=c.1');
  assert.equal(mediaListQuery({ mediaType: 'VIDEO', rightsStatus: '', visibility: 'PRIVATE', q: 'clip' }, null, 10),
    '/admin/media?limit=10&mediaType=VIDEO&visibility=PRIVATE&q=clip');
});

const video: AdminVideo = {
  id: 'v1', title: 'Mở hộp', status: 'DRAFT', contentKind: 'EDITORIAL', sponsorship: null, workingRevisionId: 'r1', publicRevisionId: null,
  ownerId: 'u1', assigneeId: null, rightsStatus: 'UNVERIFIED', rightsExpiresAt: null, scheduledAt: null, updatedAt: '2026-11-09T00:00:00Z',
  rowVersion: 3, mediaId: null, summary: 'Sapo',
};

test('PATCH video: chỉ gửi trường đã đổi; sapo rỗng = null; tài trợ gửi đủ sponsorship', () => {
  const draft = draftFromVideo(video);
  assert.equal(videoPatchBody(video, draft), null);
  assert.equal(videoPatchBody(video, { ...draft, changeReason: 'chỉ có lý do' }), null);   // lý do thôi thì không lưu
  assert.deepEqual(videoPatchBody(video, { ...draft, title: ' Mở hộp S26 ', changeReason: 'Đổi tên' }), { title: 'Mở hộp S26', changeReason: 'Đổi tên' });
  assert.deepEqual(videoPatchBody(video, { ...draft, summary: '  ' }), { summary: null });
  const sponsored = videoPatchBody(video, {
    ...draft, contentKind: 'SPONSORED',
    sponsorship: { sponsorName: 'ACME', sponsorReference: 'HD-01', evidenceRef: 'dms://1', validFrom: '2026-11-10T08:00', validTo: '2026-12-10T08:00' },
  });
  assert.equal(sponsored?.contentKind, 'SPONSORED');
  assert.equal((sponsored?.sponsorship as { sponsorName: string }).sponsorName, 'ACME');
  assert.match(String((sponsored?.sponsorship as { validFrom: string }).validFrom), /^2026-11-10T\d\d:00:00\.000Z$/);
});

test('lỗi: thông điệp theo mã của catalog, kèm lỗi từng trường', () => {
  const err = (code: string, extra: object = {}) => new ApiError({ title: 't', status: 400, code, ...extra });
  assert.match(videoErrorText(err('REVISION_CONFLICT')), /người khác sửa/);
  assert.match(videoErrorText(err('MEDIA_NOT_READY')), /chưa nhận được tệp/);
  assert.match(videoErrorText(err('VALIDATION_FAILED', { detail: 'Invalid', errors: { contentType: ['Unsupported'] } })), /Loại tệp: Unsupported/);
  assert.match(videoErrorText(new TypeError('fetch failed')), /Không kết nối được API/);
  assert.match(videoErrorText(err('RATE_LIMITED'), 125), /3 phút/);
  assert.match(videoErrorText(err('RATE_LIMITED'), 20), /20 giây/);
  assert.match(videoErrorText(err('RATE_LIMITED')), /ít phút/);
  assert.match(mediaErrorLabel('UPLOAD_EXPIRED') ?? '', /hết hạn/);
  assert.match(mediaErrorLabel('SOMETHING_NEW') ?? '', /SOMETHING_NEW/);   // mã lạ của vendor → thông điệp chung
});

test('videoPatchBody: thân video (bodyBlocks) chỉ gửi khi đổi; API cũ chưa trả bodyBlocks → coi là rỗng', () => {
  const video = { id: 'v', title: 'T', summary: null, contentKind: 'EDITORIAL', sponsorship: null } as unknown as Parameters<typeof draftFromVideo>[0];
  const draft = draftFromVideo(video);
  assert.equal(draft.body, '[]');
  assert.equal(videoPatchBody(video, draft), null);
  const blocks = [{ type: 'paragraph', text: 'Mô tả' }];
  assert.deepEqual(videoPatchBody(video, { ...draft, body: JSON.stringify(blocks) }), { bodyBlocks: blocks });
});

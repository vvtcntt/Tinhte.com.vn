// Kiểm thư viện màn Quảng cáo (P0-ADS, FR-ADS-002, WBS-136; DRAFT v0.3 mục 16/19).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ApiError } from '../src/lib/api.ts';
import {
  AD_STATUSES, adErrorText, bookingBody, bookingDraftErrors, bookingStatusBody, canTransition, draftFromBooking, emptyBookingDraft, fieldLabel,
  isHttpsUrl, isLiveNow, needsApproval, parseSize, placementDraftErrors, placementUpdateBody, placementsQuery, sizesInUse, sizesSummary, sortSizes,
  statusActions, transitionPermission, type AdBooking, type AdPlacement, type AdStatus,
} from '../src/lib/ads.ts';
import { access } from '../src/lib/rbac.ts';

const placement: AdPlacement = {
  id: 'p1',
  siteKey: 'www',
  code: 'home_top',
  label: 'Quảng cáo',
  status: 'ACTIVE',
  sizes: [
    { breakpoint: 'DESKTOP', width: 970, height: 250 },
    { breakpoint: 'MOBILE', width: 320, height: 100 },
    { breakpoint: 'DESKTOP', width: 970, height: 90 },
  ],
  rowVersion: 3,
};

const booking: AdBooking = {
  id: 'b1',
  placementId: 'p1',
  status: 'ACTIVE',
  priority: 20,
  advertiserName: 'ACME',
  targetUrl: 'https://acme.vn/khuyen-mai',
  startsAt: null,
  endsAt: null,
  creatives: [
    { breakpoint: 'MOBILE', width: 320, height: 100, mediaAssetId: 'm1', altText: 'Banner mobile', creativeUrl: 'https://media.example/ads/m1.png' },
    { breakpoint: 'DESKTOP', width: 970, height: 250, mediaAssetId: 'm2', altText: 'Banner desktop', creativeUrl: null },
  ],
  approvedBy: 'u1',
  approvedAt: '2026-11-10T10:00:00Z',
  rowVersion: 5,
};

test('vòng đời: cùng bảng chuyển trạng thái với backend (AdStatuses)', () => {
  const allowed = new Set(['DRAFT>APPROVED', 'APPROVED>ACTIVE', 'ACTIVE>PAUSED', 'PAUSED>ACTIVE', 'APPROVED>DRAFT', 'PAUSED>DRAFT']);
  for (const from of AD_STATUSES) {
    for (const to of AD_STATUSES) {
      const expected = from === to || to === 'EXPIRED' || allowed.has(`${from}>${to}`);
      assert.equal(canTransition(from, to), expected, `${from} → ${to}`);
    }
  }
  assert.equal(needsApproval('DRAFT', 'APPROVED'), true);
  assert.equal(needsApproval('PAUSED', 'ACTIVE'), true);
  assert.equal(needsApproval('ACTIVE', 'ACTIVE'), false);
  assert.equal(transitionPermission('ACTIVE', 'PAUSED'), 'ads.manage');
  assert.equal(transitionPermission('APPROVED', 'ACTIVE'), 'ads.approve');
});

test('nút trạng thái chỉ gồm bước hợp lệ; Kết thúc luôn cuối, không có ở EXPIRED', () => {
  for (const s of AD_STATUSES) {
    const actions = statusActions(s);
    assert.ok(actions.every((a) => canTransition(s, a.to) && a.to !== s), s);
    if (s === 'EXPIRED') assert.deepEqual(actions, []);
    else assert.equal(actions.at(-1)?.to, 'EXPIRED');
  }
  assert.deepEqual(statusActions('APPROVED').map((a) => a.to), ['ACTIVE', 'DRAFT', 'EXPIRED']);
});

test('ma trận DRAFT: AD_OPS chuẩn bị nhưng không mở; APPROVER mở được khi đã MFA', () => {
  const ops = { roles: ['AD_OPS'], mfaSatisfied: true };
  assert.equal(access(ops, 'ads.manage'), 'allowed');
  assert.equal(access(ops, 'ads.approve'), 'denied');
  assert.equal(access(ops, 'content.read'), 'allowed');   // mở thư viện media để chọn banner
  assert.equal(access({ roles: ['APPROVER'], mfaSatisfied: false }, 'ads.approve'), 'mfa-required');
  assert.equal(access({ roles: ['APPROVER'], mfaSatisfied: true }, 'ads.approve'), 'allowed');
  assert.equal(access({ roles: ['EDITOR'], mfaSatisfied: true }, 'ads.manage'), 'denied');
});

test('kích thước: đọc "970x250", sắp theo màn hình, tóm tắt, kích thước đang dùng', () => {
  assert.deepEqual(parseSize(' 970 × 250 '), { width: 970, height: 250 });
  assert.deepEqual(parseSize('320*100'), { width: 320, height: 100 });
  assert.equal(parseSize('0x90'), null);
  assert.equal(parseSize('5000x90'), null);
  assert.equal(parseSize('970'), null);
  assert.deepEqual(sortSizes(placement.sizes).map((s) => `${s.breakpoint}:${s.width}x${s.height}`), ['MOBILE:320x100', 'DESKTOP:970x90', 'DESKTOP:970x250']);
  assert.equal(sizesSummary(placement.sizes), 'Di động 320×100 · Máy tính 970×90, 970×250');
  assert.equal(sizesSummary([]), 'Chưa có kích thước');
  assert.deepEqual([...sizesInUse([booking])], ['MOBILE:320x100', 'DESKTOP:970x250']);
});

test('form vị trí: mã theo mẫu, nhãn, kích thước trùng; thân PUT sắp kích thước', () => {
  assert.deepEqual(placementDraftErrors({ code: 'home_top', label: 'Quảng cáo', sizes: placement.sizes }, true), {});
  const bad = placementDraftErrors({ code: 'Home Top', label: '  ', sizes: [placement.sizes[0], placement.sizes[0]] }, true);
  assert.deepEqual(Object.keys(bad).sort(), ['code', 'label', 'sizes']);
  assert.ok(placementDraftErrors({ code: 'ab', label: 'x', sizes: [] }, true).sizes);
  assert.equal(placementDraftErrors({ code: 'ab', label: 'x', sizes: [] }, false).sizes, undefined);   // PUT cho phép rỗng
  const body = placementUpdateBody({ code: 'home_top', label: ' Tài trợ ', sizes: placement.sizes }, 'PAUSED');
  assert.equal(body.label, 'Tài trợ');
  assert.equal(body.status, 'PAUSED');
  assert.equal((body.sizes as { breakpoint: string }[])[0].breakpoint, 'MOBILE');
  assert.equal(placementsQuery('www'), '/admin/ad-placements?siteKey=www');
  assert.equal(placementsQuery(''), '/admin/ad-placements');
});

test('link đích: chỉ https tuyệt đối (khớp ^https:// và CHECK DB)', () => {
  assert.equal(isHttpsUrl('https://acme.vn/a?b=1'), true);
  for (const bad of ['http://acme.vn', 'javascript:alert(1)', 'https://', 'https://a b.vn', 'HTTPS://acme.vn', 'acme.vn']) {
    assert.equal(isHttpsUrl(bad), false, bad);
  }
});

test('form đặt chỗ: rỗng → cần banner; đủ → thân đúng hợp đồng; duyệt/chạy cần link', () => {
  const empty = emptyBookingDraft(placement);
  assert.equal(empty.creatives.MOBILE.size, 'MOBILE:320x100');
  assert.equal(empty.creatives.TABLET.size, '');                 // vị trí không có kích thước TABLET
  assert.equal(empty.creatives.DESKTOP.size, 'DESKTOP:970x90');
  assert.ok(bookingDraftErrors(empty, placement).creatives);

  const draft = {
    ...empty,
    priority: '15',
    targetUrl: ' https://acme.vn ',
    creatives: { ...empty.creatives, DESKTOP: { enabled: true, size: 'DESKTOP:970x250', mediaAssetId: 'm2', altText: ' ACME ' } },
  };
  assert.deepEqual(bookingDraftErrors(draft, placement), {});
  assert.deepEqual(bookingBody(draft), {
    priority: 15,
    advertiserName: null,
    targetUrl: 'https://acme.vn',
    startsAt: null,
    endsAt: null,
    creatives: [{ breakpoint: 'DESKTOP', width: 970, height: 250, mediaAssetId: 'm2', altText: 'ACME' }],
  });

  const errors = bookingDraftErrors({
    ...draft,
    priority: '101',
    targetUrl: '',
    startsAt: '2026-11-12T10:00',
    endsAt: '2026-11-11T10:00',
    creatives: { ...draft.creatives, TABLET: { enabled: true, size: '', mediaAssetId: '', altText: '' } },
  }, placement, 'APPROVED');
  assert.deepEqual(Object.keys(errors).sort(), ['creatives.TABLET.altText', 'creatives.TABLET.media', 'creatives.TABLET.size', 'endsAt', 'priority', 'targetUrl']);
});

test('đặt chỗ đã lưu: form dựng lại đúng; đổi trạng thái gửi lại nội dung đã lưu (bỏ creativeUrl)', () => {
  const draft = draftFromBooking(booking, placement);
  assert.equal(draft.priority, '20');
  assert.equal(draft.creatives.MOBILE.enabled, true);
  assert.equal(draft.creatives.TABLET.enabled, false);
  const body = bookingStatusBody(booking, 'PAUSED');
  assert.equal(body.status, 'PAUSED');
  assert.equal(body.priority, 20);
  assert.deepEqual((body.creatives as Record<string, unknown>[]).map((c) => Object.keys(c).sort().join(',')),
    ['altText,breakpoint,height,mediaAssetId,width', 'altText,breakpoint,height,mediaAssetId,width']);
  assert.equal(bookingStatusBody(booking, 'ACTIVE', 30).priority, 30);
});

test('đang hiện: vị trí + đặt chỗ ACTIVE, trong lịch, có ít nhất một ảnh phục vụ được', () => {
  const now = new Date('2026-11-10T12:00:00Z');
  assert.equal(isLiveNow(placement, booking, now), true);
  assert.equal(isLiveNow({ status: 'PAUSED' as AdStatus }, booking, now), false);
  assert.equal(isLiveNow(placement, { ...booking, startsAt: '2026-11-11T00:00:00Z' }, now), false);
  assert.equal(isLiveNow(placement, { ...booking, endsAt: '2026-11-10T11:00:00Z' }, now), false);
  assert.equal(isLiveNow(placement, { ...booking, creatives: [booking.creatives[1]] }, now), false);
});

test('lỗi API → thông điệp tiếng Việt; trường lồng của banner có nhãn', () => {
  const err = (code: string, extra: Record<string, unknown> = {}) => new ApiError({ title: 'x', status: 422, code, ...extra });
  assert.match(adErrorText(err('REVISION_CONFLICT')), /người khác sửa/);
  assert.match(adErrorText(err('UNSAFE_URL')), /https/);
  assert.match(adErrorText(err('FORBIDDEN', { title: 'MFA required' })), /MFA/);
  assert.match(adErrorText(err('STATE_CONFLICT', { detail: 'Cannot go from APPROVED to PAUSED.' })), /APPROVED to PAUSED/);
  assert.match(adErrorText(err('VALIDATION_FAILED', { detail: 'Validation failed', errors: { 'creatives[1].mediaAssetId': ['must be IMAGE'] } })),
    /Banner 2 \(mediaAssetId\): must be IMAGE/);
  assert.equal(fieldLabel('targetUrl'), 'Link đích');
  assert.equal(fieldLabel('TargetUrl'), 'Link đích');
  assert.match(adErrorText(new TypeError('fetch failed')), /Không kết nối/);
});

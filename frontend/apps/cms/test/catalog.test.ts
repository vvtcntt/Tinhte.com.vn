// Danh mục nguồn + giấy phép (ADR-0007 §1) — quy tắc thuần, cùng quy tắc với CatalogService (C#).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { dateInputToIso, isoToDateInput, licenseCodeError, licenseError, licenseStatus, sourceError } from '../src/lib/catalog.ts';
import { SECTIONS, sectionFor } from '../src/lib/nav.ts';

test('nguồn: tên 1–200 ký tự, URL http(s) tùy chọn', () => {
  assert.ok(sourceError(' ', ''));
  assert.ok(sourceError('Báo', 'ftp://x'));
  assert.equal(sourceError('Báo Mẫu', ''), null);
  assert.equal(sourceError('Báo Mẫu', 'https://bao.example.org'), null);
});

test('giấy phép: mã chữ HOA, bằng chứng bắt buộc, hết hạn sau bắt đầu', () => {
  assert.equal(licenseCodeError('AFP-2026'), null);
  assert.ok(licenseCodeError('afp'));
  assert.ok(licenseCodeError('-X'));
  const ok = { name: 'Thỏa thuận', usageScope: 'Dùng dữ kiện', evidenceRef: 'dms://hd', validFrom: '2026-01-01', validTo: '2027-01-01' };
  assert.equal(licenseError(ok), null);
  assert.ok(licenseError({ ...ok, evidenceRef: ' ' }));
  assert.ok(licenseError({ ...ok, validTo: '2025-12-31' }));
});

test('giấy phép: trạng thái hiệu lực', () => {
  const now = new Date('2026-11-10T00:00:00Z');
  assert.equal(licenseStatus({ isEnabled: false, active: false, validFrom: null, validTo: null }, now), 'Đã tắt');
  assert.equal(licenseStatus({ isEnabled: true, active: true, validFrom: null, validTo: null }, now), 'Đang hiệu lực');
  assert.equal(licenseStatus({ isEnabled: true, active: false, validFrom: '2027-01-01T00:00:00Z', validTo: null }, now), 'Chưa tới hạn');
  assert.equal(licenseStatus({ isEnabled: true, active: false, validFrom: null, validTo: '2026-01-01T00:00:00Z' }, now), 'Hết hạn');
});

test('ngày: ô date ↔ RFC 3339', () => {
  assert.equal(dateInputToIso(''), null);
  assert.equal(isoToDateInput(dateInputToIso('2026-11-10')), '2026-11-10');
  assert.equal(isoToDateInput(null), '');
});

test('nav: Nguồn & giấy phép theo content.read', () => {
  assert.equal(SECTIONS.find((s) => s.href === '/catalog')?.permission, 'content.read');
  assert.equal(sectionFor('/catalog/licenses')?.href, '/catalog');
});

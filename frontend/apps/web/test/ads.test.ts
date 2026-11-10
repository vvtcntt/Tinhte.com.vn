// Quảng cáo nhiều kích thước theo breakpoint (OpenAPI v0.3 DRAFT đợt 3 — PublicAdSlot.creatives[]). node:test, chạy TS trực tiếp.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { acceptAdSlots, AD_BREAKPOINTS, AD_RANGE, coveredBreakpoints, resolveAdZone } from '../src/lib/ads.ts';
import type { AdBreakpoint, PublicAdCreative, PublicAdSlot } from '../src/lib/contracts.ts';

const creative = (breakpoint: AdBreakpoint, width = 300, height = 250, extra: Partial<PublicAdCreative> = {}): PublicAdCreative => ({
  breakpoint, width, height, creativeUrl: `https://cdn.example.test/${width}x${height}.png`, altText: 'ACME', targetUrl: 'https://acme.example/', ...extra,
});
const slot = (code: string, creatives: PublicAdCreative[], label = 'Quảng cáo'): PublicAdSlot => ({ code, label, creatives, rel: 'sponsored noopener' });

test('acceptAdSlots: lọc từng banner — breakpoint lạ, trùng breakpoint, URL/target/kích thước hỏng; sắp MOBILE → DESKTOP', () => {
  const ads = acceptAdSlots(
    [
      slot('a', [
        creative('DESKTOP', 970, 90),
        creative('WATCH' as AdBreakpoint),
        creative('DESKTOP', 970, 250),                                   // trùng breakpoint → bỏ, giữ banner đầu
        creative('TABLET', 728, 90, { creativeUrl: 'data:image/png;base64,AAAA' }),
        creative('MOBILE', 320, 100, { targetUrl: null }),
      ]),
      slot('b', [creative('MOBILE', Number.NaN, 100), creative('TABLET', 728, 90, { targetUrl: 'javascript:alert(1)' })]),
      { code: 'c', label: 'Quảng cáo', rel: 'sponsored noopener' } as unknown as PublicAdSlot,  // thiếu creatives → bỏ
    ],
    ['a', 'b', 'c'],
  );
  assert.deepEqual(Object.keys(ads), ['a']);
  assert.deepEqual(ads.a?.creatives.map((c) => `${c.breakpoint}:${c.width}x${c.height}`), ['MOBILE:320x100', 'DESKTOP:970x90']);
});

test('resolveAdZone: TABLET ↔ DESKTOP mượn banner của nhau trong cùng mã; MOBILE không mượn, không cho mượn', () => {
  const desktopOnly = acceptAdSlots([slot('top', [creative('DESKTOP', 970, 90)])], ['top']);
  const views = resolveAdZone(desktopOnly, [{ code: 'top', breakpoints: AD_BREAKPOINTS }]);
  assert.deepEqual(views.map((v) => v.breakpoints), [['TABLET', 'DESKTOP']]);

  const mobileOnly = acceptAdSlots([slot('top', [creative('MOBILE', 320, 100)])], ['top']);
  assert.deepEqual(resolveAdZone(mobileOnly, [{ code: 'top', breakpoints: AD_BREAKPOINTS }]).map((v) => v.breakpoints), [['MOBILE']]);

  const tabletOnly = acceptAdSlots([slot('top', [creative('TABLET', 728, 90)])], ['top']);
  assert.deepEqual(resolveAdZone(tabletOnly, [{ code: 'top', breakpoints: AD_BREAKPOINTS }]).map((v) => v.breakpoints), [['TABLET', 'DESKTOP']]);
});

test('resolveAdZone: mã đứng trước thắng ở breakpoint chung; mỗi breakpoint tối đa một khung; banner giống nhau gộp một khung', () => {
  const slots = acceptAdSlots(
    [
      slot('desk', [creative('DESKTOP', 970, 90), creative('TABLET', 970, 90)], ' '),
      slot('mob', [creative('MOBILE', 320, 100)], 'Tài trợ'),
      slot('all', [creative('MOBILE'), creative('TABLET'), creative('DESKTOP')]),
    ],
    ['desk', 'mob', 'all'],
  );
  const views = resolveAdZone(slots, [
    { code: 'desk', breakpoints: ['TABLET', 'DESKTOP'] },
    { code: 'mob', breakpoints: ['MOBILE'] },
    { code: 'all', breakpoints: AD_BREAKPOINTS },
  ]);
  assert.deepEqual(views.map((v) => `${v.code}:${v.breakpoints.join(',')}`), ['mob:MOBILE', 'desk:TABLET,DESKTOP']);
  assert.equal(views[1]?.label, 'Quảng cáo');   // nhãn rỗng → nhãn mặc định (FR-ADS-001)
  assert.equal(views[0]?.label, 'Tài trợ');
  assert.deepEqual([...coveredBreakpoints(views)].sort(), ['DESKTOP', 'MOBILE', 'TABLET']);
  assert.deepEqual(resolveAdZone({}, [{ code: 'desk', breakpoints: AD_BREAKPOINTS }]), []);
});

test('AD_RANGE: ba khoảng bù nhau ở mốc 768/1024px và khớp CSS `.ad-slot--no-*`', () => {
  // Khoảng (min, max) của từng media query — đủ cho các dạng dùng ở đây.
  const range = (q: string): [number, number] => {
    const min = Number(/min-width: (\d+(?:\.\d+)?)px/.exec(q)?.[1] ?? 0);
    const max = Number(/max-width: (\d+(?:\.\d+)?)px/.exec(q)?.[1] ?? Infinity);
    return q.startsWith('not all and') ? [0, min - 0.01] : [min, max];
  };
  assert.deepEqual(range(AD_RANGE.MOBILE), [0, 767.99]);
  assert.deepEqual(range(AD_RANGE.TABLET), [768, 1023.98]);
  assert.deepEqual(range(AD_RANGE.DESKTOP), [1024, Infinity]);
  const css = readFileSync(new URL('../src/app/globals.css', import.meta.url), 'utf8');
  for (const bp of AD_BREAKPOINTS) assert.match(css, new RegExp(`\\.ad-slot--no-${bp.toLowerCase()} \\{ display: none; \\}`));
  assert.match(css, /@media \(min-width: 768px\) \{\n {2}\.ad-slot--no-mobile \{ display: block; \}\n {2}\.ad-slot--no-tablet \{ display: none; \}/);
  assert.match(css, /@media \(min-width: 1024px\) \{\n {2}\.ad-slot--no-tablet \{ display: block; \}\n {2}\.ad-slot--no-desktop \{ display: none; \}/);
});

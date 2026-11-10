// Đọc bodyBlocks an toàn ở trang công khai (CHANGELOG v0.3 mục 22).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { alignOf, runsOf, safeHref, tableRows } from '../src/lib/body.ts';

test('runs hợp lệ giữ định dạng; liên kết/màu không an toàn bị bỏ', () => {
  const runs = runsOf({ text: 'ab c', runs: [{ text: 'ab', marks: ['bold', 'x'], href: 'javascript:alert(1)', color: 'red' }, { text: ' c', href: 'https://a.vn', highlight: '#FFF59D' }] });
  assert.deepEqual(runs, [
    { text: 'ab', marks: ['bold'], href: null, color: null, highlight: null },
    { text: ' c', marks: [], href: 'https://a.vn', color: null, highlight: '#FFF59D' },
  ]);
});

test('runs không khớp text hoặc hỏng → chỉ text, không mất chữ', () => {
  assert.deepEqual(runsOf({ text: 'abc', runs: [{ text: 'ab' }] }).map((r) => r.text), ['abc']);
  assert.deepEqual(runsOf({ text: 'abc', runs: [{ text: '' }, { text: 'abc' }] }).map((r) => r.text), ['abc']);
  assert.deepEqual(runsOf({ text: '' }), []);
  assert.deepEqual(runsOf(null), []);
});

test('safeHref, alignOf, tableRows', () => {
  assert.equal(safeHref('mailto:a@b.vn'), 'mailto:a@b.vn');
  assert.equal(safeHref('data:text/html,x'), null);
  assert.equal(alignOf('center'), 'center');
  assert.equal(alignOf('justify'), null);
  assert.deepEqual(tableRows([[{ text: 'a' }]]), [[{ text: 'a' }]]);
  assert.equal(tableRows([[{ text: 'a' }], []]), null);
  assert.equal(tableRows('x'), null);
});

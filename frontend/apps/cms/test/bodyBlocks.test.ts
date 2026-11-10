// Chuyển đổi bodyBlocks ↔ tài liệu Tiptap (CHANGELOG v0.3 mục 22).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  blocksKey, blocksPlainText, blocksProblems, blocksToDoc, blocksWarnings, docToBlocks, normalizeHrefInput, parseBlocksKey,
  richText, safeHref, wordCount, type BodyBlock,
} from '../src/lib/bodyBlocks.ts';
import { textToBlocks } from '../src/lib/articles.ts';

const roundTrip = (blocks: BodyBlock[]) => docToBlocks(blocksToDoc(blocks));

test('khứ hồi giữ nguyên mọi kiểu khối đã biết', () => {
  const blocks: BodyBlock[] = [
    { type: 'heading', level: 2, text: 'Mở đầu' },
    { type: 'heading', level: 3, text: 'Phụ', align: 'center' },
    { type: 'paragraph', text: 'Dòng 1\nDòng 2' },
    {
      type: 'paragraph',
      text: 'Đậm nghiêng link màu',
      runs: [
        { text: 'Đậm', marks: ['bold'] },
        { text: ' ' },
        { text: 'nghiêng', marks: ['bold', 'italic'] },
        { text: ' ' },
        { text: 'link', href: 'https://example.com/a?b=1' },
        { text: ' ' },
        { text: 'màu', color: '#d32f2f', highlight: '#fff59d', marks: ['underline', 'strike'] },
      ],
      align: 'right',
    },
    { type: 'quote', text: 'Trích\ndẫn' },
    { type: 'list', ordered: false, items: [{ text: 'Một' }, { text: 'Hai', runs: [{ text: 'Hai', marks: ['bold'] }] }] },
    { type: 'list', ordered: true, items: [{ text: 'A' }] },
    { type: 'image', mediaId: '0b9a3c3e-8a51-4d3f-9a43-1f2f4c3d2e1a', alt: 'Ảnh máy', caption: 'Chú thích', align: 'center' },
    { type: 'table', headerRow: true, caption: 'Bảng so sánh', rows: [[{ text: 'Cột 1' }, { text: 'Cột 2' }], [{ text: 'a' }, { text: 'b', runs: [{ text: 'b', marks: ['italic'] }] }]] },
  ];
  assert.deepEqual(roundTrip(blocks), blocks);
});

test('khối lạ và khối sai dạng được giữ nguyên, không mất khi lưu', () => {
  const blocks = [
    { type: 'embed', provider: 'youtube', url: 'https://youtu.be/x' },
    { type: 'image', src: 'x' }, // dữ liệu cũ, không có mediaId
    { type: 'paragraph', text: 'ok' },
  ] as BodyBlock[];
  assert.deepEqual(roundTrip(blocks), blocks);
  assert.equal(blocksToDoc(blocks).content![0].type, 'rawBlock');
});

test('tương thích ngược: text luôn bằng nối runs, trình soạn tạm vẫn đọc được', () => {
  const out = roundTrip(textToBlocks('## Tiêu đề\n\nĐoạn một\n\n> Trích') as BodyBlock[]);
  assert.deepEqual(out, [
    { type: 'heading', level: 2, text: 'Tiêu đề' },
    { type: 'paragraph', text: 'Đoạn một' },
    { type: 'quote', text: 'Trích' },
  ]);
  for (const b of roundTrip([{ type: 'paragraph', text: 'ab', runs: [{ text: 'a', marks: ['bold'] }, { text: 'b' }] }])) {
    const r = b as { text: string; runs?: { text: string }[] };
    assert.equal(r.runs!.map((x) => x.text).join(''), r.text);
  }
});

test('runs hỏng hoặc không khớp text → bỏ runs, giữ chữ; href/màu không an toàn bị bỏ', () => {
  const out = roundTrip([
    { type: 'paragraph', text: 'abc', runs: [{ text: 'ab' }] },
    { type: 'paragraph', text: 'xy', runs: [{ text: 'x', href: 'javascript:alert(1)' }, { text: 'y', color: 'red' }] },
  ] as BodyBlock[]);
  assert.deepEqual(out, [{ type: 'paragraph', text: 'abc' }, { type: 'paragraph', text: 'xy' }]);
});

test('tài liệu Tiptap → blocks: bỏ đoạn rỗng, làm phẳng danh sách lồng, gộp run cùng định dạng', () => {
  const blocks = docToBlocks({
    type: 'doc',
    content: [
      { type: 'paragraph' },
      { type: 'paragraph', content: [{ type: 'text', text: '  ' }] },
      { type: 'paragraph', content: [{ type: 'text', text: 'a', marks: [{ type: 'bold' }] }, { type: 'text', text: 'b', marks: [{ type: 'bold' }] }, { type: 'hardBreak' }] },
      {
        type: 'bulletList',
        content: [
          { type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'cha' }] }, { type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'con' }] }] }] }] },
        ],
      },
      { type: 'paragraph', content: [{ type: 'text', text: 'L', marks: [{ type: 'link', attrs: { href: 'data:text/html,x' } }] }] },
    ],
  });
  assert.deepEqual(blocks, [
    { type: 'paragraph', text: 'ab', runs: [{ text: 'ab', marks: ['bold'] }] },
    { type: 'list', ordered: false, items: [{ text: 'cha' }, { text: 'con' }] },
    { type: 'paragraph', text: 'L' },
  ]);
});

test('tài liệu rỗng có một đoạn trống để gõ; blocks rỗng ↔ []', () => {
  assert.deepEqual(blocksToDoc([]), { type: 'doc', content: [{ type: 'paragraph' }] });
  assert.deepEqual(roundTrip([]), []);
});

test('liên kết: chỉ http(s)/mailto; người dùng gõ thiếu giao thức được thêm https', () => {
  assert.equal(safeHref('https://a.vn/x'), 'https://a.vn/x');
  assert.equal(safeHref('mailto:a@b.vn'), 'mailto:a@b.vn');
  for (const bad of ['javascript:alert(1)', 'data:text/html,x', '/tuong-doi', 'ftp://a', 'https://', '']) assert.equal(safeHref(bad), null, bad);
  assert.equal(normalizeHrefInput('tinhte.vn/abc'), 'https://tinhte.vn/abc');
  assert.equal(normalizeHrefInput('ai@tinhte.vn'), 'mailto:ai@tinhte.vn');
  assert.equal(normalizeHrefInput('JavaScript:alert(1)'), null);
});

test('richText: toàn chữ thường thì không có runs', () => {
  assert.deepEqual(richText([{ text: 'a' }, { text: 'b' }]), { text: 'ab' });
});

test('Draft.body: khóa JSON; bản sao lưu cũ dạng văn bản nhận ra được', () => {
  const blocks: BodyBlock[] = [{ type: 'paragraph', text: 'x' }];
  assert.deepEqual(parseBlocksKey(blocksKey(blocks)), blocks);
  assert.equal(parseBlocksKey('## Tiêu đề cũ'), null);
  assert.equal(parseBlocksKey('[hỏng'), null);
});

test('đếm chữ, giới hạn và nhắc alt', () => {
  const blocks: BodyBlock[] = [
    { type: 'paragraph', text: 'Xin chào bạn' },
    { type: 'list', ordered: false, items: [{ text: 'một' }] },
    { type: 'image', mediaId: '0b9a3c3e-8a51-4d3f-9a43-1f2f4c3d2e1a', alt: '' },
  ];
  assert.equal(wordCount(blocksPlainText(blocks)), 4);
  assert.deepEqual(blocksProblems(blocks), []);
  assert.equal(blocksWarnings(blocks).length, 1);
  assert.equal(blocksProblems([{ type: 'paragraph', text: 'x'.repeat(20001) }]).length, 1);
  assert.equal(blocksProblems([{ type: 'table', rows: [Array.from({ length: 21 }, () => ({ text: 'c' }))] }]).length, 1);
});

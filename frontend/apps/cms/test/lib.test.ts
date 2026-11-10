// Kiểm thư viện CMS (node:test, chạy TS trực tiếp như packages/sites).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { access, grantedPermissions, hasLockedPermissions, ALL_PERMISSIONS } from '../src/lib/rbac.ts';
import { loginUrl, readCookie, sanitizeReturnTo, toProblem } from '../src/lib/api.ts';
import { SECTIONS, sectionFor } from '../src/lib/nav.ts';

test('rbac: cùng quyết định với PermissionMatrix (C#)', () => {
  assert.equal(access({ roles: ['AUTHOR'], mfaSatisfied: false }, 'content.write_own'), 'allowed');
  assert.equal(access({ roles: ['EDITOR'], mfaSatisfied: true }, 'content.approve'), 'denied');
  assert.equal(access({ roles: ['APPROVER'], mfaSatisfied: false }, 'content.publish'), 'mfa-required');
  assert.equal(access({ roles: ['APPROVER'], mfaSatisfied: true }, 'content.publish'), 'allowed');
  assert.equal(access({ roles: ['ADMIN'], mfaSatisfied: true }, 'content.publish'), 'denied');
  assert.equal(access({ roles: ['UNKNOWN'], mfaSatisfied: true }, 'content.read'), 'denied');
  assert.throws(() => access({ roles: ['ADMIN'], mfaSatisfied: true }, 'nope' as never));
});

test('rbac: vai trò khớp CHECK ck_user_roles_role của migration mới nhất định nghĩa nó (0313, rồi 0351 thêm AD_OPS)', () => {
  const dir = new URL('../../../../doc/07_AI_REVIEW/v0.3/db/migrations/', import.meta.url);
  // "CONSTRAINT ck_user_roles_role CHECK (role IN ('A', …))" hoặc tt_migration.add_constraint(…, 'ck_user_roles_role', 'CHECK (role IN (''A'', …))').
  const pattern = /ck_user_roles_role(?:',\s*')?\s*CHECK \(role IN \(([^)]*)\)\)/;
  const checks = readdirSync(dir).filter((f) => f.endsWith('.up.sql')).sort()
    .map((f) => ({ file: f, list: pattern.exec(readFileSync(new URL(f, dir), 'utf8'))?.[1] }))
    .filter((x) => x.list !== undefined);
  assert.equal(checks.at(-1)?.file, '0351_ad_ops_role.up.sql');
  const fromSql = [...(checks.at(-1)?.list ?? '').matchAll(/'([A-Z_]+)'/g)].map((m) => m[1]).sort();
  const rbac = JSON.parse(readFileSync(new URL('../../../../config/rbac/rbac.json', import.meta.url), 'utf8'));
  assert.deepEqual(Object.keys(rbac.roles).sort(), fromSql);
});

test('rbac: quyền đã cấp và cảnh báo MFA', () => {
  const approver = grantedPermissions({ roles: ['APPROVER'], mfaSatisfied: false });
  assert.ok(approver.some((p) => p.permission === 'content.publish' && p.access === 'mfa-required'));
  assert.ok(approver.some((p) => p.permission === 'content.read' && p.access === 'allowed'));
  assert.equal(hasLockedPermissions({ roles: ['APPROVER'], mfaSatisfied: false }), true);
  assert.equal(hasLockedPermissions({ roles: ['AUTHOR'], mfaSatisfied: false }), false);
  assert.equal(hasLockedPermissions({ roles: ['APPROVER'], mfaSatisfied: true }), false);
});

test('nav: mọi mục dùng quyền có thật; tìm mục theo đường dẫn', () => {
  for (const s of SECTIONS) if (s.permission) assert.ok(ALL_PERMISSIONS.includes(s.permission), s.href);
  assert.equal(sectionFor('/')?.href, '/');
  assert.equal(sectionFor('/articles/123')?.href, '/articles');
  assert.equal(sectionFor('/articlesx'), undefined);
});

test('api: returnTo chặn open redirect, cùng quy tắc với API', () => {
  for (const ok of ['/', '/articles?status=DRAFT', '/a/b']) assert.equal(sanitizeReturnTo(ok), ok);
  for (const bad of ['//evil.example', 'https://evil.example', '/\\evil', '/a b', 'articles', '', null, undefined, `/${'a'.repeat(500)}`]) {
    assert.equal(sanitizeReturnTo(bad), '/');
  }
  assert.equal(loginUrl('/users?x=1'), '/api/v1/auth/staff/oidc/start?returnTo=%2Fusers%3Fx%3D1');
  assert.equal(loginUrl('//evil'), '/api/v1/auth/staff/oidc/start?returnTo=%2F');
});

test('api: đọc cookie CSRF', () => {
  assert.equal(readCookie('a=1; __Host-tt_cms_csrf=abc%2Ddef; b=2', '__Host-tt_cms_csrf'), 'abc-def');
  assert.equal(readCookie('x__Host-tt_cms_csrf=1', '__Host-tt_cms_csrf'), null);
  assert.equal(readCookie('', '__Host-tt_cms_csrf'), null);
});

test('api: chuẩn hóa Problem', () => {
  const p = toProblem(403, { title: 'CSRF token invalid', status: 403, code: 'CSRF_INVALID', traceId: 't' });
  assert.equal(p.code, 'CSRF_INVALID');
  assert.equal(toProblem(502, '<html>bad gateway</html>').code, 'DEPENDENCY_UNAVAILABLE');
  assert.equal(toProblem(401, null).code, 'UNAUTHENTICATED');
});

test('login: thông điệp theo reason/code mà API chuyển về /login', async () => {
  const { describeLoginError, returnToFor } = await import('../src/lib/loginErrors.ts');
  assert.equal(describeLoginError(''), null);
  assert.equal(describeLoginError('?returnTo=%2Fa'), null);
  const noRole = describeLoginError('?error=FORBIDDEN&reason=no_role&returnTo=%2Farticles');
  assert.equal(noRole?.code, 'FORBIDDEN');
  assert.equal(noRole?.error.retry, false);
  assert.match(noRole?.error.title ?? '', /chưa có quyền/);
  assert.equal(describeLoginError('?error=OIDC_STATE_INVALID&reason=state_invalid')?.error.retry, true);
  assert.equal(describeLoginError('?error=OIDC_STATE_INVALID')?.error.retry, true);           // thiếu reason → theo code
  assert.ok(describeLoginError('?error=WHATEVER&reason=nope')?.error.title);                  // mã lạ → thông điệp chung
  for (const reason of ['state_invalid', 'idp_error', 'denied', 'no_role', 'inactive', 'unavailable']) {
    assert.ok(describeLoginError(`?error=X&reason=${reason}`)?.error.message, reason);
  }
  assert.equal(returnToFor('/login', '?error=FORBIDDEN&returnTo=%2Fvideos'), '/videos');
  assert.equal(returnToFor('/login', '?returnTo=%2F%2Fevil.example'), '/');
  assert.equal(returnToFor('/articles', '?page=2'), '/articles?page=2');
});

test('login: các reason khớp x-browser-error-redirect của hợp đồng', async () => {
  const spec = readFileSync(new URL('../../../../doc/07_AI_REVIEW/v0.3/07_OPENAPI_TINHTE_v0.3_DRAFT.yaml', import.meta.url), 'utf8');
  const block = spec.slice(spec.indexOf('/auth/staff/oidc/start:'), spec.indexOf('/auth/staff/logout:'));
  const reasons = [...new Set([...block.matchAll(/^ {10}([a-z_]+):/gm)].map((m) => m[1]))].sort();
  const { describeLoginError } = await import('../src/lib/loginErrors.ts');
  assert.deepEqual(reasons, ['denied', 'idp_error', 'inactive', 'no_role', 'state_invalid', 'unavailable']);
  for (const r of reasons) assert.notEqual(describeLoginError(`?error=X&reason=${r}`)?.error.title, 'Không thể đăng nhập', r);
});

test('articles: văn bản ↔ khối nội dung (trình soạn tạm)', async () => {
  const { textToBlocks, blocksToText, canEditArticle, EDITABLE_STATUSES, statusLabel } = await import('../src/lib/articles.ts');
  const text = '## Mở đầu\n\nĐoạn một\ncòn tiếp\n\n> Trích dẫn\n> hai dòng\n\n### Mục nhỏ\n\nĐoạn hai';
  const blocks = textToBlocks(text);
  assert.deepEqual(blocks.map((b) => b.type), ['heading', 'paragraph', 'quote', 'heading', 'paragraph']);
  assert.equal(blocks[0].level, 2);
  assert.equal(blocks[3].level, 3);
  assert.equal(blocks[1].text, 'Đoạn một\ncòn tiếp');
  assert.equal(blocks[2].text, 'Trích dẫn\nhai dòng');
  const back = blocksToText(blocks);
  assert.equal(back.supported, true);
  assert.deepEqual(textToBlocks(back.text), blocks);                       // khứ hồi không mất nội dung
  assert.deepEqual(textToBlocks('\r\n\r\n  \n\n'), []);
  assert.equal(blocksToText([{ type: 'image', src: 'x' }]).supported, false); // khối lạ → chỉ xem
  assert.equal(blocksToText([]).text, '');

  const can = (granted: string[]) => (p: string) => granted.includes(p);
  assert.equal(canEditArticle(can(['content.write_own']), 'u1', { ownerId: 'u1' }), true);
  assert.equal(canEditArticle(can(['content.write_own']), 'u1', { ownerId: 'u2' }), false);
  assert.equal(canEditArticle(can(['content.write_any']), 'u1', { ownerId: 'u2' }), true);
  assert.equal(EDITABLE_STATUSES.has('IN_REVIEW'), false);
  assert.equal(statusLabel('PUBLISHED'), 'Đã xuất bản');
});

test('articles: trạng thái sửa được khớp ArticleStatuses.Editable của backend', async () => {
  const { EDITABLE_STATUSES, ARTICLE_STATUSES } = await import('../src/lib/articles.ts');
  const cs = readFileSync(new URL('../../../../backend/src/Modules/Tinhte.Modules.News/Domain/ArticleRules.cs', import.meta.url), 'utf8');
  const block = cs.slice(cs.indexOf('Editable'), cs.indexOf('};', cs.indexOf('Editable')));
  const names = [...block.matchAll(/\b([A-Z][a-zA-Z]+)\b/g)].map((m) => m[1]).filter((n) => !['Editable', 'HashSet', 'StringComparer', 'Ordinal', 'IReadOnlySet'].includes(n));
  const toConst = (n: string) => n.replace(/([a-z])([A-Z])/g, '$1_$2').toUpperCase();
  assert.deepEqual(names.map(toConst).sort(), [...EDITABLE_STATUSES].sort());
  const all = cs.slice(cs.indexOf('All ='), cs.indexOf('};', cs.indexOf('All =')));
  assert.equal(ARTICLE_STATUSES.length, [...all.matchAll(/\b(Draft|InReview|ChangesRequested|Rejected|Approved|Scheduled|Published|Unpublished|Retracted)\b/g)].length);
});

test('review: hàng đợi theo vai trò (WBS-031)', async () => {
  const { visibleQueues, queueUrl, badgeText, waitingFor, isOverdue, QUEUES } = await import('../src/lib/review.ts');
  const { access } = await import('../src/lib/rbac.ts');
  const keysFor = (roles: string[], mfa = true) => visibleQueues((p) => access({ roles, mfaSatisfied: mfa }, p)).map((q) => q.key);
  assert.deepEqual(keysFor(['AUTHOR']), ['to-fix', 'my-submitted']);
  assert.deepEqual(keysFor(['EDITOR']), ['to-review', 'to-fix', 'my-submitted']);
  assert.deepEqual(keysFor(['APPROVER']), ['to-review', 'to-publish']);
  assert.deepEqual(keysFor(['APPROVER'], false), ['to-review', 'to-publish']);      // thiếu MFA vẫn thấy việc, nút sẽ yêu cầu MFA
  assert.deepEqual(keysFor(['ADMIN']), []);
  assert.deepEqual(keysFor(['MODERATOR']), []);

  const fix = QUEUES.find((q) => q.key === 'to-fix')!;
  const url = new URL(`http://x/api/v1${queueUrl(fix, 'u-1', 20, 'c1')}`);
  assert.equal(url.pathname, '/api/v1/admin/articles');
  assert.equal(url.searchParams.get('status'), 'CHANGES_REQUESTED,REJECTED');
  assert.equal(url.searchParams.get('assigneeId'), 'u-1');
  assert.equal(url.searchParams.get('sort'), 'updatedAt_asc');
  assert.equal(url.searchParams.get('cursor'), 'c1');
  assert.match(QUEUES.find((q) => q.key === 'to-review')!.params('u').sort, /^updatedAt_asc$/); // chờ lâu nhất trước

  assert.equal(badgeText(0, false), null);
  assert.equal(badgeText(5, false), '5');
  assert.equal(badgeText(100, true), '99+');
  const now = new Date('2026-11-09T12:00:00Z');
  assert.equal(waitingFor('2026-11-09T11:59:40Z', now), 'vừa xong');
  assert.equal(waitingFor('2026-11-09T11:15:00Z', now), '45 phút');
  assert.equal(waitingFor('2026-11-09T07:00:00Z', now), '5 giờ');
  assert.equal(waitingFor('2026-11-06T12:00:00Z', now), '3 ngày');
  assert.equal(waitingFor('2026-11-10T00:00:00Z', now), 'vừa xong');
  assert.equal(isOverdue('2026-11-08T11:00:00Z', now), true);
  assert.equal(isOverdue('2026-11-09T01:00:00Z', now), false);
});

test('review: bộ lọc hàng đợi chỉ dùng trạng thái/sort mà API chấp nhận', async () => {
  const { QUEUES } = await import('../src/lib/review.ts');
  const { ARTICLE_STATUSES } = await import('../src/lib/articles.ts');
  for (const q of QUEUES) {
    const p = q.params('u');
    for (const s of p.status.split(',')) assert.ok((ARTICLE_STATUSES as readonly string[]).includes(s), `${q.key}: ${s}`);
    assert.match(p.sort, /^(updatedAt_desc|updatedAt_asc|createdAt_desc|title_asc)$/);
  }
});

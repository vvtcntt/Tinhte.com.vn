// Chạy thử migration v0.3 trên PostgreSQL thật (PGlite = PostgreSQL biên dịch WASM, một kết nối).
//   cd doc/07_AI_REVIEW/v0.3/db/tests && npm install && npm test
//   (hoặc PGLITE_MODULE=<đường dẫn>/@electric-sql/pglite/dist/index.js node run_migration_tests.mjs)
// Kịch bản:
//   A  stub schema 04 → up toàn bộ (+ optional) → chạy lại up (idempotent) → kiểm hành vi → down toàn bộ → so schema → up lại
//   B  stub + 07_DB_ADS_LITE_REFERENCE_v0.2.sql + dữ liệu cũ → up → 0310 phải dừng → backfill → 0310 → down → so schema
//   C  thiếu bảng tiên quyết → migration phải dừng với thông báo rõ
// Giới hạn: PGlite chỉ có một kết nối → KHÔNG kiểm được khóa đồng thời (FOR SHARE vs UPDATE) — cần test trên PostgreSQL thật.
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const { PGlite } = await import(process.env.PGLITE_MODULE ? pathToFileURL(process.env.PGLITE_MODULE).href : '@electric-sql/pglite');

const HERE = path.dirname(fileURLToPath(import.meta.url));
const MIG_DIR = path.resolve(HERE, '..', 'migrations');
const OPT_DIR = path.join(MIG_DIR, 'optional');
const STUB = path.join(HERE, 'baseline_04_stub.sql');
const V02 = path.resolve(HERE, '..', '..', '..', '..', '07_DB_ADS_LITE_REFERENCE_v0.2.sql');

const REPO = path.resolve(HERE, '..', '..', '..', '..', '..');
const { generateDbSync } = await import(pathToFileURL(path.join(REPO, 'tools', 'sites', 'generate-db-sync.mjs')).href);
const { registry: siteRegistry } = await import(pathToFileURL(path.join(REPO, 'frontend', 'packages', 'sites', 'src', 'index.ts')).href);

const read = (f) => readFileSync(f, 'utf8');
const list = (dir) => readdirSync(dir).filter((f) => f.endsWith('.up.sql')).sort().map((f) => path.join(dir, f));
const UPS = list(MIG_DIR);
const OPT_UPS = list(OPT_DIR);
const toDown = (f) => f.replace(/\.up\.sql$/, '.down.sql');
const ALL_DOWNS = [...OPT_UPS, ...UPS].map(toDown).reverse().sort((a, b) => path.basename(b).localeCompare(path.basename(a)));
const base = (f) => path.basename(f);

const results = [];
function record(scenario, name, ok, info = '') {
  results.push({ scenario, name, ok, info });
  console.log(`${ok ? 'PASS' : 'FAIL'} [${scenario}] ${name}${info ? ' — ' + info : ''}`);
}
const errText = (e) => [e.message, e.detail, e.constraint].filter(Boolean).join(' | ');

// Mỗi file migration chạy trong MỘT transaction (giống Flyway/DbUp).
async function apply(db, file) {
  await db.exec('BEGIN');
  try {
    await db.exec(read(file));
    await db.exec('COMMIT');
  } catch (e) {
    await db.exec('ROLLBACK');
    throw new Error(`${base(file)}: ${errText(e)}`);
  }
}
async function applyAll(sc, db, files, label) {
  for (const f of files) {
    try { await apply(db, f); record(sc, `${label} ${base(f)}`, true); }
    catch (e) { record(sc, `${label} ${base(f)}`, false, e.message); }
  }
}
// Kiểm hành vi trong transaction rồi ROLLBACK để không để lại dữ liệu.
async function expectOk(sc, db, name, sql) {
  await db.exec('BEGIN');
  try { await db.exec(sql); await db.exec('ROLLBACK'); record(sc, name, true); }
  catch (e) { await db.exec('ROLLBACK'); record(sc, name, false, errText(e)); }
}
async function expectError(sc, db, name, sql, pattern) {
  await db.exec('BEGIN');
  try { await db.exec(sql); await db.exec('ROLLBACK'); record(sc, name, false, 'không có lỗi'); }
  catch (e) {
    try { await db.exec('ROLLBACK'); } catch { /* ignore */ }
    const t = errText(e);
    record(sc, name, pattern.test(t), t.slice(0, 170));
  }
}
async function expectValue(sc, db, name, sql, expected) {
  const r = await db.query(sql);
  const v = Object.values(r.rows[0] ?? {})[0];
  const ok = JSON.stringify(v) === JSON.stringify(expected);
  record(sc, name, ok, ok ? '' : `nhận ${JSON.stringify(v)}, kỳ vọng ${JSON.stringify(expected)}`);
}

const SNAP = `SELECT json_build_object(
  'columns', (SELECT coalesce(json_agg(x ORDER BY x), '[]') FROM (SELECT table_name || '.' || column_name || ' ' || data_type || ' '
              || is_nullable || ' ' || coalesce(column_default, '') AS x FROM information_schema.columns WHERE table_schema = 'tt') s),
  'constraints', (SELECT coalesce(json_agg(x ORDER BY x), '[]') FROM (SELECT conrelid::regclass::text || ' ' || conname || ' '
              || pg_get_constraintdef(oid) AS x FROM pg_constraint WHERE connamespace = 'tt'::regnamespace) s),
  'indexes', (SELECT coalesce(json_agg(indexdef ORDER BY indexdef), '[]') FROM pg_indexes WHERE schemaname = 'tt'),
  'triggers', (SELECT coalesce(json_agg(x ORDER BY x), '[]') FROM (SELECT t.tgrelid::regclass::text || ' ' || t.tgname AS x
              FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE NOT t.tgisinternal AND c.relnamespace = 'tt'::regnamespace) s),
  'functions', (SELECT coalesce(json_agg(x ORDER BY x), '[]') FROM (SELECT p.oid::regprocedure::text AS x FROM pg_proc p
              WHERE p.pronamespace = 'tt'::regnamespace) s),
  'feature_gates', (SELECT json_agg(key || '=' || enabled ORDER BY key) FROM tt.feature_gates),
  'tt_migration', (SELECT json_agg(to_regnamespace('tt_migration') IS NOT NULL))
) AS snap`;
const snapshot = async (db) => (await db.query(SNAP)).rows[0].snap;
function diff(a, b) {
  const out = [];
  for (const k of Object.keys(a)) {
    const A = new Set(a[k]), B = new Set(b[k]);
    for (const x of A) if (!B.has(x)) out.push(`- ${k}: ${x}`);
    for (const x of B) if (!A.has(x)) out.push(`+ ${k}: ${x}`);
  }
  return out;
}
function expectSame(sc, name, a, b) {
  const d = diff(a, b);
  record(sc, name, d.length === 0, d.slice(0, 8).join(' ; '));
}

// ---------------------------------------------------------------- dữ liệu mẫu
const id = (s) => `00000000-0000-0000-0000-${s.padStart(12, '0')}`;
const [U1, U2] = [id('a1'), id('a2')];
const APPROVE = (key) => `UPDATE tt.feature_gates SET enabled = true, approval_reference = 'TEST-APPROVAL', approved_by = '${U1}', approved_at = now() WHERE key = '${key}';`;
const SPONSOR = `'ACME', 'HD-01', 'dms://hd-01'`;
const FIXTURES = `
  INSERT INTO tt.users (id, display_name) VALUES ('${U1}', 'Approver'), ('${U2}', 'Member');
  INSERT INTO tt.media_assets (id) VALUES ('${id('e1')}');
  INSERT INTO tt.categories (id, slug, name) VALUES ('${id('ca1')}', 'cong-nghe', 'Công nghệ');
  INSERT INTO tt.articles (id, slug) VALUES ('${id('b1')}', 'bai-mot'), ('${id('b2')}', 'bai-hai');
  INSERT INTO tt.article_revisions (id, article_id, number, title, content_kind) VALUES
    ('${id('f1')}', '${id('b1')}', 1, 'Editorial', 'EDITORIAL'),
    ('${id('f5')}', '${id('b2')}', 1, 'Bài khác', 'EDITORIAL');
  INSERT INTO tt.article_revisions (id, article_id, number, title, content_kind, sponsor_name, sponsor_reference,
      sponsorship_evidence_ref, sponsorship_valid_from, sponsorship_valid_to, sponsorship_approved_by, sponsorship_approved_at) VALUES
    ('${id('f2')}', '${id('b1')}', 2, 'Tài trợ chưa duyệt', 'SPONSORED', ${SPONSOR}, now() - interval '1 day', now() + interval '30 days', NULL, NULL),
    ('${id('f3')}', '${id('b1')}', 3, 'Tài trợ đã duyệt', 'SPONSORED', ${SPONSOR}, now() - interval '1 day', now() + interval '30 days', '${U1}', now()),
    ('${id('f4')}', '${id('b1')}', 4, 'Tài trợ hết hạn', 'SPONSORED', ${SPONSOR}, now() - interval '60 days', now() - interval '1 day', '${U1}', now());
  INSERT INTO tt.videos (id, slug, media_id) VALUES ('${id('d1')}', 'video-mot', '${id('e1')}');
  INSERT INTO tt.video_revisions (id, video_id, number, title, content_kind, sponsor_name, sponsor_reference,
      sponsorship_evidence_ref, sponsorship_valid_from, sponsorship_valid_to, sponsorship_approved_by, sponsorship_approved_at) VALUES
    ('${id('d2')}', '${id('d1')}', 1, 'Video tài trợ', 'SPONSORED', ${SPONSOR}, now() - interval '1 day', now() + interval '30 days', '${U1}', now());
  INSERT INTO tt.moderation_cases (id, entity_type, entity_id) VALUES ('${id('c1')}', 'COMMENT', '${id('cc1')}');
`;
const TAKEDOWN = `INSERT INTO tt.takedown_requests (id, content_type, content_id, requester_type, reason)
  VALUES ('${id('7d1')}', 'VIDEO', '${id('d1')}', 'RIGHTS_HOLDER', 'Khiếu nại bản quyền');`;
const SLOT = (over = '') => `INSERT INTO tt.ad_placements (code, label, status, media_asset_id, alt_text, target_url, width, height, approved_by, approved_at)
  VALUES ${over || `('article_bottom', 'Quảng cáo', 'ACTIVE', '${id('e1')}', 'Banner ACME', 'https://acme.vn', 728, 90, '${U1}', now())`};`;

async function behaviour(sc, db) {
  // Registry cờ (0300)
  await expectValue(sc, db, 'F1 17 khóa registry + 1 khóa ngoài registry được giữ', `SELECT count(*)::int FROM tt.feature_gates`, 18);
  await expectValue(sc, db, 'F1b khóa cũ đã đổi tên hết', `SELECT count(*)::int FROM tt.feature_gates WHERE key IN ('community.comments','member.bookmark')`, 0);
  await expectValue(sc, db, 'F1c chỉ community.moderation ON, mọi cờ public OFF', `SELECT string_agg(key, ',' ORDER BY key) FROM tt.feature_gates WHERE enabled`, 'community.moderation');
  await expectError(sc, db, 'F2 bật cờ không có hồ sơ duyệt bị chặn', `UPDATE tt.feature_gates SET enabled = true WHERE key = 'ads.sponsored_publication'`, /ck_feature_gates_enabled_requires_approval/);
  await expectOk(sc, db, 'F3 bật cờ có hồ sơ duyệt → audit có người thao tác + version tăng', `SET LOCAL tt.actor_id = '${U1}'; ${APPROVE('ads.sponsored_publication')}
    DO $$ BEGIN
      IF (SELECT count(*) FROM tt.feature_gate_changes WHERE key = 'ads.sponsored_publication' AND new_enabled AND changed_by = '${U1}') <> 1 THEN RAISE EXCEPTION 'audit missing'; END IF;
      IF (SELECT version FROM tt.feature_gates WHERE key = 'ads.sponsored_publication') <> 2 THEN RAISE EXCEPTION 'version not bumped'; END IF;
    END $$;`);
  await expectError(sc, db, 'F4 xóa nhật ký cờ bị chặn', `${APPROVE('ads.display_slots')} DELETE FROM tt.feature_gate_changes`, /APPEND_ONLY/);
  await expectError(sc, db, 'F5 TRUNCATE nhật ký cờ bị chặn', `TRUNCATE tt.feature_gate_changes`, /APPEND_ONLY/);

  // Revision tài trợ (0301)
  await expectError(sc, db, 'R1 revision SPONSORED thiếu sponsorship bị chặn', `INSERT INTO tt.article_revisions (article_id, number, title, content_kind) VALUES ('${id('b1')}', 9, 'x', 'SPONSORED')`, /ck_article_revisions_sponsorship_complete/);
  await expectError(sc, db, 'R2 validTo ≤ validFrom bị chặn', `INSERT INTO tt.article_revisions (article_id, number, title, content_kind, sponsor_name, sponsor_reference, sponsorship_evidence_ref, sponsorship_valid_from, sponsorship_valid_to) VALUES ('${id('b1')}', 9, 'x', 'SPONSORED', ${SPONSOR}, now(), now() - interval '1 day')`, /ck_article_revisions_sponsorship_window/);
  await expectError(sc, db, 'R3 approved_by thiếu approved_at bị chặn', `UPDATE tt.article_revisions SET sponsorship_approved_by = '${U1}' WHERE id = '${id('f2')}'`, /ck_article_revisions_sponsorship_approval_pair/);
  await expectError(sc, db, 'R4 content_kind ngoài enum bị chặn', `UPDATE tt.video_revisions SET content_kind = 'ADVERTORIAL' WHERE id = '${id('d2')}'`, /ck_video_revisions_content_kind/);

  // Chốt chặn publish (0307)
  const PUB = (rev, art = 'b1') => `UPDATE tt.articles SET public_revision_id = '${id(rev)}' WHERE id = '${id(art)}';`;
  await expectError(sc, db, 'P1 publish tài trợ khi cờ OFF → FEATURE_DISABLED', PUB('f3'), /FEATURE_DISABLED/);
  await expectError(sc, db, 'P2 cờ ON, tài trợ chưa duyệt → SPONSORSHIP_LABEL_REQUIRED', APPROVE('ads.sponsored_publication') + PUB('f2'), /SPONSORSHIP_LABEL_REQUIRED/);
  await expectError(sc, db, 'P3 cờ ON, tài trợ hết hạn → SPONSORSHIP_EXPIRED', APPROVE('ads.sponsored_publication') + PUB('f4'), /SPONSORSHIP_EXPIRED/);
  await expectOk(sc, db, 'P4 cờ ON, đã duyệt, còn hạn → publish được', APPROVE('ads.sponsored_publication') + PUB('f3'));
  await expectOk(sc, db, 'P5 bài EDITORIAL publish được khi cờ tài trợ OFF', PUB('f1'));
  await expectError(sc, db, 'P6 revision của bài khác → STATE_CONFLICT', PUB('f5'), /STATE_CONFLICT/);
  await expectError(sc, db, 'P7 video tài trợ khi cờ OFF → FEATURE_DISABLED', `UPDATE tt.videos SET public_revision_id = '${id('d2')}' WHERE id = '${id('d1')}'`, /FEATURE_DISABLED/);

  // Chốt chặn ghi bình luận (0307)
  const COMMENT = `INSERT INTO tt.comments (video_id, author_id, text) VALUES ('${id('d1')}', '${U2}', 'Xin chào');`;
  await expectError(sc, db, 'C1 ghi bình luận khi cờ OFF → FEATURE_DISABLED', COMMENT, /FEATURE_DISABLED/);
  await expectOk(sc, db, 'C2 ghi bình luận khi cờ ON (có duyệt)', APPROVE('community.video_comments') + COMMENT);

  // Slot quảng cáo (0302)
  const S = (code, status, media, alt, url, w, h, appr) => SLOT(`('${code}', 'Quảng cáo', '${status}', ${media}, ${alt}, ${url}, ${w}, ${h}, ${appr ? `'${U1}', now()` : 'NULL, NULL'})`);
  const M = `'${id('e1')}'`;
  await expectError(sc, db, 'A1 targetUrl javascript: bị chặn', S('slot_a', 'DRAFT', 'NULL', 'NULL', `'javascript:alert(1)'`, 728, 90, false), /ck_ad_placements_target_https/);
  await expectError(sc, db, 'A2 ACTIVE chưa duyệt bị chặn', S('slot_a', 'ACTIVE', M, `'alt'`, `'https://acme.vn'`, 728, 90, false), /ck_ad_placements_approval/);
  await expectError(sc, db, 'A3 APPROVED thiếu alt/media bị chặn', S('slot_a', 'APPROVED', 'NULL', 'NULL', `'https://acme.vn'`, 728, 90, true), /ck_ad_placements_serving_complete/);
  await expectError(sc, db, 'A4 code sai định dạng bị chặn', S('Bad Code', 'DRAFT', 'NULL', 'NULL', 'NULL', 728, 90, false), /ck_ad_placements_code_format/);
  await expectError(sc, db, 'A5 thiếu kích thước bị chặn (chống CLS)', S('slot_a', 'DRAFT', 'NULL', 'NULL', 'NULL', 'NULL', 90, false), /ck_ad_placements_dimensions/);
  await expectError(sc, db, 'A6 status ngoài enum bị chặn', S('slot_a', 'LIVE', 'NULL', 'NULL', 'NULL', 728, 90, false), /ck_ad_placements_status/);
  await expectOk(sc, db, 'A7 slot ACTIVE đầy đủ hợp lệ; UPDATE tăng row_version', `${SLOT()} UPDATE tt.ad_placements SET label = 'QC' WHERE code = 'article_bottom';
    DO $$ BEGIN IF (SELECT row_version FROM tt.ad_placements WHERE code = 'article_bottom') <> 2 THEN RAISE EXCEPTION 'row_version'; END IF; END $$;`);

  // Takedown (0303)
  await expectOk(sc, db, 'T1 ghi nhận yêu cầu takedown', TAKEDOWN);
  await expectError(sc, db, 'T2 ACTIONED khi chưa có action → STATE_CONFLICT (kiểm hoãn)', `${TAKEDOWN} UPDATE tt.takedown_requests SET status = 'TRIAGED'; UPDATE tt.takedown_requests SET status = 'ACTIONED'; SET CONSTRAINTS ALL IMMEDIATE;`, /STATE_CONFLICT/);
  await expectOk(sc, db, 'T3 có action UNPUBLISH → ACTIONED hợp lệ, tự sinh 7 bước lan truyền', `${TAKEDOWN} UPDATE tt.takedown_requests SET status = 'TRIAGED';
    INSERT INTO tt.takedown_actions (takedown_id, action, reason, actor_id) VALUES ('${id('7d1')}', 'UNPUBLISH', 'Gỡ theo khiếu nại', '${U1}');
    UPDATE tt.takedown_requests SET status = 'ACTIONED'; SET CONSTRAINTS ALL IMMEDIATE;
    DO $$ BEGIN IF (SELECT count(*) FROM tt.takedown_propagation WHERE status = 'PENDING') <> 7 THEN RAISE EXCEPTION 'propagation'; END IF; END $$;`);
  const EVID = `${TAKEDOWN} INSERT INTO tt.takedown_evidence (takedown_id, ref) VALUES ('${id('7d1')}', 'dms://email-01');`;
  await expectError(sc, db, 'T4 sửa bằng chứng bị chặn', `${EVID} UPDATE tt.takedown_evidence SET ref = 'x'`, /APPEND_ONLY/);
  await expectError(sc, db, 'T5 xóa bằng chứng bị chặn', `${EVID} DELETE FROM tt.takedown_evidence`, /APPEND_ONLY/);
  await expectError(sc, db, 'T6 xóa yêu cầu takedown bị chặn', `${TAKEDOWN} DELETE FROM tt.takedown_requests`, /APPEND_ONLY/);
  await expectError(sc, db, 'T7 TRUNCATE bằng chứng bị chặn', `TRUNCATE tt.takedown_evidence`, /APPEND_ONLY/);
  await expectError(sc, db, 'T8 RECEIVED → CLOSED bị chặn', `${TAKEDOWN} UPDATE tt.takedown_requests SET status = 'CLOSED'`, /STATE_CONFLICT/);
  await expectError(sc, db, 'T9 bước lan truyền DONE thiếu completed_at bị chặn', `${TAKEDOWN}
    INSERT INTO tt.takedown_actions (takedown_id, action, reason) VALUES ('${id('7d1')}', 'UNPUBLISH', 'Gỡ theo khiếu nại');
    UPDATE tt.takedown_propagation SET status = 'DONE' WHERE step = 'CDN_PURGE'`, /ck_takedown_propagation_done_at/);
  await expectError(sc, db, 'T10 down 0303 từ chối khi đã có yêu cầu takedown', `${TAKEDOWN} ${read(path.join(MIG_DIR, '0303_takedown.down.sql'))}`, /Refusing to roll back 0303/);
  await expectError(sc, db, 'T11 action thiếu lý do bị chặn', `${TAKEDOWN} INSERT INTO tt.takedown_actions (takedown_id, action, reason) VALUES ('${id('7d1')}', 'UNPUBLISH', 'x')`, /takedown_actions_reason_check/);

  // Moderation (0304)
  await expectError(sc, db, 'M1 priority ngoài enum bị chặn', `UPDATE tt.moderation_cases SET priority = 'SUPER'`, /ck_moderation_cases_priority/);
  await expectError(sc, db, 'M2 sửa lịch sử kiểm duyệt bị chặn', `INSERT INTO tt.moderation_case_history (case_id, action, reason) VALUES ('${id('c1')}', 'HIDE', 'Spam lặp lại'); UPDATE tt.moderation_case_history SET reason = 'x'`, /APPEND_ONLY/);
  await expectError(sc, db, 'M3 HIDE thiếu lý do bị chặn', `INSERT INTO tt.moderation_case_history (case_id, action) VALUES ('${id('c1')}', 'HIDE')`, /ck_moderation_case_history_reason/);
  await expectOk(sc, db, 'M4 ASSIGN không cần lý do; UPDATE case tăng row_version', `INSERT INTO tt.moderation_case_history (case_id, action) VALUES ('${id('c1')}', 'ASSIGN');
    UPDATE tt.moderation_cases SET assignee_id = '${U1}'; DO $$ BEGIN IF (SELECT row_version FROM tt.moderation_cases) <> 2 THEN RAISE EXCEPTION 'row_version'; END IF; END $$;`);

  // Media (0305)
  await expectError(sc, db, 'MD1 media PUBLIC khi quyền chưa CLEARED bị chặn', `UPDATE tt.media_assets SET visibility = 'PUBLIC'`, /ck_media_assets_public_requires_cleared/);
  await expectOk(sc, db, 'MD2 media PUBLIC khi quyền CLEARED', `UPDATE tt.media_assets SET rights_status = 'CLEARED', visibility = 'PUBLIC'`);

  // Bài/video (0306)
  await expectError(sc, db, 'G1 PUBLISHED thiếu public revision bị chặn', `UPDATE tt.articles SET status = 'PUBLISHED', rights_status = 'CLEARED' WHERE id = '${id('b1')}'`, /ck_articles_published_requires_public_revision/);
  await expectError(sc, db, 'G2 PUBLISHED khi quyền chưa CLEARED bị chặn', `${PUB('f1')} UPDATE tt.articles SET status = 'PUBLISHED' WHERE id = '${id('b1')}'`, /ck_articles_published_requires_cleared_rights/);
  await expectError(sc, db, 'G3 SCHEDULED thiếu scheduled_at bị chặn', `UPDATE tt.articles SET status = 'SCHEDULED' WHERE id = '${id('b1')}'`, /ck_articles_scheduled_requires_time/);
  await expectError(sc, db, 'G4 status ngoài enum bị chặn', `UPDATE tt.videos SET status = 'LIVE'`, /ck_videos_status/);
  await expectOk(sc, db, 'G5 publish hợp lệ (EDITORIAL, CLEARED); row_version tăng', `${PUB('f1')} UPDATE tt.articles SET status = 'PUBLISHED', rights_status = 'CLEARED' WHERE id = '${id('b1')}';
    DO $$ BEGIN IF (SELECT row_version FROM tt.articles WHERE id = '${id('b1')}') <> 3 THEN RAISE EXCEPTION 'row_version'; END IF; END $$;`);

  // Redirect slug + idempotency (0308)
  const RENAME = `UPDATE tt.articles SET slug = 'bai-mot-moi' WHERE id = '${id('b1')}';`;
  await expectOk(sc, db, 'S1 đổi slug → slug cũ thành redirect', `${RENAME} DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM tt.article_slug_redirects WHERE old_slug = 'bai-mot' AND article_id = '${id('b1')}') THEN RAISE EXCEPTION 'redirect'; END IF; END $$;`);
  await expectError(sc, db, 'S2 bài khác chiếm slug đang là redirect → STATE_CONFLICT', `${RENAME} UPDATE tt.articles SET slug = 'bai-mot' WHERE id = '${id('b2')}'`, /STATE_CONFLICT/);
  await expectOk(sc, db, 'S3 bài lấy lại slug cũ → redirect cũ bị gỡ, slug vừa bỏ thành redirect', `${RENAME} UPDATE tt.articles SET slug = 'bai-mot' WHERE id = '${id('b1')}';
    DO $$ BEGIN IF EXISTS (SELECT 1 FROM tt.article_slug_redirects WHERE old_slug = 'bai-mot') OR NOT EXISTS (SELECT 1 FROM tt.article_slug_redirects WHERE old_slug = 'bai-mot-moi') THEN RAISE EXCEPTION 'redirect swap'; END IF; END $$;`);
  const IDEM = `INSERT INTO tt.idempotency_keys (actor_key, http_method, route_template, idempotency_key, request_hash) VALUES ('${U2}', 'POST', '/videos/{videoId}/comments', 'key-12345678', repeat('a', 64));`;
  await expectError(sc, db, 'I1 trùng Idempotency-Key cùng phạm vi bị chặn', IDEM + IDEM, /idempotency_keys_pkey/);
  await expectError(sc, db, 'I2 Idempotency-Key quá ngắn bị chặn', IDEM.replace('key-12345678', 'short'), /idempotency_key_check/);

  // row_version cho danh mục (0309)
  await expectOk(sc, db, 'L1 UPDATE category tăng row_version', `UPDATE tt.categories SET name = 'Công nghệ mới'; DO $$ BEGIN IF (SELECT row_version FROM tt.categories) <> 2 THEN RAISE EXCEPTION 'row_version'; END IF; END $$;`);

  // Revision bài viết + publish pointer (0320/0321, WBS-022, FR-NEWS-005)
  const REV = (rev, set) => `UPDATE tt.article_revisions SET ${set} WHERE id = '${id(rev)}';`;
  await expectOk(sc, db, 'N1 revision DRAFT chưa public sửa được nội dung, updated_at được chạm', `${REV('f5', `title = 'Sửa', body = '[{"type":"p"}]', updated_at = now() - interval '1 day'`)}
    DO $$ BEGIN IF (SELECT updated_at < now() - interval '1 hour' FROM tt.article_revisions WHERE id = '${id('f5')}') THEN RAISE EXCEPTION 'touch'; END IF; END $$;`);
  await expectError(sc, db, 'N2 revision đã gửi duyệt không sửa được nội dung → STATE_CONFLICT', `${REV('f5', `status = 'IN_REVIEW'`)} ${REV('f5', `title = 'Lén sửa'`)}`, /STATE_CONFLICT.*immutable/);
  await expectError(sc, db, 'N3 public revision không sửa được nội dung (kể cả status DRAFT) → STATE_CONFLICT', `${PUB('f1')} ${REV('f1', `summary = 'đổi sapo'`)}`, /STATE_CONFLICT.*public and immutable/);
  await expectOk(sc, db, 'N4 revision đã đóng băng vẫn đổi được trạng thái + thông tin xuất bản', `${REV('f5', `status = 'APPROVED'`)} ${REV('f5', `status = 'PUBLISHED', published_at = now(), published_by = '${U1}'`)}`);
  await expectError(sc, db, 'N5 revision đã rời DRAFT không quay lại DRAFT', `${REV('f5', `status = 'REJECTED'`)} ${REV('f5', `status = 'DRAFT'`)}`, /STATE_CONFLICT.*cannot return to DRAFT/);
  await expectError(sc, db, 'N6 revision không xóa được (lịch sử sửa bài)', `DELETE FROM tt.article_revisions WHERE id = '${id('f5')}'`, /APPEND_ONLY/);
  await expectError(sc, db, 'N7 status revision ngoài vòng đời bị chặn', REV('f5', `status = 'LIVE'`), /ck_article_revisions_status/);
  await expectError(sc, db, 'N8 slug sai định dạng bị chặn', `UPDATE tt.articles SET slug = 'Bai Mot' WHERE id = '${id('b2')}'`, /ck_articles_slug_format/);
  await expectError(sc, db, 'N9 body revision phải là mảng block', REV('f5', `body = '{"type":"p"}'`), /ck_article_revisions_body_array/);
  await expectError(sc, db, 'N10 published_by thiếu published_at bị chặn', REV('f5', `published_by = '${U1}'`), /ck_article_revisions_published_pair/);

  // Tải lên media (0330, WBS-046, FR-VID-005) — upload intent → finalize → pipeline
  const MEDIA = (set) => `UPDATE tt.media_assets SET ${set} WHERE id = '${id('e1')}';`;
  const SHA = 'a'.repeat(64);
  const INTENT = `INSERT INTO tt.media_assets (id, media_type, file_name, content_type, size_bytes, checksum_sha256, storage_key, upload_token_hash, upload_expires_at, created_by)
    VALUES ('${id('e2')}', 'VIDEO', 'clip.mp4', 'video/mp4', 1024, '${SHA}', 'media/2026/11/${id('e2')}', '${'b'.repeat(64)}', now() + interval '15 minutes', '${U1}');`;
  await expectError(sc, db, 'MV1 status media ngoài enum MediaStatus bị chặn', MEDIA(`status = 'DONE'`), /ck_media_assets_status/);
  await expectError(sc, db, 'MV2 checksum không phải sha256 hex thường bị chặn', MEDIA(`checksum_sha256 = '${'A'.repeat(64)}'`), /ck_media_assets_checksum_format/);
  await expectError(sc, db, 'MV3 upload token thiếu hạn bị chặn', MEDIA(`upload_token_hash = '${SHA}'`), /ck_media_assets_upload_token/);
  await expectError(sc, db, 'MV4 upload token chỉ tồn tại khi UPLOADING', `${INTENT} UPDATE tt.media_assets SET status = 'UPLOADED', uploaded_at = now() WHERE id = '${id('e2')}'`, /ck_media_assets_upload_token/);
  await expectError(sc, db, 'MV5 đã finalize phải có storage_key/size/checksum', MEDIA(`status = 'UPLOADED', uploaded_at = now()`), /ck_media_assets_uploaded_complete/);
  await expectError(sc, db, 'MV6 kích thước ≤ 0 bị chặn', MEDIA(`size_bytes = 0`), /ck_media_assets_size_positive/);
  await expectOk(sc, db, 'MV7 intent → finalize (xóa token) → READY; mỗi UPDATE tăng row_version, quyền giữ PRIVATE/UNVERIFIED', `${INTENT}
    UPDATE tt.media_assets SET status = 'UPLOADED', uploaded_at = now(), upload_token_hash = NULL, upload_expires_at = NULL WHERE id = '${id('e2')}';
    UPDATE tt.media_assets SET status = 'READY' WHERE id = '${id('e2')}';
    DO $$ BEGIN IF (SELECT (row_version, visibility, rights_status) <> (3::bigint, 'PRIVATE', 'UNVERIFIED') FROM tt.media_assets WHERE id = '${id('e2')}')
      THEN RAISE EXCEPTION 'row_version/rights'; END IF; END $$;`);
  await expectError(sc, db, 'MV8 down 0330 từ chối khi đã có object trong kho lưu trữ', `${INTENT} ${read(path.join(MIG_DIR, '0330_media_uploads.down.sql'))}`, /Refusing to roll back 0330/);

  // Revision video (0331, WBS-046/040) — cùng mô hình đóng băng với bài viết
  const VREV = (rev, set) => `UPDATE tt.video_revisions SET ${set} WHERE id = '${id(rev)}';`;
  const VDRAFT = `INSERT INTO tt.video_revisions (id, video_id, number, title) VALUES ('${id('d3')}', '${id('d1')}', 2, 'Video biên tập');`;
  await expectOk(sc, db, 'V1 revision video DRAFT chưa public sửa được nội dung', `${VDRAFT} ${VREV('d3', `title = 'Sửa', summary = 'Mô tả'`)}`);
  await expectError(sc, db, 'V2 revision video đã gửi duyệt không sửa được nội dung → STATE_CONFLICT', `${VDRAFT} ${VREV('d3', `status = 'IN_REVIEW'`)} ${VREV('d3', `title = 'Lén sửa'`)}`, /STATE_CONFLICT.*immutable/);
  await expectError(sc, db, 'V3 public revision video không sửa được (kể cả DRAFT) → STATE_CONFLICT', `${VDRAFT}
    UPDATE tt.videos SET public_revision_id = '${id('d3')}' WHERE id = '${id('d1')}'; ${VREV('d3', `summary = 'đổi'`)}`, /STATE_CONFLICT.*public and immutable/);
  await expectError(sc, db, 'V4 revision video đã rời DRAFT không quay lại DRAFT', `${VDRAFT} ${VREV('d3', `status = 'REJECTED'`)} ${VREV('d3', `status = 'DRAFT'`)}`, /cannot return to DRAFT/);
  await expectError(sc, db, 'V5 revision video không xóa được', `DELETE FROM tt.video_revisions WHERE id = '${id('d2')}'`, /APPEND_ONLY/);
  await expectError(sc, db, 'V6 status revision video ngoài vòng đời bị chặn', VREV('d2', `status = 'LIVE'`), /ck_video_revisions_status/);
  await expectError(sc, db, 'V7 tiêu đề revision video rỗng bị chặn', VREV('d2', `title = '  '`), /ck_video_revisions_title_not_blank/);
  await expectError(sc, db, 'V8 down 0331 từ chối khi có lịch sử workflow', `${VREV('d2', `status = 'IN_REVIEW'`)} ${read(path.join(MIG_DIR, '0331_video_revisions.down.sql'))}`, /Refusing to roll back 0331/);

  // Site Registry trong DB (0312, ADR-0001 D8) — SQL đồng bộ sinh từ config/sites/sites.json
  const SYNC = generateDbSync(siteRegistry);
  const withGame = structuredClone(siteRegistry);
  withGame.sites.push({ ...withGame.sites[0], key: 'game', label: 'Game', subdomain: 'game', aliases: [], status: 'planned', scope: 'P1' });
  await expectOk(sc, db, 'Z1 nội dung có sẵn và mới mặc định thuộc site www', `${SYNC}
    INSERT INTO tt.articles (slug) VALUES ('bai-moi');
    DO $$ BEGIN IF EXISTS (SELECT 1 FROM tt.articles WHERE primary_site_key <> 'www')
      OR (SELECT site_key FROM tt.categories LIMIT 1) <> 'www' THEN RAISE EXCEPTION 'default site'; END IF; END $$;`);
  await expectError(sc, db, 'Z2 site không có trong registry bị chặn (trigger trước, khóa ngoại dự phòng)', `${SYNC} UPDATE tt.articles SET primary_site_key = 'nope' WHERE id = '${id('b1')}'`, /VALIDATION_FAILED.*= nope/);
  await expectError(sc, db, 'Z3 nội dung không được gán cho site cms/media', `${SYNC} UPDATE tt.videos SET primary_site_key = 'cms'`, /VALIDATION_FAILED/);
  await expectOk(sc, db, 'Z4 đồng bộ idempotent; thêm site chỉ bằng registry; bỏ khỏi registry → retired, không xóa', `${SYNC}${SYNC}
    DO $$ BEGIN IF (SELECT count(*) FROM tt.sites) <> ${siteRegistry.sites.length} THEN RAISE EXCEPTION 'sync count'; END IF; END $$;
    ${generateDbSync(withGame)}
    UPDATE tt.articles SET primary_site_key = 'game' WHERE id = '${id('b2')}';
    ${SYNC}
    DO $$ BEGIN IF (SELECT status FROM tt.sites WHERE key = 'game') <> 'retired' THEN RAISE EXCEPTION 'retire'; END IF; END $$;`);
  await expectError(sc, db, 'Z5 xóa site bị chặn (chỉ retired)', `${SYNC} DELETE FROM tt.sites WHERE key = 'media'`, /APPEND_ONLY/);

  // Tùy chọn 0311
  await expectError(sc, db, 'O1 privacy request VERIFIED thiếu verified_at bị chặn', `INSERT INTO tt.privacy_requests (request_type, email_ciphertext, email_hmac, policy_version, status) VALUES ('ACCESS', '\\x00', repeat('b', 64), 'v1', 'VERIFIED')`, /ck_privacy_requests_verified_at/);
}

// ---------------------------------------------------------------- Kịch bản A
{
  const sc = 'A';
  const db = new PGlite();
  await db.exec(read(STUB));
  const baseline = await snapshot(db);
  await applyAll(sc, db, UPS, 'UP');
  await applyAll(sc, db, OPT_UPS, 'UP');
  const afterUp = await snapshot(db);
  await applyAll(sc, db, [...UPS, ...OPT_UPS], 'UP lần 2 (idempotent)');
  expectSame(sc, 'Chạy up lần 2 không đổi schema', afterUp, await snapshot(db));
  await db.exec(FIXTURES);
  await behaviour(sc, db);
  await applyAll(sc, db, ALL_DOWNS, 'DOWN');
  expectSame(sc, 'Sau down toàn bộ, schema trở về đúng baseline 04', baseline, await snapshot(db));
  await applyAll(sc, db, UPS, 'UP lại sau down');
  await db.close();
}

// ---------------------------------------------------------------- Kịch bản B
{
  const sc = 'B';
  const db = new PGlite();
  await db.exec(read(STUB));
  try { await db.exec(read(V02)); record(sc, 'SQL addendum v0.2 chạy được trên stub (lần đầu được chạy thử)', true); }
  catch (e) { record(sc, 'SQL addendum v0.2 chạy được trên stub (lần đầu được chạy thử)', false, errText(e)); }
  await db.exec(`INSERT INTO tt.ad_placements (code, status) VALUES ('legacy_slot', 'DRAFT');`);  // dòng v0.2 chưa có kích thước
  const baseline = await snapshot(db);
  const main = UPS.filter((f) => !base(f).startsWith('0310'));
  const validate = UPS.find((f) => base(f).startsWith('0310'));
  await applyAll(sc, db, main, 'UP');
  await expectValue(sc, db, 'Khóa v0.2 đổi sang registry, giữ trạng thái OFF',
    `SELECT string_agg(key || '=' || enabled, ',' ORDER BY key) FROM tt.feature_gates WHERE key LIKE 'ads.%'`,
    'ads.campaign_advanced=false,ads.display_slots=false,ads.shorts_instream=false,ads.sponsored_publication=false');
  {
    const report = await db.exec(read(path.resolve(HERE, '..', 'tools', 'report_not_valid_violations.sql')));
    const rows = report.at(-1).rows.map((r) => `${r.table_name}.${r.constraint_name}=${r.violating_rows}`);
    record(sc, 'Báo cáo vi phạm NOT VALID chỉ ra đúng dòng v0.2 thiếu kích thước',
      JSON.stringify(rows) === JSON.stringify(['ad_placements.ck_ad_placements_dimensions=1']), rows.join(', '));
  }
  try { await apply(db, validate); record(sc, '0310 phải DỪNG khi dữ liệu cũ thiếu kích thước slot', false, 'không dừng'); }
  catch (e) { record(sc, '0310 phải DỪNG khi dữ liệu cũ thiếu kích thước slot', /ck_ad_placements_dimensions/.test(e.message), e.message.slice(0, 170)); }
  await db.exec(`UPDATE tt.ad_placements SET width = 728, height = 90 WHERE code = 'legacy_slot';`);
  await applyAll(sc, db, [validate], 'UP sau backfill');
  await expectValue(sc, db, 'Mọi ràng buộc v0.3 đã VALIDATED',
    `SELECT count(*)::int FROM pg_constraint c JOIN tt_migration.ledger l ON l.object_name = c.conname WHERE l.object_type = 'constraint' AND NOT c.convalidated`, 0);
  await applyAll(sc, db, UPS.map(toDown).reverse(), 'DOWN');
  expectSame(sc, 'Sau down, schema + khóa cờ trở về đúng trạng thái v0.2', baseline, await snapshot(db));
  await db.close();
}

// ---------------------------------------------------------------- Kịch bản C
{
  const sc = 'C';
  const db = new PGlite();
  await db.exec(read(STUB));
  await db.exec('DROP TABLE tt.moderation_cases;');
  await apply(db, UPS.find((f) => base(f).startsWith('0299')));
  try { await apply(db, UPS.find((f) => base(f).startsWith('0304'))); record(sc, '0304 dừng khi thiếu tt.moderation_cases', false, 'không dừng'); }
  catch (e) { record(sc, '0304 dừng khi thiếu tt.moderation_cases', /Precondition failed: table tt\.moderation_cases/.test(e.message), e.message.slice(0, 170)); }
  await db.close();
}

const failed = results.filter((r) => !r.ok);
console.log(`\nTỔNG: ${results.length - failed.length}/${results.length} PASS` + (failed.length ? ` — ${failed.length} FAIL` : ''));
process.exitCode = failed.length ? 1 : 0;

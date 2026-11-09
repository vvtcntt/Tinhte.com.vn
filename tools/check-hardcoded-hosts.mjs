// Chặn hostname viết cứng trong mã nguồn (ADR-0001 D1): host phải suy ra từ config/sites/sites.json.
// Quét backend/src, frontend/apps, frontend/packages/*/src. Bỏ qua test, build output, node_modules.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { registry, SITE_ENVS } from '../frontend/packages/sites/src/index.ts';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SCAN = ['backend/src', 'frontend/apps', 'frontend/packages'];
const SKIP_DIRS = new Set(['node_modules', '.next', 'bin', 'obj', 'test', 'tests', 'dist', 'out']);
const EXTS = new Set(['.cs', '.ts', '.tsx', '.js', '.mjs', '.json', '.css']);
const SKIP_FILES = new Set(['next-env.d.ts', 'package-lock.json']);

const suffixes = [...new Set(SITE_ENVS.map((env) => registry.environments[env].domainSuffix))]
  .sort((a, b) => b.length - a.length);
const pattern = new RegExp(suffixes.map((s) => s.replace(/\./g, '\\.')).join('|'), 'i');

const hits = [];
function walk(dir) {
  for (const name of readdirSync(dir)) {
    const full = path.join(dir, name);
    const st = statSync(full);
    if (st.isDirectory()) {
      if (!SKIP_DIRS.has(name)) walk(full);
    } else if (EXTS.has(path.extname(name)) && !SKIP_FILES.has(name)) {
      readFileSync(full, 'utf8').split('\n').forEach((line, i) => {
        if (pattern.test(line)) hits.push(`${path.relative(ROOT, full)}:${i + 1}: ${line.trim()}`);
      });
    }
  }
}
for (const d of SCAN) {
  try { walk(path.join(ROOT, d)); } catch (e) { if (e.code !== 'ENOENT') throw e; }
}
if (hits.length) {
  console.error(`Hard-coded site hostnames found (${hits.length}) — derive hosts from config/sites/sites.json:\n${hits.join('\n')}`);
  process.exit(1);
}
console.log(`No hard-coded site hostnames (checked ${suffixes.join(', ')})`);

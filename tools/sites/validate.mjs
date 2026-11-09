// Kiểm Site Registry (schema + ràng buộc chéo). Dùng trong CI. Thoát mã 1 nếu lỗi.
import { registry, validateRegistry, resolvableSites, hostFor, SITE_ENVS } from '../../frontend/packages/sites/src/index.ts';

const errors = validateRegistry(registry);
if (errors.length) {
  console.error(`Site registry INVALID (${errors.length}):\n - ${errors.join('\n - ')}`);
  process.exit(1);
}
console.log(`Site registry OK — ${registry.sites.length} sites`);
for (const env of SITE_ENVS) {
  const hosts = resolvableSites(env).map((s) => `${s.key}=${hostFor(s, env)}`);
  console.log(`  ${env.padEnd(10)} ${hosts.join('  ')}`);
}

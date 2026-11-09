import path from 'node:path';
import type { NextConfig } from 'next';

// Gốc repo: cho phép import config/sites/sites.json (Site Registry, ADR-0001) từ @tinhte/sites.
const repoRoot = path.resolve(process.cwd(), '../../..');

const config: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  transpilePackages: ['@tinhte/sites'],
  turbopack: { root: repoRoot },
  outputFileTracingRoot: repoRoot,
  // Chỉ dùng khi chạy Next trực tiếp (không qua edge): /api/v1 cùng origin → API. Production: edge route /api/* tới API.
  async rewrites() {
    return [{ source: '/api/v1/:path*', destination: `${process.env.TT_API_ORIGIN ?? 'http://localhost:5080'}/api/v1/:path*` }];
  },
};

export default config;

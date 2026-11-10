import type { ReactNode } from 'react';
import type { Viewport } from 'next';
import { Be_Vietnam_Pro } from 'next/font/google';
import './globals.css';

// Font tự host: next/font tải lúc build rồi phục vụ từ /_next/static (đúng CSP `font-src 'self'`).
// Chỉ 3 độ đậm theo thang chữ trong globals.css: 400 (nội dung), 600 (giao diện), 700 (tiêu đề).
const sans = Be_Vietnam_Pro({
  subsets: ['latin', 'vietnamese'],
  weight: ['400', '600', '700'],
  display: 'swap',
  variable: '--font-sans',
});

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: '#ffffff' },
    { media: '(prefers-color-scheme: dark)', color: '#151a20' },
  ],
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="vi" className={sans.variable}>
      <body>{children}</body>
    </html>
  );
}

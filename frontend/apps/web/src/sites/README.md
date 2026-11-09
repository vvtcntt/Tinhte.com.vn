# Giao diện riêng theo site (tùy chọn)

Mặc định mọi site `kind: public-web` trong `config/sites/sites.json` dùng layout chung ở `src/app/[site]/`.

Nếu một subdomain cần giao diện hoặc trang riêng, tạo thư mục `src/sites/<siteKey>/` chứa component/layout của site đó, rồi chọn theo `site.key` trong `src/app/[site]/layout.tsx`. **Không** viết cứng hostname: luôn dùng `siteKey` và các helper của `@tinhte/sites` (ADR-0001).

// Logo dạng SVG nội tuyến: nét sắc ở mọi mật độ điểm ảnh và đổi màu theo dark mode qua token
// (--logo-blue, --logo-green, --logo-word trong globals.css).
// Bản vector dựng lại từ src/assets/logo.png (PNG vẫn dùng cho JSON-LD publisher.logo).
// Thay bằng file SVG gốc khi bộ nhận diện thương hiệu chính thức có (WBS-016).

function Mark({ person, leaf, personOpacity }: { person: string; leaf: string; personOpacity?: number }) {
  return (
    <>
      {/* Nhóm có opacity được ghép như một lớp → chỗ đầu chồng thân không bị đậm hơn. */}
      <g fill={person} opacity={personOpacity}>
        <path d="M160 118C82 118 18 196 18 318C18 398 62 414 150 392C226 372 252 300 242 220C232 150 202 118 160 118Z" />
        <circle cx="115" cy="76" r="62" />
      </g>
      <ellipse cx="272" cy="257" rx="97" ry="155" transform="rotate(-12 272 257)" fill={leaf} />
    </>
  );
}

/** Logo đầy đủ (biểu tượng + chữ). Trang trí: tên truy cập được đặt ở link bao ngoài. */
export function Logo({ className, height = 40 }: { className?: string; height?: number }) {
  return (
    <svg
      className={className}
      viewBox="0 0 1181 428"
      height={height}
      width={Math.round((height * 1181) / 428)}
      aria-hidden="true"
      focusable="false"
    >
      <Mark person="var(--logo-blue)" leaf="var(--logo-green)" />
      <g style={{ fontFamily: 'var(--font-sans), system-ui, sans-serif' }}>
        <text x="440" y="228" fontSize="178" fontWeight="700" textLength="718" lengthAdjust="spacing" fill="var(--logo-word)">
          TINHTE
        </text>
        <text x="447" y="335" fontSize="66" fontWeight="400" textLength="423" lengthAdjust="spacing" fill="var(--logo-green)">
          .COM.VN
        </text>
      </g>
    </svg>
  );
}

/** Chỉ biểu tượng, một màu theo `currentColor` — dùng làm nền cho ảnh thiếu. */
export function LogoMark({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 400 428" aria-hidden="true" focusable="false">
      <Mark person="currentColor" leaf="currentColor" personOpacity={0.55} />
    </svg>
  );
}

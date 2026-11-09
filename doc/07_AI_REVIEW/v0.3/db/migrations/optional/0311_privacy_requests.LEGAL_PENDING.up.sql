-- 0311 — [LEGAL PENDING] Lưu yêu cầu dữ liệu cá nhân gửi không cần tài khoản (POST /privacy/requests; AIR-017, FR-ID-005)
-- CHỈ CHẠY khi Legal chọn kênh API (thay vì quy trình ngoài hệ thống). Không nằm trong chuỗi mặc định.
-- Không lưu email dạng rõ: mã hóa ở tầng ứng dụng (khóa trong secret manager), kèm HMAC để chống trùng/giới hạn tần suất.
-- retention_until đặt theo chính sách lưu trữ do Legal duyệt.

SELECT tt_migration.track_new_table('0311', 'privacy_requests');
CREATE TABLE IF NOT EXISTS tt.privacy_requests (
  id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  request_type            varchar(20) NOT NULL CHECK (request_type IN ('ACCESS', 'EXPORT', 'RECTIFY', 'DELETE', 'WITHDRAW_CONSENT')),
  email_ciphertext        bytea NOT NULL,
  email_hmac              char(64) NOT NULL,
  note_ciphertext         bytea,
  policy_version          varchar(40) NOT NULL,
  status                  varchar(24) NOT NULL DEFAULT 'PENDING_VERIFICATION'
                          CHECK (status IN ('PENDING_VERIFICATION', 'VERIFIED', 'IN_PROGRESS', 'COMPLETED', 'REJECTED', 'EXPIRED')),
  verification_token_hash char(64),
  verification_expires_at timestamptz,
  verified_at             timestamptz,
  completed_at            timestamptz,
  created_at              timestamptz NOT NULL DEFAULT now(),
  retention_until         timestamptz,
  CONSTRAINT ck_privacy_requests_verified_at CHECK (status NOT IN ('VERIFIED', 'IN_PROGRESS', 'COMPLETED') OR verified_at IS NOT NULL),
  CONSTRAINT ck_privacy_requests_completed_at CHECK (status <> 'COMPLETED' OR completed_at IS NOT NULL)
);
SELECT tt_migration.add_index('0311', 'privacy_requests', 'ix_privacy_requests_email_hmac',
  'ON tt.privacy_requests (email_hmac, created_at)');
SELECT tt_migration.add_index('0311', 'privacy_requests', 'ix_privacy_requests_status',
  'ON tt.privacy_requests (status, created_at)');

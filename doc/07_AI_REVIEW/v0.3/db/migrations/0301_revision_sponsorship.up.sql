-- 0301 — Metadata tài trợ trên revision bất biến (AIR-012, AIR-024; FR-NEWS-008, FR-ADS-001)
-- Ánh xạ schema API v0.3 `Sponsorship` → cột:
--   sponsorName → sponsor_name · sponsorReference → sponsor_reference · evidenceRef → sponsorship_evidence_ref
--   validFrom/validTo → sponsorship_valid_from/to · approvedBy/approvedAt → sponsorship_approved_by/at
-- Tương thích v0.2 addendum: content_kind/sponsor_name/sponsor_reference đã có thì giữ nguyên (ledger không ghi → down không xóa).
-- CHECK thêm ở dạng NOT VALID: áp ngay cho dòng mới/sửa; dữ liệu cũ được VALIDATE ở 0310.

SELECT tt_migration.require_table('users');
SELECT tt_migration.require_table('article_revisions');
SELECT tt_migration.require_table('video_revisions');

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['article_revisions', 'video_revisions'] LOOP
    PERFORM tt_migration.add_column('0301', t, 'content_kind', 'varchar(16) NOT NULL DEFAULT ''EDITORIAL''');
    PERFORM tt_migration.add_column('0301', t, 'sponsor_name', 'varchar(180)');
    PERFORM tt_migration.add_column('0301', t, 'sponsor_reference', 'varchar(120)');
    PERFORM tt_migration.add_column('0301', t, 'sponsorship_evidence_ref', 'varchar(300)');
    PERFORM tt_migration.add_column('0301', t, 'sponsorship_valid_from', 'timestamptz');
    PERFORM tt_migration.add_column('0301', t, 'sponsorship_valid_to', 'timestamptz');
    PERFORM tt_migration.add_column('0301', t, 'sponsorship_approved_by', 'uuid REFERENCES tt.users(id)');
    PERFORM tt_migration.add_column('0301', t, 'sponsorship_approved_at', 'timestamptz');
    PERFORM tt_migration.add_column('0301', t, 'change_reason', 'varchar(500)');

    PERFORM tt_migration.add_constraint('0301', t, 'ck_' || t || '_content_kind',
      'CHECK (content_kind IN (''EDITORIAL'', ''SPONSORED'')) NOT VALID');
    -- Khớp SponsorshipRequiredRule: SPONSORED phải có đủ sponsorship (API trả 422 SPONSORSHIP_LABEL_REQUIRED).
    PERFORM tt_migration.add_constraint('0301', t, 'ck_' || t || '_sponsorship_complete',
      'CHECK (content_kind <> ''SPONSORED'' OR (sponsor_name IS NOT NULL AND btrim(sponsor_name) <> '''' '
      || 'AND sponsor_reference IS NOT NULL AND sponsorship_evidence_ref IS NOT NULL '
      || 'AND sponsorship_valid_from IS NOT NULL AND sponsorship_valid_to IS NOT NULL)) NOT VALID');
    PERFORM tt_migration.add_constraint('0301', t, 'ck_' || t || '_sponsorship_window',
      'CHECK (sponsorship_valid_to IS NULL OR sponsorship_valid_from IS NULL OR sponsorship_valid_to > sponsorship_valid_from) NOT VALID');
    PERFORM tt_migration.add_constraint('0301', t, 'ck_' || t || '_sponsorship_approval_pair',
      'CHECK ((sponsorship_approved_by IS NULL) = (sponsorship_approved_at IS NULL)) NOT VALID');

    -- Phục vụ job hết hạn tài trợ (gỡ/ẩn khi validTo qua).
    PERFORM tt_migration.add_index('0301', t, 'ix_' || t || '_sponsorship_expiry',
      format('ON tt.%I (sponsorship_valid_to) WHERE content_kind = ''SPONSORED''', t));
  END LOOP;
END $$;

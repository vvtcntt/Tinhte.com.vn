-- 0299 — Hạ tầng migration v0.3 + hàm trigger dùng chung
-- Hợp đồng: 07_OPENAPI_TINHTE_v0.3_DRAFT.yaml · Đề xuất: AIR-024
-- Chạy trong MỘT transaction (công cụ migration bọc BEGIN/COMMIT). Không chứa BEGIN/COMMIT riêng.
--
-- Ledger ghi lại đúng những đối tượng mỗi migration đã TẠO MỚI, để bản down chỉ gỡ chúng
-- (an toàn cả khi DB đã áp hoặc chưa áp 07_DB_ADS_LITE_REFERENCE_v0.2.sql).

CREATE SCHEMA IF NOT EXISTS tt_migration;

CREATE TABLE IF NOT EXISTS tt_migration.ledger (
  seq         bigserial PRIMARY KEY,
  migration   varchar(10) NOT NULL,
  object_type varchar(12) NOT NULL CHECK (object_type IN ('table', 'column', 'constraint', 'index', 'trigger', 'function')),
  table_name  text,
  object_name text NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE OR REPLACE FUNCTION tt_migration.record(p_mig text, p_type text, p_table text, p_name text)
RETURNS void LANGUAGE sql AS $$
  INSERT INTO tt_migration.ledger (migration, object_type, table_name, object_name) VALUES (p_mig, p_type, p_table, p_name);
$$;

CREATE OR REPLACE FUNCTION tt_migration.require_table(p_table text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF to_regclass(format('tt.%I', p_table)) IS NULL THEN
    RAISE EXCEPTION 'Precondition failed: table tt.% is required (schema 04). Reconcile 04_DATABASE_SCHEMA_REFERENCE_v0.1.sql first.', p_table;
  END IF;
END $$;

CREATE OR REPLACE FUNCTION tt_migration.has_column(p_table text, p_column text)
RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM information_schema.columns
                 WHERE table_schema = 'tt' AND table_name = p_table AND column_name = p_column);
$$;

CREATE OR REPLACE FUNCTION tt_migration.require_column(p_table text, p_column text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  PERFORM tt_migration.require_table(p_table);
  IF NOT tt_migration.has_column(p_table, p_column) THEN
    RAISE EXCEPTION 'Precondition failed: column tt.%.% is required (schema 04).', p_table, p_column;
  END IF;
END $$;

-- Ghi nhận bảng sắp tạo nếu hiện CHƯA tồn tại (gọi ngay trước CREATE TABLE IF NOT EXISTS).
CREATE OR REPLACE FUNCTION tt_migration.track_new_table(p_mig text, p_table text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF to_regclass(format('tt.%I', p_table)) IS NULL THEN
    PERFORM tt_migration.record(p_mig, 'table', NULL, p_table);
  END IF;
END $$;

-- Ghi nhận hàm (chữ ký đầy đủ, ví dụ 'tg_touch_row()') nếu chưa migration nào ghi nhận.
CREATE OR REPLACE FUNCTION tt_migration.track_function(p_mig text, p_signature text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM tt_migration.ledger WHERE object_type = 'function' AND object_name = p_signature) THEN
    PERFORM tt_migration.record(p_mig, 'function', NULL, p_signature);
  END IF;
END $$;

CREATE OR REPLACE FUNCTION tt_migration.add_column(p_mig text, p_table text, p_column text, p_definition text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF NOT tt_migration.has_column(p_table, p_column) THEN
    EXECUTE format('ALTER TABLE tt.%I ADD COLUMN %I %s', p_table, p_column, p_definition);
    PERFORM tt_migration.record(p_mig, 'column', p_table, p_column);
  END IF;
END $$;

CREATE OR REPLACE FUNCTION tt_migration.add_constraint(p_mig text, p_table text, p_name text, p_definition text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = format('tt.%I', p_table)::regclass AND conname = p_name) THEN
    EXECUTE format('ALTER TABLE tt.%I ADD CONSTRAINT %I %s', p_table, p_name, p_definition);
    PERFORM tt_migration.record(p_mig, 'constraint', p_table, p_name);
  END IF;
END $$;

-- p_definition: phần sau tên index, ví dụ 'ON tt.ad_placements (code) WHERE status = ''ACTIVE'''
CREATE OR REPLACE FUNCTION tt_migration.add_index(p_mig text, p_table text, p_name text, p_definition text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF to_regclass(format('tt.%I', p_name)) IS NULL THEN
    EXECUTE format('CREATE INDEX %I %s', p_name, p_definition);
    PERFORM tt_migration.record(p_mig, 'index', p_table, p_name);
  END IF;
END $$;

-- p_definition: phần sau tên trigger. p_constraint = true để tạo CONSTRAINT TRIGGER (deferrable).
CREATE OR REPLACE FUNCTION tt_migration.add_trigger(p_mig text, p_table text, p_name text, p_definition text, p_constraint boolean DEFAULT false)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = format('tt.%I', p_table)::regclass AND tgname = p_name AND NOT tgisinternal) THEN
    EXECUTE format('CREATE %sTRIGGER %I %s', CASE WHEN p_constraint THEN 'CONSTRAINT ' ELSE '' END, p_name, p_definition);
    PERFORM tt_migration.record(p_mig, 'trigger', p_table, p_name);
  END IF;
END $$;

-- Gỡ mọi đối tượng migration p_mig đã tạo, theo thứ tự ngược. Không dùng CASCADE: phụ thuộc lạ sẽ làm down dừng lại.
CREATE OR REPLACE FUNCTION tt_migration.rollback(p_mig text)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE r record;
BEGIN
  FOR r IN SELECT * FROM tt_migration.ledger WHERE migration = p_mig ORDER BY seq DESC LOOP
    CASE r.object_type
      WHEN 'trigger'    THEN EXECUTE format('DROP TRIGGER IF EXISTS %I ON tt.%I', r.object_name, r.table_name);
      WHEN 'index'      THEN EXECUTE format('DROP INDEX IF EXISTS tt.%I', r.object_name);
      WHEN 'constraint' THEN EXECUTE format('ALTER TABLE tt.%I DROP CONSTRAINT IF EXISTS %I', r.table_name, r.object_name);
      WHEN 'column'     THEN EXECUTE format('ALTER TABLE tt.%I DROP COLUMN IF EXISTS %I', r.table_name, r.object_name);
      WHEN 'table'      THEN EXECUTE format('DROP TABLE IF EXISTS tt.%I', r.object_name);
      WHEN 'function'   THEN EXECUTE format('DROP FUNCTION IF EXISTS tt.%s', r.object_name);
    END CASE;
  END LOOP;
  DELETE FROM tt_migration.ledger WHERE migration = p_mig;
END $$;

-- Từ chối rollback khi bảng còn dữ liệu cần lưu giữ (bằng chứng pháp lý, audit, SEO).
CREATE OR REPLACE FUNCTION tt_migration.refuse_if_rows(p_mig text, p_table text, p_reason text)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_has boolean;
BEGIN
  IF to_regclass(format('tt.%I', p_table)) IS NOT NULL THEN
    EXECUTE format('SELECT EXISTS (SELECT 1 FROM tt.%I)', p_table) INTO v_has;
    IF v_has THEN
      RAISE EXCEPTION 'Refusing to roll back %: tt.% contains rows (%). Export the data and obtain approval before rolling back.', p_mig, p_table, p_reason;
    END IF;
  END IF;
END $$;

-- ---------------------------------------------------------------------------------
-- Hàm dùng chung cho các migration 0300–0311
-- ---------------------------------------------------------------------------------

-- Lỗi nghiệp vụ: MESSAGE = mã trong x-error-catalog (FEATURE_DISABLED, SPONSORSHIP_LABEL_REQUIRED...).
-- Ứng dụng map SQLSTATE P0001 + MESSAGE sang Problem.code.
CREATE OR REPLACE FUNCTION tt.raise_domain_error(p_code text, p_detail text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = p_code, DETAIL = p_detail;
END $$;
SELECT tt_migration.track_function('0299', 'raise_domain_error(text,text)');

CREATE OR REPLACE FUNCTION tt.tg_touch_row()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at := now();
  NEW.row_version := OLD.row_version + 1;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0299', 'tg_touch_row()');

CREATE OR REPLACE FUNCTION tt.tg_bump_row_version()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.row_version := OLD.row_version + 1;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0299', 'tg_bump_row_version()');

CREATE OR REPLACE FUNCTION tt.tg_touch_updated_at()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at := now();
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0299', 'tg_touch_updated_at()');

CREATE OR REPLACE FUNCTION tt.tg_append_only()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  PERFORM tt.raise_domain_error('APPEND_ONLY',
    format('%s on tt.%s is not allowed: rows are append-only evidence', TG_OP, TG_TABLE_NAME));
  RETURN NULL;
END $$;
SELECT tt_migration.track_function('0299', 'tg_append_only()');

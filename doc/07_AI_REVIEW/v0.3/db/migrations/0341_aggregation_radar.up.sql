-- 0341 — Module Aggregation, Radar tin GĐ1 (ADR-0008 §2, §3, §6, §7, §9) · FR-AGG-001…005 · DRAFT chờ TL + DBA + PO + Legal duyệt
-- Hồ sơ thu thập 1–1 với tt.content_sources (A-2: phương án mặc định của ADR, chưa có quyết định TL/DBA), feed thuê theo
-- next_fetch_at/lease_until (§3), nhật ký thu thập, mục tin, cụm, hành động trên cụm, luật phân loại; cờ aggregation.radar (OFF).
--
-- Ràng buộc DB (§9): M2/M3 phải có giấy phép; ENABLED phải có người duyệt; đổi chế độ/giấy phép chỉ đi kèm bản ghi duyệt
-- (agg_source_approvals, chỉ thêm); tạm dừng/ngừng/bỏ qua cụm phải có lý do (REASON_REQUIRED); mục tin không đổi nguồn;
-- hồ sơ và feed không xóa được (chỉ RETIRED / tắt). Người thao tác: SET LOCAL tt.actor_id; lý do: SET LOCAL tt.reason.
-- Tách nhiệm vụ M2/M3 (người đề xuất không tự duyệt) kiểm ở cả service và trigger (SOD_VIOLATION).
-- Mọi bảng mới → không cần NOT VALID/VALIDATE (CLAUDE.md §3 chỉ áp cho CHECK trên bảng cũ).

SELECT tt_migration.require_table('content_sources');   -- 0340 (ADR-0007 §1) phải chạy trước
SELECT tt_migration.require_table('licenses');
SELECT tt_migration.require_column('sites', 'kind');     -- 0312
SELECT tt_migration.require_column('categories', 'site_key');
SELECT tt_migration.require_column('articles', 'id');
SELECT tt_migration.require_column('users', 'id');
SELECT tt_migration.require_column('feature_gates', 'production_default');   -- 0300

-- ---------------------------------------------------------------------------------
-- 1) Hồ sơ thu thập của nguồn (ADR-0008 §2)
-- ---------------------------------------------------------------------------------
SELECT tt_migration.track_new_table('0341', 'agg_source_profiles');
CREATE TABLE IF NOT EXISTS tt.agg_source_profiles (
  content_source_id  uuid PRIMARY KEY REFERENCES tt.content_sources(id),
  status             varchar(12) NOT NULL DEFAULT 'PROPOSED',
  usage_mode         varchar(2) NOT NULL DEFAULT 'M0',
  license_id         uuid REFERENCES tt.licenses(id),
  terms_evidence_ref varchar(500),
  trust_weight       smallint NOT NULL DEFAULT 3,
  language           varchar(2) NOT NULL,
  site_keys          varchar(31)[] NOT NULL,
  proposed_by        uuid NOT NULL REFERENCES tt.users(id),
  approved_by        uuid REFERENCES tt.users(id),
  approved_at        timestamptz,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  row_version        bigint NOT NULL DEFAULT 1,
  CONSTRAINT ck_agg_source_profiles_status CHECK (status IN ('PROPOSED', 'ENABLED', 'PAUSED', 'DEGRADED', 'RETIRED')),
  CONSTRAINT ck_agg_source_profiles_usage_mode CHECK (usage_mode IN ('M0', 'M1', 'M2', 'M3')),
  CONSTRAINT ck_agg_source_profiles_license_required CHECK (usage_mode IN ('M0', 'M1') OR license_id IS NOT NULL),
  CONSTRAINT ck_agg_source_profiles_trust_weight CHECK (trust_weight BETWEEN 1 AND 5),
  CONSTRAINT ck_agg_source_profiles_language CHECK (language ~ '^[a-z]{2}$'),
  CONSTRAINT ck_agg_source_profiles_site_keys CHECK (cardinality(site_keys) BETWEEN 1 AND 20 AND array_position(site_keys, NULL) IS NULL),
  CONSTRAINT ck_agg_source_profiles_approval_pair CHECK ((approved_by IS NULL) = (approved_at IS NULL)),
  CONSTRAINT ck_agg_source_profiles_enabled_requires_approval CHECK (status NOT IN ('ENABLED', 'DEGRADED') OR approved_at IS NOT NULL)
);
SELECT tt_migration.add_index('0341', 'agg_source_profiles', 'ix_agg_source_profiles_status',
  'ON tt.agg_source_profiles (status, content_source_id)');
SELECT tt_migration.add_index('0341', 'agg_source_profiles', 'ix_agg_source_profiles_site_keys',
  'ON tt.agg_source_profiles USING gin (site_keys)');

-- Lịch sử duyệt chế độ khai thác + giấy phép — chỉ thêm.
SELECT tt_migration.track_new_table('0341', 'agg_source_approvals');
CREATE TABLE IF NOT EXISTS tt.agg_source_approvals (
  id                 bigserial PRIMARY KEY,
  content_source_id  uuid NOT NULL REFERENCES tt.agg_source_profiles(content_source_id),
  usage_mode         varchar(2) NOT NULL,
  license_id         uuid REFERENCES tt.licenses(id),
  terms_evidence_ref varchar(500) NOT NULL,
  note               varchar(2000),
  approved_by        uuid NOT NULL REFERENCES tt.users(id),
  approved_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_agg_source_approvals_usage_mode CHECK (usage_mode IN ('M0', 'M1', 'M2', 'M3')),
  CONSTRAINT ck_agg_source_approvals_license_required CHECK (usage_mode IN ('M0', 'M1') OR license_id IS NOT NULL),
  CONSTRAINT ck_agg_source_approvals_terms_not_blank CHECK (length(btrim(terms_evidence_ref)) > 0)
);
SELECT tt_migration.add_index('0341', 'agg_source_approvals', 'ix_agg_source_approvals_source',
  'ON tt.agg_source_approvals (content_source_id, approved_at DESC, id DESC)');

-- Lịch sử trạng thái (đề xuất/bật/tạm dừng/tiếp tục/suy giảm/ngừng) — chỉ thêm, do trigger ghi.
SELECT tt_migration.track_new_table('0341', 'agg_source_status_history');
CREATE TABLE IF NOT EXISTS tt.agg_source_status_history (
  id                bigserial PRIMARY KEY,
  content_source_id uuid NOT NULL REFERENCES tt.agg_source_profiles(content_source_id),
  from_status       varchar(12),
  to_status         varchar(12) NOT NULL,
  actor_id          uuid REFERENCES tt.users(id),   -- NULL = hệ thống (worker)
  reason            varchar(2000),
  created_at        timestamptz NOT NULL DEFAULT now()
);
SELECT tt_migration.add_index('0341', 'agg_source_status_history', 'ix_agg_source_status_history_source',
  'ON tt.agg_source_status_history (content_source_id, created_at DESC)');

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['agg_source_approvals', 'agg_source_status_history'] LOOP
    PERFORM tt_migration.add_trigger('0341', t, 'trg_' || t || '_append_only',
      format('BEFORE UPDATE OR DELETE ON tt.%I FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()', t));
    PERFORM tt_migration.add_trigger('0341', t, 'trg_' || t || '_no_truncate',
      format('BEFORE TRUNCATE ON tt.%I FOR EACH STATEMENT EXECUTE FUNCTION tt.tg_append_only()', t));
  END LOOP;
END $$;

-- Mỗi site của hồ sơ phải là site public-web chưa retired (như tg_content_site_kind của 0312, cho mảng).
CREATE OR REPLACE FUNCTION tt.tg_agg_profile_site_keys()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_bad text;
BEGIN
  SELECT string_agg(k, ', ' ORDER BY k) INTO v_bad
    FROM unnest(NEW.site_keys) AS k
   WHERE NOT EXISTS (SELECT 1 FROM tt.sites s WHERE s.key = k AND s.kind = 'public-web' AND s.status <> 'retired');
  IF v_bad IS NOT NULL THEN
    PERFORM tt.raise_domain_error('VALIDATION_FAILED',
      format('siteKeys %s: an aggregation source can only feed active/planned public-web sites', v_bad));
  END IF;
  IF cardinality(NEW.site_keys) <> (SELECT count(DISTINCT k) FROM unnest(NEW.site_keys) AS k) THEN
    PERFORM tt.raise_domain_error('VALIDATION_FAILED', 'siteKeys must be unique');
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0341', 'tg_agg_profile_site_keys()');
SELECT tt_migration.add_trigger('0341', 'agg_source_profiles', 'trg_agg_source_profiles_site_keys',
  'BEFORE INSERT OR UPDATE OF site_keys ON tt.agg_source_profiles FOR EACH ROW EXECUTE FUNCTION tt.tg_agg_profile_site_keys()');

-- Bảo vệ hồ sơ: tạo mới chỉ ở PROPOSED/M0 chưa duyệt; RETIRED là trạng thái cuối; chế độ khai thác/giấy phép/người duyệt
-- chỉ đổi cùng một bản ghi duyệt khớp (thao tác approval), hoặc xóa người duyệt khi gửi lại duyệt (đổi feed, BR-01).
CREATE OR REPLACE FUNCTION tt.tg_agg_source_profiles_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.status <> 'PROPOSED' OR NEW.usage_mode <> 'M0' OR NEW.license_id IS NOT NULL OR NEW.approved_at IS NOT NULL
       OR NEW.terms_evidence_ref IS NOT NULL THEN
      PERFORM tt.raise_domain_error('STATE_CONFLICT', 'A new aggregation profile starts PROPOSED with usage mode M0 and no approval');
    END IF;
    RETURN NEW;
  END IF;

  IF OLD.status = 'RETIRED' THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('Aggregation source %s is retired and cannot change', OLD.content_source_id));
  END IF;
  IF NEW.proposed_by IS DISTINCT FROM OLD.proposed_by OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', 'proposed_by/created_at of an aggregation profile cannot change');
  END IF;

  IF (NEW.usage_mode, NEW.license_id, NEW.terms_evidence_ref, NEW.approved_by, NEW.approved_at)
     IS DISTINCT FROM (OLD.usage_mode, OLD.license_id, OLD.terms_evidence_ref, OLD.approved_by, OLD.approved_at) THEN
    IF NEW.approved_at IS NULL THEN
      -- Gửi lại duyệt: chỉ được xóa người duyệt, giữ nguyên chế độ/giấy phép đã duyệt gần nhất.
      IF (NEW.usage_mode, NEW.license_id, NEW.terms_evidence_ref) IS DISTINCT FROM (OLD.usage_mode, OLD.license_id, OLD.terms_evidence_ref) THEN
        PERFORM tt.raise_domain_error('STATE_CONFLICT', 'Usage mode and licence change only through the approval operation');
      END IF;
    ELSIF NOT EXISTS (
      SELECT 1 FROM tt.agg_source_approvals a
       WHERE a.content_source_id = NEW.content_source_id AND a.usage_mode = NEW.usage_mode
         AND a.license_id IS NOT DISTINCT FROM NEW.license_id AND a.terms_evidence_ref = NEW.terms_evidence_ref
         AND a.approved_by = NEW.approved_by AND a.approved_at = NEW.approved_at) THEN
      PERFORM tt.raise_domain_error('STATE_CONFLICT', 'Usage mode and licence change only through the approval operation');
    END IF;
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0341', 'tg_agg_source_profiles_guard()');
SELECT tt_migration.add_trigger('0341', 'agg_source_profiles', 'trg_agg_source_profiles_guard',
  'BEFORE INSERT OR UPDATE ON tt.agg_source_profiles FOR EACH ROW EXECUTE FUNCTION tt.tg_agg_source_profiles_guard()');
SELECT tt_migration.add_trigger('0341', 'agg_source_profiles', 'trg_agg_source_profiles_touch',
  'BEFORE UPDATE ON tt.agg_source_profiles FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');
-- Không xóa hồ sơ (BR-07): chỉ chuyển RETIRED.
SELECT tt_migration.add_trigger('0341', 'agg_source_profiles', 'trg_agg_source_profiles_no_delete',
  'BEFORE DELETE ON tt.agg_source_profiles FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');

-- Ghi lịch sử trạng thái. Tạm dừng/ngừng bắt buộc lý do (SET LOCAL tt.reason) → REASON_REQUIRED.
CREATE OR REPLACE FUNCTION tt.tg_agg_source_status_history()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_reason text := NULLIF(btrim(current_setting('tt.reason', true)), '');
BEGIN
  IF NEW.status IN ('PAUSED', 'RETIRED') AND (v_reason IS NULL OR length(v_reason) < 5) THEN
    PERFORM tt.raise_domain_error('REASON_REQUIRED',
      format('Moving aggregation source %s to %s requires a reason of at least 5 characters', NEW.content_source_id, NEW.status));
  END IF;
  INSERT INTO tt.agg_source_status_history (content_source_id, from_status, to_status, actor_id, reason)
  VALUES (NEW.content_source_id, CASE WHEN TG_OP = 'UPDATE' THEN OLD.status END, NEW.status,
          NULLIF(current_setting('tt.actor_id', true), '')::uuid, v_reason);
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0341', 'tg_agg_source_status_history()');
SELECT tt_migration.add_trigger('0341', 'agg_source_profiles', 'trg_agg_source_profiles_status_created',
  'AFTER INSERT ON tt.agg_source_profiles FOR EACH ROW EXECUTE FUNCTION tt.tg_agg_source_status_history()');
SELECT tt_migration.add_trigger('0341', 'agg_source_profiles', 'trg_agg_source_profiles_status_history',
  'AFTER UPDATE OF status ON tt.agg_source_profiles FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status) '
  || 'EXECUTE FUNCTION tt.tg_agg_source_status_history()');

-- Bản ghi duyệt: hồ sơ chưa RETIRED; M2/M3 cần giấy phép đang hiệu lực (VALIDATION_FAILED) và người duyệt khác người đề xuất
-- (SOD_VIOLATION). M0/M1 không cần tách nhiệm vụ (ADR-0008 §8).
CREATE OR REPLACE FUNCTION tt.tg_agg_source_approvals_check()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_status   text;
  v_proposer uuid;
BEGIN
  SELECT status, proposed_by INTO v_status, v_proposer FROM tt.agg_source_profiles WHERE content_source_id = NEW.content_source_id;
  IF v_status = 'RETIRED' THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('Aggregation source %s is retired', NEW.content_source_id));
  END IF;
  IF NEW.usage_mode IN ('M2', 'M3') THEN
    IF NOT tt.license_active(NEW.license_id, NEW.approved_at) THEN
      PERFORM tt.raise_domain_error('VALIDATION_FAILED',
        format('Usage mode %s needs an enabled licence valid at approval time (licence %s)', NEW.usage_mode, COALESCE(NEW.license_id::text, 'none')));
    END IF;
    IF NEW.approved_by = v_proposer THEN
      PERFORM tt.raise_domain_error('SOD_VIOLATION', 'The person who proposed the source cannot approve usage mode M2/M3 for it');
    END IF;
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0341', 'tg_agg_source_approvals_check()');
SELECT tt_migration.add_trigger('0341', 'agg_source_approvals', 'trg_agg_source_approvals_check',
  'BEFORE INSERT ON tt.agg_source_approvals FOR EACH ROW EXECUTE FUNCTION tt.tg_agg_source_approvals_check()');

-- ---------------------------------------------------------------------------------
-- 2) Feed (ADR-0008 §3): thuê qua next_fetch_at + lease_until, FOR UPDATE SKIP LOCKED ở worker
-- ---------------------------------------------------------------------------------
SELECT tt_migration.track_new_table('0341', 'agg_feeds');
CREATE TABLE IF NOT EXISTS tt.agg_feeds (
  id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  content_source_id    uuid NOT NULL REFERENCES tt.agg_source_profiles(content_source_id),
  url                  varchar(2048) NOT NULL,
  format               varchar(10) NOT NULL DEFAULT 'AUTO',
  is_enabled           boolean NOT NULL DEFAULT true,
  schedule_minutes     integer NOT NULL DEFAULT 15,
  category_mapping     jsonb NOT NULL DEFAULT '[]'::jsonb,
  etag                 varchar(500),
  last_modified        varchar(100),
  -- DISABLED của AggregationFeedHealth suy ra từ is_enabled = false, không lưu.
  health               varchar(20) NOT NULL DEFAULT 'NEVER_FETCHED',
  last_fetched_at      timestamptz,
  last_success_at      timestamptz,
  next_fetch_at        timestamptz,
  lease_until          timestamptz,
  consecutive_failures integer NOT NULL DEFAULT 0,
  last_failure         varchar(32),
  created_by           uuid REFERENCES tt.users(id),
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  row_version          bigint NOT NULL DEFAULT 1,
  CONSTRAINT uq_agg_feeds_url UNIQUE (url),
  CONSTRAINT ck_agg_feeds_url CHECK (url ~ '^https://[^[:space:]]+$'),
  CONSTRAINT ck_agg_feeds_format CHECK (format IN ('AUTO', 'RSS2', 'RSS1', 'ATOM', 'JSONFEED')),
  CONSTRAINT ck_agg_feeds_schedule CHECK (schedule_minutes BETWEEN 5 AND 1440),
  CONSTRAINT ck_agg_feeds_category_mapping CHECK (jsonb_typeof(category_mapping) = 'array' AND jsonb_array_length(category_mapping) <= 100),
  CONSTRAINT ck_agg_feeds_health CHECK (health IN ('NEVER_FETCHED', 'OK', 'DEGRADED', 'FAILING', 'BLOCKED_BY_ROBOTS')),
  CONSTRAINT ck_agg_feeds_failures CHECK (consecutive_failures >= 0),
  CONSTRAINT ck_agg_feeds_last_failure CHECK (last_failure IS NULL OR last_failure IN ('TIMEOUT', 'DNS', 'TLS', 'HTTP_STATUS', 'TOO_LARGE',
    'UNSUPPORTED_CONTENT_TYPE', 'PARSE_ERROR', 'ROBOTS_DISALLOWED', 'TOO_MANY_REDIRECTS', 'RATE_LIMITED_BY_SOURCE'))
);
SELECT tt_migration.add_index('0341', 'agg_feeds', 'ix_agg_feeds_source', 'ON tt.agg_feeds (content_source_id, created_at, id)');
SELECT tt_migration.add_index('0341', 'agg_feeds', 'ix_agg_feeds_due',
  'ON tt.agg_feeds (next_fetch_at) WHERE is_enabled AND next_fetch_at IS NOT NULL');
SELECT tt_migration.add_trigger('0341', 'agg_feeds', 'trg_agg_feeds_touch',
  'BEFORE UPDATE ON tt.agg_feeds FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');
SELECT tt_migration.add_trigger('0341', 'agg_feeds', 'trg_agg_feeds_no_delete',
  'BEFORE DELETE ON tt.agg_feeds FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');

-- Nhật ký mỗi lần thu thập; job dọn giữ 30 ngày (ADR-0008 §9) → không append-only.
SELECT tt_migration.track_new_table('0341', 'agg_fetch_log');
CREATE TABLE IF NOT EXISTS tt.agg_fetch_log (
  id            bigserial PRIMARY KEY,
  feed_id       uuid NOT NULL REFERENCES tt.agg_feeds(id),
  started_at    timestamptz NOT NULL,
  finished_at   timestamptz,
  outcome       varchar(12) NOT NULL,
  http_status   smallint,
  failure       varchar(32),
  bytes_read    integer,
  items_seen    integer NOT NULL DEFAULT 0,
  items_new     integer NOT NULL DEFAULT 0,
  items_updated integer NOT NULL DEFAULT 0,
  CONSTRAINT ck_agg_fetch_log_outcome CHECK (outcome IN ('OK', 'NOT_MODIFIED', 'FAILED')),
  CONSTRAINT ck_agg_fetch_log_failure CHECK ((outcome = 'FAILED') = (failure IS NOT NULL))
);
SELECT tt_migration.add_index('0341', 'agg_fetch_log', 'ix_agg_fetch_log_feed', 'ON tt.agg_fetch_log (feed_id, started_at DESC)');
SELECT tt_migration.add_index('0341', 'agg_fetch_log', 'ix_agg_fetch_log_retention', 'ON tt.agg_fetch_log (started_at)');

-- ---------------------------------------------------------------------------------
-- 3) Cụm, mục tin, hành động trên cụm, luật phân loại (ADR-0008 §6, §7) — dùng ở các lát sau (worker, Radar)
-- ---------------------------------------------------------------------------------
SELECT tt_migration.track_new_table('0341', 'agg_clusters');
CREATE TABLE IF NOT EXISTS tt.agg_clusters (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  site_key               varchar(31) NOT NULL REFERENCES tt.sites(key),
  title                  varchar(1000) NOT NULL,
  representative_item_id uuid,   -- khóa ngoại tới agg_items thêm sau khi tạo bảng mục tin
  primary_category_id    uuid REFERENCES tt.categories(id),
  category_needs_review  boolean NOT NULL DEFAULT false,
  score                  numeric(7,2) NOT NULL DEFAULT 0,
  status                 varchar(10) NOT NULL DEFAULT 'OPEN',
  labels                 varchar(20)[] NOT NULL DEFAULT '{}',
  assignee_id            uuid REFERENCES tt.users(id),
  covered_article_id     uuid REFERENCES tt.articles(id),
  first_seen_at          timestamptz NOT NULL DEFAULT now(),
  last_item_at           timestamptz NOT NULL DEFAULT now(),
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  row_version            bigint NOT NULL DEFAULT 1,
  CONSTRAINT ck_agg_clusters_status CHECK (status IN ('OPEN', 'WATCHING', 'DISMISSED', 'COVERED', 'CLOSED')),
  CONSTRAINT ck_agg_clusters_labels CHECK (labels <@ ARRAY['SINGLE_SOURCE', 'SENSITIVE', 'SOURCE_UPDATED', 'NEW_SINCE_COVERED']::varchar(20)[]),
  CONSTRAINT ck_agg_clusters_covered CHECK (status <> 'COVERED' OR covered_article_id IS NOT NULL),
  CONSTRAINT ck_agg_clusters_title_not_blank CHECK (length(btrim(title)) > 0)
);
SELECT tt_migration.add_index('0341', 'agg_clusters', 'ix_agg_clusters_radar', 'ON tt.agg_clusters (site_key, status, score DESC, id)');
SELECT tt_migration.add_index('0341', 'agg_clusters', 'ix_agg_clusters_recent', 'ON tt.agg_clusters (site_key, last_item_at DESC)');
SELECT tt_migration.add_trigger('0341', 'agg_clusters', 'trg_agg_clusters_site_kind',
  'BEFORE INSERT OR UPDATE OF site_key ON tt.agg_clusters FOR EACH ROW EXECUTE FUNCTION tt.tg_content_site_kind(''site_key'')');
SELECT tt_migration.add_trigger('0341', 'agg_clusters', 'trg_agg_clusters_touch',
  'BEFORE UPDATE ON tt.agg_clusters FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');

-- Mục tin (§6). Khóa khử trùng BR-10: (nguồn, guid), rồi (nguồn, url_hash); url_hash = sha256 URL chuẩn hóa, hoặc
-- sha256(tiêu đề chuẩn hóa + ngày) khi mục không có link. GĐ1 không lưu toàn văn (chỉ tóm tắt feed, văn bản thuần).
SELECT tt_migration.track_new_table('0341', 'agg_items');
CREATE TABLE IF NOT EXISTS tt.agg_items (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  content_source_id   uuid NOT NULL REFERENCES tt.agg_source_profiles(content_source_id),
  feed_id             uuid NOT NULL REFERENCES tt.agg_feeds(id),
  guid                varchar(1000),
  url                 varchar(2048),
  url_hash            char(64) NOT NULL,
  title               varchar(1000) NOT NULL,
  summary             varchar(5000),
  author              varchar(300),
  feed_categories     varchar(200)[] NOT NULL DEFAULT '{}',
  language            varchar(2),
  published_at        timestamptz,
  first_seen_at       timestamptz NOT NULL DEFAULT now(),
  time_reliable       boolean NOT NULL DEFAULT true,
  status              varchar(12) NOT NULL DEFAULT 'NEW',
  site_key            varchar(31) REFERENCES tt.sites(key),
  category_id         uuid REFERENCES tt.categories(id),
  category_confidence numeric(4,3),
  needs_review        boolean NOT NULL DEFAULT false,
  sensitive           boolean NOT NULL DEFAULT false,
  content_hash        char(64) NOT NULL,
  version             integer NOT NULL DEFAULT 1,
  source_updated_at   timestamptz,
  cluster_id          uuid REFERENCES tt.agg_clusters(id),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_agg_items_status CHECK (status IN ('NEW', 'CLASSIFIED', 'CLUSTERED', 'DUPLICATE', 'OUT_OF_SCOPE')),
  CONSTRAINT ck_agg_items_url CHECK (url IS NULL OR url ~ '^https?://[^[:space:]]+$'),
  CONSTRAINT ck_agg_items_hashes CHECK (url_hash ~ '^[0-9a-f]{64}$' AND content_hash ~ '^[0-9a-f]{64}$'),
  CONSTRAINT ck_agg_items_title_not_blank CHECK (length(btrim(title)) > 0),
  CONSTRAINT ck_agg_items_confidence CHECK (category_confidence IS NULL OR category_confidence BETWEEN 0 AND 1),
  CONSTRAINT ck_agg_items_version CHECK (version >= 1),
  CONSTRAINT ck_agg_items_clustered CHECK (status <> 'CLUSTERED' OR cluster_id IS NOT NULL)
);
-- Hai khóa khử trùng UNIQUE (BR-10). add_index chỉ tạo index thường → tạo trực tiếp và tự ghi ledger.
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT * FROM (VALUES
    ('ux_agg_items_source_guid', 'ON tt.agg_items (content_source_id, guid) WHERE guid IS NOT NULL'),
    ('ux_agg_items_source_url_hash', 'ON tt.agg_items (content_source_id, url_hash)')) AS v(name, def)
  LOOP
    IF to_regclass(format('tt.%I', r.name)) IS NULL THEN
      EXECUTE format('CREATE UNIQUE INDEX %I %s', r.name, r.def);
      PERFORM tt_migration.record('0341', 'index', 'agg_items', r.name);
    END IF;
  END LOOP;
END $$;
SELECT tt_migration.add_index('0341', 'agg_items', 'ix_agg_items_recent', 'ON tt.agg_items (first_seen_at DESC, id DESC)');
SELECT tt_migration.add_index('0341', 'agg_items', 'ix_agg_items_source_recent', 'ON tt.agg_items (content_source_id, first_seen_at DESC)');
SELECT tt_migration.add_index('0341', 'agg_items', 'ix_agg_items_cluster', 'ON tt.agg_items (cluster_id) WHERE cluster_id IS NOT NULL');
SELECT tt_migration.add_trigger('0341', 'agg_items', 'trg_agg_items_site_kind',
  'BEFORE INSERT OR UPDATE OF site_key ON tt.agg_items FOR EACH ROW WHEN (NEW.site_key IS NOT NULL) EXECUTE FUNCTION tt.tg_content_site_kind(''site_key'')');
SELECT tt_migration.add_trigger('0341', 'agg_items', 'trg_agg_items_touch',
  'BEFORE UPDATE ON tt.agg_items FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_updated_at()');

-- Mục tin không đổi nguồn/feed sau khi tạo (ADR-0008 §9); feed cũng không đổi nguồn.
CREATE OR REPLACE FUNCTION tt.tg_agg_source_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.content_source_id IS DISTINCT FROM OLD.content_source_id
     OR (TG_TABLE_NAME = 'agg_items' AND to_jsonb(NEW) ->> 'feed_id' IS DISTINCT FROM to_jsonb(OLD) ->> 'feed_id') THEN
    PERFORM tt.raise_domain_error('STATE_CONFLICT', format('tt.%s %s cannot move to another source or feed', TG_TABLE_NAME, OLD.id));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0341', 'tg_agg_source_immutable()');
SELECT tt_migration.add_trigger('0341', 'agg_items', 'trg_agg_items_source_immutable',
  'BEFORE UPDATE OF content_source_id, feed_id ON tt.agg_items FOR EACH ROW EXECUTE FUNCTION tt.tg_agg_source_immutable()');
SELECT tt_migration.add_trigger('0341', 'agg_feeds', 'trg_agg_feeds_source_immutable',
  'BEFORE UPDATE OF content_source_id ON tt.agg_feeds FOR EACH ROW EXECUTE FUNCTION tt.tg_agg_source_immutable()');

SELECT tt_migration.add_constraint('0341', 'agg_clusters', 'fk_agg_clusters_representative_item',
  'FOREIGN KEY (representative_item_id) REFERENCES tt.agg_items(id)');

-- Hành động trên cụm (bỏ qua/theo dõi/mở lại/nhận việc/đã đưa tin/tách/gộp) — chỉ thêm. Bỏ qua cần lý do (REASON_REQUIRED).
SELECT tt_migration.track_new_table('0341', 'agg_cluster_actions');
CREATE TABLE IF NOT EXISTS tt.agg_cluster_actions (
  id                 bigserial PRIMARY KEY,
  cluster_id         uuid NOT NULL REFERENCES tt.agg_clusters(id),
  action             varchar(8) NOT NULL,
  actor_id           uuid NOT NULL REFERENCES tt.users(id),
  reason             varchar(2000),
  related_cluster_id uuid REFERENCES tt.agg_clusters(id),
  article_id         uuid REFERENCES tt.articles(id),
  created_at         timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT ck_agg_cluster_actions_action CHECK (action IN ('DISMISS', 'WATCH', 'REOPEN', 'CLAIM', 'COVER', 'SPLIT', 'MERGE')),
  CONSTRAINT ck_agg_cluster_actions_cover CHECK (action <> 'COVER' OR article_id IS NOT NULL),
  CONSTRAINT ck_agg_cluster_actions_related CHECK (action NOT IN ('SPLIT', 'MERGE') OR related_cluster_id IS NOT NULL)
);
SELECT tt_migration.add_index('0341', 'agg_cluster_actions', 'ix_agg_cluster_actions_cluster',
  'ON tt.agg_cluster_actions (cluster_id, created_at DESC)');
SELECT tt_migration.add_trigger('0341', 'agg_cluster_actions', 'trg_agg_cluster_actions_append_only',
  'BEFORE UPDATE OR DELETE ON tt.agg_cluster_actions FOR EACH ROW EXECUTE FUNCTION tt.tg_append_only()');
SELECT tt_migration.add_trigger('0341', 'agg_cluster_actions', 'trg_agg_cluster_actions_no_truncate',
  'BEFORE TRUNCATE ON tt.agg_cluster_actions FOR EACH STATEMENT EXECUTE FUNCTION tt.tg_append_only()');

CREATE OR REPLACE FUNCTION tt.tg_agg_cluster_actions_reason()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.action = 'DISMISS' AND length(btrim(COALESCE(NEW.reason, ''))) < 5 THEN
    PERFORM tt.raise_domain_error('REASON_REQUIRED', format('Dismissing cluster %s requires a reason of at least 5 characters', NEW.cluster_id));
  END IF;
  RETURN NEW;
END $$;
SELECT tt_migration.track_function('0341', 'tg_agg_cluster_actions_reason()');
SELECT tt_migration.add_trigger('0341', 'agg_cluster_actions', 'trg_agg_cluster_actions_reason',
  'BEFORE INSERT ON tt.agg_cluster_actions FOR EACH ROW EXECUTE FUNCTION tt.tg_agg_cluster_actions_reason()');

-- Luật phân loại theo chuyên mục (BR-30 tầng 2). Site suy ra từ tt.categories.site_key. Chưa có dòng = danh sách rỗng, rowVersion 1.
SELECT tt_migration.track_new_table('0341', 'agg_category_rules');
CREATE TABLE IF NOT EXISTS tt.agg_category_rules (
  category_id      uuid PRIMARY KEY REFERENCES tt.categories(id),
  include_keywords varchar(100)[] NOT NULL DEFAULT '{}',
  exclude_keywords varchar(100)[] NOT NULL DEFAULT '{}',
  entity_keywords  varchar(100)[] NOT NULL DEFAULT '{}',
  updated_by       uuid REFERENCES tt.users(id),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  row_version      bigint NOT NULL DEFAULT 1,
  CONSTRAINT ck_agg_category_rules_sizes CHECK (cardinality(include_keywords) <= 200 AND cardinality(exclude_keywords) <= 200
    AND cardinality(entity_keywords) <= 200)
);
SELECT tt_migration.add_trigger('0341', 'agg_category_rules', 'trg_agg_category_rules_touch',
  'BEFORE UPDATE ON tt.agg_category_rules FOR EACH ROW EXECUTE FUNCTION tt.tg_touch_row()');

-- ---------------------------------------------------------------------------------
-- 4) Cờ aggregation.radar (registry 0300): OFF, P2 cho tới khi PO xếp hạng (A-5). Bật cần hồ sơ duyệt (CHECK của 0300).
-- ---------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS tt_migration.flag_added_0341 (key varchar(80) PRIMARY KEY);
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM tt.feature_gates WHERE key = 'aggregation.radar') THEN
    INSERT INTO tt.feature_gates (key, enabled, scope, production_default, description)
    VALUES ('aggregation.radar', false, 'P2', false,
            'News radar phase 1: RSS collection, rule-based classification and clustering for editors (ADR-0008). No AI, nothing published.');
    INSERT INTO tt_migration.flag_added_0341 (key) VALUES ('aggregation.radar');
  END IF;
END $$;

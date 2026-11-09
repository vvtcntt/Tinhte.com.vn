-- =====================================================================================
-- STUB BASELINE "SCHEMA 04" — CHỈ DÙNG ĐỂ CHẠY THỬ MIGRATION v0.3. KHÔNG PHẢI SCHEMA THẬT.
-- =====================================================================================
-- File 04_DATABASE_SCHEMA_REFERENCE_v0.1.sql không có trong repo. Stub này mô tả TỐI THIỂU những
-- bảng/cột mà migration v0.3 giả định đã tồn tại (suy ra từ 07_DB_ADS_LITE_REFERENCE_v0.2.sql và
-- hợp đồng API). DBA phải đối chiếu từng giả định dưới đây với schema 04 thật trước khi chạy.
--
-- Giả định (A-xx được nhắc lại trong README):
--   A-01 schema `tt`; khóa chính kiểu uuid; có gen_random_uuid() (PostgreSQL ≥ 13).
--   A-02 tt.users(id)                          — người dùng/staff.
--   A-03 tt.media_assets(id)                   — tài sản media.
--   A-04 tt.feature_gates(key PK, enabled)     — cờ tính năng; tên khóa cũ (ví dụ community.comments).
--   A-05 tt.articles(id, slug UNIQUE, status, public_revision_id) và tt.article_revisions(id, article_id).
--   A-06 tt.videos(id, slug, status, public_revision_id) và tt.video_revisions(id, video_id).
--   A-07 tt.comments(id, video_id, ...)        — bình luận video (P0*).
--   A-08 tt.moderation_cases(id uuid, entity_type, entity_id, status, created_at).
--   A-09 tt.categories(id), tt.topics(id).
-- =====================================================================================

CREATE SCHEMA tt;

CREATE TABLE tt.users (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  display_name varchar(80) NOT NULL,
  status       varchar(16) NOT NULL DEFAULT 'ACTIVE',
  created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tt.media_assets (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  status     varchar(16) NOT NULL DEFAULT 'UPLOADING',
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tt.feature_gates (
  key     varchar(80) PRIMARY KEY,
  enabled boolean NOT NULL DEFAULT false
);
-- Khóa cũ theo tên gate của API v0.2 (giả định schema 04 dùng các tên này).
INSERT INTO tt.feature_gates (key, enabled) VALUES
  ('community.comments', false),
  ('community.member', false),
  ('member.bookmark', false),
  ('ai.assistance', false),
  ('legacy.unmapped_flag', false);   -- khóa không có trong registry: migration phải giữ nguyên + NOTICE

CREATE TABLE tt.categories (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  slug       varchar(120) NOT NULL UNIQUE,
  name       varchar(120) NOT NULL,
  is_enabled boolean NOT NULL DEFAULT true
);

CREATE TABLE tt.topics (
  id   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  slug varchar(120) NOT NULL UNIQUE,
  name varchar(120) NOT NULL
);

CREATE TABLE tt.articles (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  slug               varchar(250) NOT NULL UNIQUE,
  status             varchar(20) NOT NULL DEFAULT 'DRAFT',
  category_id        uuid REFERENCES tt.categories(id),
  public_revision_id uuid,
  created_at         timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tt.article_revisions (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  article_id uuid NOT NULL REFERENCES tt.articles(id),
  number     integer NOT NULL CHECK (number > 0),
  title      varchar(300) NOT NULL,
  body       jsonb,
  author_id  uuid REFERENCES tt.users(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (article_id, number)
);
ALTER TABLE tt.articles
  ADD CONSTRAINT fk_articles_public_revision FOREIGN KEY (public_revision_id) REFERENCES tt.article_revisions(id);

CREATE TABLE tt.videos (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  slug               varchar(250) NOT NULL UNIQUE,
  status             varchar(20) NOT NULL DEFAULT 'DRAFT',
  media_id           uuid REFERENCES tt.media_assets(id),
  public_revision_id uuid,
  created_at         timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tt.video_revisions (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  video_id   uuid NOT NULL REFERENCES tt.videos(id),
  number     integer NOT NULL CHECK (number > 0),
  title      varchar(300) NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (video_id, number)
);
ALTER TABLE tt.videos
  ADD CONSTRAINT fk_videos_public_revision FOREIGN KEY (public_revision_id) REFERENCES tt.video_revisions(id);

CREATE TABLE tt.comments (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  video_id   uuid NOT NULL REFERENCES tt.videos(id),
  parent_id  uuid REFERENCES tt.comments(id),
  author_id  uuid NOT NULL REFERENCES tt.users(id),
  text       varchar(5000) NOT NULL,
  status     varchar(20) NOT NULL DEFAULT 'PENDING_REVIEW',
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tt.moderation_cases (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  entity_type varchar(16) NOT NULL,
  entity_id   uuid NOT NULL,
  reason_code varchar(40),
  status      varchar(20) NOT NULL DEFAULT 'OPEN',
  created_at  timestamptz NOT NULL DEFAULT now()
);

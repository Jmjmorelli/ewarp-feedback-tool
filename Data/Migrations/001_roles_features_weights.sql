-- Users and roles, manual features and feedback, feature and group weights.
-- The feedback_mentions view below is replaced by Migrations/002 (it leaves out excluded surveys); run 002 after this.
-- Run once as the database owner before deploying the code that uses it (the app's user can't create tables):
--     psql "$OWNER_DATABASE_URL" -v app_user=<the app's database user> -f Data/Migrations/001_roles_features_weights.sql
-- Safe to run again: every statement checks before it creates.

begin;

-- People who can sign in. role: admin = can change things, viewer = read only.
create table if not exists users (
  id text primary key,                    -- 'usr_' + 32 hex
  email text not null unique,             -- stored lower-case
  name text not null,
  password_hash text not null,            -- ASP.NET Core PasswordHasher (PBKDF2)
  role text not null check (role in ('admin', 'viewer')),
  created_at timestamptz not null default now()
);

-- Where a feature came from (the classifier, or an admin adding it by hand) and how much it counts in vendor scores.
alter table features add column if not exists source text not null default 'survey';
alter table features add column if not exists weight numeric not null default 1;

-- Likes, dislikes and wants an admin enters by hand. They count everywhere a survey mention does.
create table if not exists feedback_entries (
  id text primary key,                    -- 'fbk_' + 12 hex
  project_id text not null,
  feature_id text not null,
  vendor_id text,                         -- null = not about one vendor
  kind text not null check (kind in ('like', 'dislike', 'want')),
  respondent_role text not null,          -- a key from RespondentGroups.All
  respondent_group text not null,         -- pm / sponsor / other, the frontend's groups
  note text not null default '',
  created_by text,
  created_at timestamptz not null default now()
);
create index if not exists feedback_entries_feature on feedback_entries (feature_id);
create index if not exists feedback_entries_project on feedback_entries (project_id);

-- How much each respondent group counts in vendor scores, per project. A group with no row counts 1.
create table if not exists group_weights (
  project_id text not null,
  role_key text not null,
  weight numeric not null default 1,
  primary key (project_id, role_key)
);

-- Vendor/feature pairs an admin took off, so the classifier doesn't add them back from old likes and dislikes.
create table if not exists vendor_offering_removals (
  vendor_id text not null,
  feature_id text not null,
  primary key (vendor_id, feature_id)
);

-- Every like / dislike / want: survey mentions plus manual entries, with who said it and about which vendor.
create or replace view feedback_mentions as
  select m.id, r.project_id, m.feature_id, m.kind, r.vendor_id,
         r.respondent_group, coalesce(r.respondent_role, r.respondent_group) as respondent_role,
         r.submitted_at as occurred_at, m.response_id, 'survey'::text as source
  from mentions m join responses r on r.id = m.response_id
  union all
  select e.id, e.project_id, e.feature_id, e.kind, e.vendor_id,
         e.respondent_group, e.respondent_role,
         e.created_at, null::text, 'manual'::text
  from feedback_entries e;

grant select, insert, update, delete on users, feedback_entries, group_weights, vendor_offering_removals to :"app_user";
grant select on feedback_mentions to :"app_user";

commit;

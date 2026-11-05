-- Response-level exclusion. Keep raw submissions, but remove only selected responses from analysis.
-- Run as the database owner:
--   psql "$OWNER_DATABASE_URL" -v app_user=<the app's database user> -f Data/Migrations/002_response_analysis_inclusion.sql

begin;

alter table responses add column if not exists included_in_analysis boolean not null default true;
alter table responses add column if not exists exclusion_reason text;
alter table responses add column if not exists excluded_at timestamptz;
alter table responses add column if not exists excluded_by_user_id text references users(id);

create index if not exists ix_responses_project_included_submitted
  on responses (project_id, included_in_analysis, submitted_at desc);

-- Manual feedback has no response and is always included.
create or replace view feedback_mentions as
  select m.id, r.project_id, m.feature_id, m.kind, r.vendor_id,
         r.respondent_group, coalesce(r.respondent_role, r.respondent_group) as respondent_role,
         r.submitted_at as occurred_at, m.response_id, 'survey'::text as source
  from mentions m
  join responses r on r.id = m.response_id and r.included_in_analysis
  union all
  select e.id, e.project_id, e.feature_id, e.kind, e.vendor_id,
         e.respondent_group, e.respondent_role,
         e.created_at, null::text, 'manual'::text
  from feedback_entries e;

grant update (included_in_analysis, exclusion_reason, excluded_at, excluded_by_user_id) on responses to :"app_user";
grant select on feedback_mentions to :"app_user";

commit;

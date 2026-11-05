-- Excluding a survey from analysis (Surveys page → "Exclude from analysis", PATCH /surveys/{id}).
-- Records the schema the code since a8805c5 expects, which was applied to the live database by hand.
-- Safe to run again: every statement checks before it creates, and the view matches the one the code relies on.
--     psql "$OWNER_DATABASE_URL" -v app_user=<the app's database user> -f Data/Migrations/002_survey_exclusion.sql
-- Run after 001. Re-running 001 alone would put back the unfiltered view, so run 002 after it again.

begin;

alter table surveys add column if not exists included_in_analysis boolean not null default true;
alter table surveys add column if not exists exclusion_reason text;
alter table surveys add column if not exists excluded_at timestamptz;
alter table surveys add column if not exists excluded_by_user_id text;

-- Every like / dislike / want, as in 001, minus survey mentions from excluded surveys. Counts, gaps, vendor pages
-- and scores all read this view, so excluding a survey takes its answers out of all of them. Manual entries stay.
create or replace view feedback_mentions as
  select m.id, r.project_id, m.feature_id, m.kind, r.vendor_id,
         r.respondent_group, coalesce(r.respondent_role, r.respondent_group) as respondent_role,
         r.submitted_at as occurred_at, m.response_id, 'survey'::text as source
  from mentions m join responses r on r.id = m.response_id join surveys s on s.id = r.survey_id
  where s.included_in_analysis
  union all
  select e.id, e.project_id, e.feature_id, e.kind, e.vendor_id,
         e.respondent_group, e.respondent_role,
         e.created_at, null::text, 'manual'::text
  from feedback_entries e;

grant select, update on surveys to :"app_user";
grant select on feedback_mentions to :"app_user";

commit;

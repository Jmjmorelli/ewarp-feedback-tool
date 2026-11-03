-- Responses an admin asked the AI to read again (the "Reread with AI" buttons).
-- The classifier keeps its own list of what it has read, so without this it never looks at a response twice.
-- It picks these up on its next pass and deletes the row once it has read the response.
-- Run once as the database owner before deploying the code that uses it:
--     psql "$OWNER_DATABASE_URL" -v app_user=<the app's database user> -f Data/Migrations/002_response_rereads.sql
-- Safe to run again.

begin;

create table if not exists response_rereads (
  response_id text primary key references responses(id) on delete cascade,
  requested_at timestamptz not null default now(),
  requested_by text
);

grant select, insert, update, delete on response_rereads to :"app_user";

commit;

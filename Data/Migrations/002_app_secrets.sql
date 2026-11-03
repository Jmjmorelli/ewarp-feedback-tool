-- Where the backend keeps its sign-in signing key (created on its first start). Run once as the database owner,
-- e.g. in pgAdmin's Query Tool. Replace ewarp_app with the backend's database user.

create table if not exists app_secrets (
  key text primary key,
  value text not null
);

grant select, insert on app_secrets to ewarp_app;

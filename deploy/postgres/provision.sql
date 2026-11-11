-- Subscribed: database provisioning TEMPLATE for the DBA (design §10.3).
-- The application team never runs this on shared servers. Review, then run as a role with
-- CREATEDB, CREATEROLE and permission to SET ROLE to the new migrator (e.g. the cluster
-- superuser) with psql:
--
--   psql -v ON_ERROR_STOP=1 \
--        -v migrator_password="$(cat migrator.pw)" -v app_password="$(cat app.pw)" \
--        -f deploy/postgres/provision.sql
--
-- Creates: database `subscribed`, role `subscribed_migrator` (owns schema `subscribed`, runs
-- `admin db migrate` / `admin projections rebuild`), role `subscribed_app` (LOGIN, schema USAGE
-- only, no CREATE). Per-object grants are NOT here: they live in one file,
-- backend/src/Subscribed.Platform/Database/grants.sql, applied by `admin db migrate`.
-- To use a schema in a shared database instead, skip CREATE DATABASE and \connect to it.

\if :{?migrator_password}
\else
  \echo 'missing -v migrator_password=...'
  \quit
\endif
\if :{?app_password}
\else
  \echo 'missing -v app_password=...'
  \quit
\endif

CREATE ROLE subscribed_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD :'migrator_password';
CREATE ROLE subscribed_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD :'app_password';

CREATE DATABASE subscribed OWNER subscribed_migrator ENCODING 'UTF8' TEMPLATE template0;
REVOKE ALL ON DATABASE subscribed FROM PUBLIC;
GRANT CONNECT ON DATABASE subscribed TO subscribed_migrator, subscribed_app;

\connect subscribed

REVOKE ALL ON SCHEMA public FROM PUBLIC;
CREATE SCHEMA subscribed AUTHORIZATION subscribed_migrator;
REVOKE ALL ON SCHEMA subscribed FROM PUBLIC;
GRANT USAGE ON SCHEMA subscribed TO subscribed_app;

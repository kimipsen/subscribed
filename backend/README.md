# Subscribed backend (net11.0)

Modular monolith: `Subscribed.Api` (host, BFF, static SPA, admin CLI) → `Subscribed.Household`
(aggregate, events, inline view, endpoints, membership) → `Subscribed.Platform` (event store
configuration, error mapping, grants, schema versioning, Data Protection keys, readiness, problems).
Design: [foundation draft](https://github.com/kimipsen/subscribed/blob/agentic/project-foundation/design/docs/agentic/project-foundation/design.md)
(rev. 3, sections 3–8, 10, 12, 14). Its historical review status is preserved on the design branch.

## B-1: event-store evidence and decision

Collected on 2026-11-11 in this worktree with the real toolchain: .NET SDK 11.0.100 (runtime
11.0.0), PostgreSQL 18 (isolated local test cluster), Node 24.22.2. The scratch probe lived in a
temporary directory and was deleted; the experiments are re-asserted permanently by the
persistence suite (AC2, AC19, AC20) against the exact `grants.sql`.

| Step | Result |
| --- | --- |
| E1 | Marten **9.59.0** (latest stable on nuget.org); Npgsql latest **10.0.3**. Marten 9.59.0 resolves Npgsql **9.0.4**. |
| E2 | From `project.assets.json` of a `net11.0` probe: Marten 9.59.0, JasperFx 2.102.0, JasperFx.Events 2.102.0, Weasel.Core/Postgresql/Storage 9.50.0 ship `lib/net9.0` and `lib/net10.0` (highest **net10.0**, selected for net11.0). Npgsql 9.0.4 ships `lib/net6.0`, `lib/net8.0`. |
| E3 | The [official 9.59.0 release notes](https://github.com/JasperFx/marten/releases/tag/V9.59.0) describe the JasperFx/Weasel dependency updates and fixes; they declare neither .NET 11 support nor an incompatibility. This is runtime-tested compatibility, not a declared `net11.0` target. |
| E4 | `dotnet new console -f net11.0` + `Marten 9.59.0` + `dotnet build -warnaserror`: 0 warnings, 0 errors (no NU1701/NU1202/NU1605). The whole solution builds with `-warnaserror`. |
| E5 | See "Grant derivation" below. **No UPDATE, DELETE or TRUNCATE on `mt_events` is needed.** |
| E6 | (a) two sessions at the same expected version and (b) a stale explicit version throw `JasperFx.Events.EventStreamUnexpectedMaxEventIdException` → `JasperFx.ConcurrencyException` → `System.Exception`, with **no** inner `PostgresException`. (c) concurrent `StartStream` of the same id throws `Marten.Exceptions.ExistingStreamIdCollisionException` → `Marten.Exceptions.MartenException` → `System.Exception`, no inner `PostgresException`. A 42501 surfaces either as a raw `Npgsql.PostgresException` or as `Marten.Exceptions.MartenCommandException` with an inner `PostgresException` whose `TableName` is empty (the object name is in `MessageText`). |

**Grant derivation (E5).** Store configured exactly as in production (`SubscribedStore`): schema
`subscribed`, string stream ids, Rich append mode, headers enabled, inline single-stream view
projection, a DP key document. The migrator applied the schema; the app role then ran: start a
stream, append at the expected version, rehydrate + load the view, insert + query DP keys, and
the three E6 conflict cases. Objects created: tables `mt_events`, `mt_streams`,
`mt_event_progression`, `mt_event_projection_floors`, `mt_high_water_skips`,
`mt_doc_deadletterevent`, the two document tables, and sequence `mt_events_sequence`; 24
functions (`mt_quick_append_events`, `mt_archive_stream`, `mt_mark_event_progression`,
`mt_jsonb_*`, …), **all `SECURITY INVOKER` and owned by the migrator**. Rich mode executes none of
them, so `grants.sql` revokes EXECUTE from PUBLIC and grants none.

* Sufficiency: with exactly the 11 privileges in
  [`grants.sql`](src/Subscribed.Platform/Database/grants.sql) every scenario passed as the app role.
* Necessity: the probe was re-run 11 times, each time omitting one privilege; every run failed with
  42501 in at least one scenario (e.g. without `UPDATE` on `mt_streams` the second append fails;
  without `SELECT` on `mt_events` rehydration fails; without `INSERT` on `mt_doc_dp_key` key storage fails).
* Observation: a failed append makes Marten write *tombstone* events to the `mt_tombstone` stream
  (`INSERT … ON CONFLICT DO NOTHING` into `mt_streams`, INSERT into `mt_events`). INSERT only.
* The same probe passed with Npgsql 10.0.3; the solution pins **9.0.4**, the version Marten 9.59.0 declares.

**Decision.** Marten passes the least-privilege rule (append-only events, exact grants, invoker
functions, one distinguishable conflict type per case). Per the design's decision table this is
the **"Marten compatible, not declared"** outcome (highest TFM `net10.0` resolved for `net11.0`;
no upstream .NET 11 declaration). This implementation selects Marten provisionally based on the
successful experiments, following the request to implement the drafts. Explicit acceptance of the
"compatible, not declared" support status remains a release gate; it is not asserted here.
G0 (AC1–AC5, AC19, AC20 on SDK 11.0.x) passes in `Subscribed.Persistence.Tests`. The custom
Npgsql store of §5.1 was therefore **not** implemented. If the approver rejects the
"not declared" status, the fallback remains §5.1; `EventStoreErrors.Map` is the only
library-aware code to change besides `SubscribedStore`.

Marten-specific consequences: `grants.sql` names Marten's tables (`mt_events`, `mt_streams`,
`mt_events_sequence`, `mt_doc_household_view`, `mt_doc_dp_key`) instead of §5.1's
`events/streams/views/dp_keys`; Marten 9 supports only asynchronous data access, so the synchronous
`IXmlRepository` bridges with `Task.Run(...).GetAwaiter().GetResult()` (no synchronization context
in ASP.NET Core; the key ring is cached).

## Projects

| Project | Contents |
| --- | --- |
| `src/Subscribed.Platform` | `SubscribedStore` (only Marten configuration), `EventStoreErrors.Map` (only library-aware error code), `Database/grants.sql` (embedded, single source), `SchemaMigrator` + `SchemaVersion`, `ReadinessProbe`, `MartenXmlRepository` (DP keys), RFC 9457 `Problem`, `MemberIdentity`, `SecurityLog` |
| `src/Subscribed.Household` | v1 events with explicit aliases, `Household` aggregate (rehydrate/decide), `HouseholdView` + inline projection, `HouseholdService`, rebuild, endpoints, `HouseholdMembership` authorization handler |
| `src/Subscribed.Api` | `Program` (web, `--check-config`, `--probe-live`, `admin …`), `SubscribedApp` (composition/pipeline), BFF (`OidcSetup`, `SessionCookie`, `BffEndpoints`, `RequestGuards`), `SpaHost`, health, `AdminCli` |
| `tests/Subscribed.UnitTests` | invariants (AC7), golden event fixtures + unknown alias (AC6), validators, error map |
| `tests/Subscribed.Persistence.Tests` | AC1–AC5, AC19, AC20 on PostgreSQL 18 as the least-privilege role |
| `tests/Subscribed.Api.Tests` | HTTP tests on TestServer with the in-process stub IdP (AC8–AC15, AC12b, AC21), child-process startup tests (AC13), admin CLI |
| `tests/Subscribed.TestSupport` | PostgreSQL fixture, stub OpenID Provider, log capture (test-only; no production bypass) |

## Configuration (validated before the host starts)

| Key | Rule |
| --- | --- |
| `App:PublicOrigin` | absolute, no path/query; https (http only in Development) |
| `App:KnownProxies` | optional comma list of IPs/CIDRs; forwarded headers are honoured only from these |
| `App:SpaRoot` | optional; default `<content root>/wwwroot` (the image copies `frontend/dist/subscribed/browser` there) |
| `Oidc:Authority` | absolute https (http only in Development), ≤ 200 chars |
| `Oidc:ClientId`, `Oidc:ClientSecret` | required; the secret only via env/key-per-file |
| `Oidc:IdpLogout` | `ClientId` (default) or `None` |
| `ConnectionStrings:Subscribed` | app role (required for the web host) |
| `ConnectionStrings:SubscribedMigrator` | migrator (`admin db migrate|patch`, `admin projections rebuild`) |
| `Database:Schema` / `Database:AppRole` | `^[a-z_][a-z0-9_]{0,62}$`; schema default `subscribed`; AppRole required for migrate |

Sources: `appsettings.json` (non-secret defaults only), environment variables (`Section__Key`),
key-per-file from `/run/secrets` (or `SUBSCRIBED_SECRETS_DIR`), file name `Section__Key`. Invalid
configuration prints one stderr line with the **keys only** and exits 1. There are no production
fallback credentials.

```sh
dotnet backend/src/Subscribed.Api/bin/Release/net11.0/Subscribed.Api.dll --check-config   # exit 0/1
```

## Admin CLI

`Subscribed.Api admin <command>` (`dotnet run --project backend/src/Subscribed.Api -- admin …`, or
`docker compose -f deploy/compose.yml run --rm subscribed admin …`). Exit codes: 0 ok, 1 invalid
configuration, 2 usage/domain error, 3 conflict (e.g. already initialized), 4 database error.

| Command | Role | Effect |
| --- | --- | --- |
| `db migrate` | migrator | Marten DDL (diff, never drops), `schema_migrations` version, then re-applies `grants.sql` |
| `db patch --output <file>` | migrator | writes pending DDL for DBA review; executes nothing |
| `projections rebuild` | migrator | deletes the view documents and reprojects from events in one transaction |
| `household init --name <n> --owner-subject <sub> --owner-label <l>` | app | creates the household and grants the first member |
| `household grant --subject <sub> --label <l>` | app | grants membership (no self-registration exists anywhere) |
| `household revoke --member-id <id>` | app | revokes; effective on the member's next request; the last member cannot be revoked |
| `household show` | app | lists members, ids and subjects |

The issuer stored with a member is `Oidc:Authority` without trailing slash; the subject is the
`sub` a denied user sees on the access-denied page (`/bff/session` `not-member` state).

## Local development

### Database setup from `.env`

Run this inside the Dev Container (or on a host with Python 3, the pinned .NET SDK,
and PostgreSQL 16+ `psql` on `PATH`):

```sh
cp -n .devcontainer/.env.example .devcontainer/.env
# Edit .devcontainer/.env: keep the existing POSTGRES_* administrator credentials,
# and set distinct SUBSCRIBED_APP_PASSWORD and SUBSCRIBED_MIGRATOR_PASSWORD values.
python3 scripts/setup-database.py
# Or: python3 scripts/setup-database.py --env-file /absolute/path/to/.env
```

Install the PostgreSQL client if `psql` is missing (`sudo apt-get update && sudo
apt-get install postgresql-client` on Ubuntu). The server must already be running.
`POSTGRES_USER` needs database/role creation privileges and permission to grant
membership in the migrator role (the local PostgreSQL container administrator
has these). `POSTGRES_HOST`/`POSTGRES_PORT` default to `localhost:5432`;
`POSTGRES_DB` is the administrator connection database, **not** the application
database. An existing volume retains its original administrator credentials.

The script creates the dedicated `SUBSCRIBED_DB` (default `subscribed`), separate
app/migrator roles, and the schema; runs `admin db migrate` including Data
Protection key tables and grants; verifies app-role access; then saves the runtime
connection and `App:PublicOrigin=http://localhost:5080` in .NET **development
user-secrets**, outside the repository. Restart
the debugger afterward. No connection-string edits to tracked appsettings are
needed. Environment variables and key-per-file secrets still take precedence;
remove stale connection overrides if they point at another database.

Re-running preserves data, reapplies migrations/grants, and updates the two role
passwords from `.env`. Stop the local backend before rotating passwords. Existing
databases/schemas must be owned by the configured migrator; elevated roles or roles
with memberships are rejected. This is a **local development** script, not a
shared-server provisioner. No databases, tables, or roles are dropped.

Dotenv values are literal `KEY=value`, optionally single/double quoted, with
optional `export` and comments. There is no shell execution, variable expansion,
escape processing, or multiline-value support. Never commit the real `.env`.
The migrator password stays in `.env`, not runtime user-secrets. For subsequent
manual migrations, supply `ConnectionStrings__SubscribedMigrator` separately or
rerun the setup script.

### Starting the application

The Dev Container starts a local Keycloak on port 8180; see
[`docs/dev-environment.md`](../docs/dev-environment.md#keycloak-sign-in) for
its bootstrap credentials and synthetic-user setup. The standalone Keycloak
command below is an alternative, not an additional service.

```sh
# PostgreSQL: any PG ≥ 16. Create the roles/schema once with deploy/postgres/provision.sql (local cluster only).
export ConnectionStrings__SubscribedMigrator='Host=localhost;Database=subscribed;Username=subscribed_migrator;Password=…'
export ConnectionStrings__Subscribed='Host=localhost;Database=subscribed;Username=subscribed_app;Password=…'
export ASPNETCORE_ENVIRONMENT=Development
export App__PublicOrigin=http://localhost:5080             # already saved by setup-database.py
dotnet run --project backend/src/Subscribed.Api -- admin db migrate
export KC_DEV_ADMIN_PASSWORD="$(openssl rand -hex 16)"
docker compose -f deploy/compose.dev.yml up -d keycloak     # optional
bash scripts/dev-keycloak-users.sh                         # standalone Keycloak only; prints client secret and users
export Oidc__ClientSecret='<printed by scripts/dev-keycloak-users.sh>'
npm --prefix frontend run build
dotnet run --project backend/src/Subscribed.Api          # frontend + backend at http://localhost:5080
```

The default public origin and OIDC callback use port 5080; no Angular server is
needed. For optional frontend hot reload, start the backend with
`App__PublicOrigin=http://localhost:4200`, run `npm --prefix frontend start` in
another terminal, and browse port 4200. The local Keycloak client permits both
callback origins. Existing realms are not re-imported: re-run
`scripts/dev-keycloak-users.sh` to update their callback registrations (this also
resets synthetic user passwords).

## Tests

Prerequisites: .NET SDK 11.0.100 and a PostgreSQL 18 server reachable through
`SUBSCRIBED_TEST_DB`, an Npgsql connection string for a role with `CREATEDB` and `CREATEROLE`
(fallback: `POSTGRES_USER`/`POSTGRES_PASSWORD`/`POSTGRES_DB` on `localhost:5432`). If neither is
available or the server is unreachable within 30 s the suites **fail**, never skip.

Per test assembly the fixture creates exactly `subscribed_test_<8hex>_migrator`, `…_app` (random
passwords) and the template database `…_tpl`, migrated by the real `SchemaMigrator` + `grants.sql`.
The fixture explicitly grants its administrator SET/INHERIT membership in its own migrator role
(needed since PostgreSQL 16); the administrator does not need SUPERUSER.
Each test gets `CREATE DATABASE … TEMPLATE …_tpl` and runs as `…_app`. Only those exact objects are
dropped (per test `WITH (FORCE)`, at the end of the run, and from a ProcessExit hook). There is
deliberately **no** prefix-based startup sweep.

```sh
dotnet build backend/Subscribed.slnx -c Release -warnaserror
dotnet test backend/Subscribed.slnx -c Release --no-build
dotnet format backend/Subscribed.slnx --verify-no-changes
```

xUnit v3 runs through the VSTest adapter (`Directory.Build.targets` sets
`IsTestingPlatformApplication=false`), so no `global.json` test-runner section is needed.
OIDC tests use `StubIdentityProvider` (test assembly only) behind the OIDC handler's backchannel:
the complete authorization-code + PKCE + nonce + state flow runs; production has no test or
development authentication bypass.

## Known limitations

* `scripts/integration-check.sh` (Docker image build, AC18) has not been run here: no Docker daemon
  is available. Tests exercise discovery failure after login with a failing configuration manager,
  verifying that local logout still succeeds and the fallback is logged.
* Marten's .NET 11 compatibility is demonstrated by the tests, not declared upstream. See the
  event-store decision above before accepting this foundation for deployment.

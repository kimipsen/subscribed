# Subscribed backend (net11.0)

Modular monolith: `Subscribed.Api` (host, BFF, static SPA, admin CLI) → `Subscribed.Subscriptions`
(subscription events, state, calculator, service, endpoints) and `Subscribed.Household`
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
`mt_doc_deadletterevent`, the document tables (the household and subscription views at the time of the probe; the payment option view of schema version 3 follows the same pattern), and sequence `mt_events_sequence`; 24
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
`mt_events_sequence`, `mt_doc_household_view`, `mt_doc_subscription_view`, `mt_doc_payment_option_view`, `mt_doc_dp_key`) instead of §5.1's
`events/streams/views/dp_keys`; Marten 9 supports only asynchronous data access, so the synchronous
`IXmlRepository` bridges with `Task.Run(...).GetAwaiter().GetResult()` (no synchronization context
in ASP.NET Core; the key ring is cached).

The `mt_doc_subscription_view` grant (`SELECT, INSERT, UPDATE`) mirrors `mt_doc_household_view`: both
are inline single-stream snapshots written through the same append-and-save path, so the privilege set
shown necessary and sufficient for the household view applies unchanged. The subscription tests run
as the same least-privilege role, readiness verifies the new grant, and the app role still has no
`DELETE` on any table (a removed subscription is flagged `removed = true`, never deleted).

Schema version 2 added that document table and grant. Schema version 3 (`SchemaVersion.Current`) adds `mt_doc_payment_option_view`
with the same `SELECT, INSERT, UPDATE` grant, for the payment option view (see "Payment options" below); `admin db migrate`
creates the table, records version 3 and re-applies `grants.sql`, and readiness stays at 503 until it has run.

## Projects

| Project | Contents |
| --- | --- |
| `src/Subscribed.Platform` | `SubscribedStore` (only Marten configuration), `EventStoreErrors.Map` (only library-aware error code), `Database/grants.sql` (embedded, single source), `SchemaMigrator` + `SchemaVersion`, `ReadinessProbe`, `MartenXmlRepository` (DP keys), RFC 9457 `Problem`, `MemberIdentity`, `SecurityLog` |
| `src/Subscribed.Household` | v1 events with explicit aliases, `Household` aggregate (rehydrate/decide), `HouseholdView` + inline projection, `HouseholdService`, rebuild, endpoints, `HouseholdMembership` authorization handler |
| `src/Subscribed.Subscriptions` | twelve `subscription.*.v1` events with explicit aliases, `SubscriptionState` (the Marten document and the domain state in one: a self-aggregating inline snapshot), `SubscriptionDecisions` (commands to events, field and invariant checks), `SubscriptionCalculator` (status, current price, history, totals, with exact rational arithmetic), `SubscriptionService`, the migrator-only `SubscriptionProjectionRebuilder`, the known-services catalog (`Catalog/`), DTOs and `/api/subscriptions` endpoints; `PaymentOptions/` holds the three `payment_option.*.v1` events, `PaymentOptionState`, `PaymentOptionDecisions`, `PaymentOptionService`, its rebuilder and the `/api/payment-options` endpoints |
| `src/Subscribed.Api` | `Program` (web, `--check-config`, `--probe-live`, `admin …`), `SubscribedApp` (composition/pipeline), BFF (`OidcSetup`, `SessionCookie`, `BffEndpoints`, `RequestGuards`), `SpaHost`, health, `AdminCli` |
| `tests/Subscribed.UnitTests` | invariants (AC7), golden event fixtures + unknown alias (AC6), validators, error map |
| `tests/Subscribed.Persistence.Tests` | AC1–AC5, AC19, AC20 on PostgreSQL 18 as the least-privilege role |
| `tests/Subscribed.Api.Tests` | HTTP tests on TestServer with the in-process stub IdP (AC8–AC15, AC12b, AC21), child-process startup tests (AC13), admin CLI |
| `tests/Subscribed.TestSupport` | PostgreSQL fixture, stub OpenID Provider, log capture (test-only; no production bypass) |

## Pausing a subscription

A household member can pause a subscription and resume it later. Nothing is deleted and no price changes: pausing only
takes the subscription out of the totals.

| Endpoint | Success | Notes |
| --- | --- | --- |
| `POST /api/subscriptions/{id}/pause` | `200` with the subscription detail and a new `ETag` | appends `subscription.paused.v1` |
| `POST /api/subscriptions/{id}/resume` | `200` with the subscription detail and a new `ETag` | appends `subscription.resumed.v1` |

Both take no body and use the same `If-Match` pipeline as the other writes, in this order: malformed id `404`, `If-Match`
missing `428` or malformed `400`, removed or unknown subscription `404`, stale version `412`, state rule `409`, then the append
(`412` if another writer won the race). The stale-version check runs before the state rule, so a stale `If-Match` on a
subscription that is already paused answers `412`, not `409`.

- **Problem code `invalid-subscription-state` (`409`).** Pausing a subscription that is already paused, or resuming one that is
  not paused, is rejected rather than treated as a no-op. The problem body carries `currentVersion`, like the `412`, so a client can refresh.
- **Status precedence.** `status` is `paused` only when the subscription would otherwise be `active`. `not-started` and
  `ended` win over a pause, because the dates stay authoritative. A pause is allowed in any non-removed state. The flag is kept:
  a paused subscription whose end date is later cleared shows `paused` until it is resumed.
- **DTOs.** Both the overview row and the detail carry `paused` (the flag itself, which the UI uses to offer Resume even when
  `status` shows `ended` or `not-started`). The overview row also carries `version`, the `If-Match` value for a row action.
- **Totals.** Per-currency totals and the active count include `active` subscriptions only, so a paused one is excluded.
  Price history and the current price are unchanged by a pause.
- **History view.** `priceHistory` lists price entries only. Pause and resume events are not shown there.
- **Roll forward only.** Once a `subscription.paused.v1` or `subscription.resumed.v1` event exists, rolling the backend back to a
  version that does not know them fails stream reads with `UnknownSubscriptionEventAliasException`. Recover by redeploying the new version.
  Deploy the backend before or together with the frontend: the new UI reads `version` from the overview rows, which an old backend does not send.
- **Existing data.** Existing streams and snapshot rows are valid as they are: a stored snapshot without `paused` or `pausedAt`
  loads as not paused, and `admin projections rebuild` reproduces the flag from the events. No migration is needed.
## Payment options

A household keeps its own payment options (a card, PayPal, MobilePay/Vipps, anything) and can record which one pays each
price entry. History is kept: the option belongs to a price entry, so a later price entry can name a different option. The
read model exposes `paymentOptionId` on `currentPrice` and on every `priceHistory` row, so charges can later be grouped by it.

| Endpoint | Success | Notes |
| --- | --- | --- |
| `GET /api/payment-options` | `200` `{ "options": [...] }` | every option, removed ones included (`removed: true`), by name |
| `GET /api/payment-options/suggestions` | `200` `{ "suggestions": [...] }` | a static list: `paypal`, `visa`, `mastercard`, `dankort`, `mobilepay-vipps` (one MobilePay/Vipps entry) |
| `GET /api/payment-options/{id}` | `200` with an `ETag` | `404` for an unknown, malformed or other-household option |
| `POST /api/payment-options` | `201`, `Location` and `ETag` | body `{ "name", "label" }` |
| `PUT /api/payment-options/{id}` | `200` with a new `ETag` | needs `If-Match`; `404` on a removed option |
| `DELETE /api/payment-options/{id}` | `204` | needs `If-Match`; a soft remove |

Writes use the same order as the subscription writes: malformed id `404`, `If-Match` missing `428` or malformed `400`, body
`413`/`415`/`400`, field shape `400`, then unknown, removed or other-household option `404`, stale version `412`.

- **Events.** `payment_option.created.v1`, `payment_option.changed.v1` and `payment_option.removed.v1`, one stream per option
  (`paymentoption-<id>`), with a self-aggregating inline snapshot `payment_option_view`. These aliases are registered only in
  `PaymentOptionEventTypes.ByAlias`, not in `SubscriptionEventTypes.ByAlias`.
- **The link to a price entry** is a new event, `subscription.price_entry_payment_changed.v1`. No existing v1 event changed, so
  the existing golden fixtures are byte-identical and old streams replay unchanged. A price entry request carries an optional
  `paymentOptionId`: omitted or `null` means "no option" (full replace, like `catalogId`), and the bundled frontend always sends the key.
  The details `PUT` carries no option and neither sets nor clears one. `Version` rises by one per event, so a `PUT` that changes both
  price and option raises the stream version by 2: use the returned `ETag`.
- **A new assignment** must name an existing, non-removed option of the household (`400` on `paymentOptionId`); an unchanged
  reference to a since-removed option stays allowed, so old entries remain editable. The check runs after the `404` and `412` checks.
  A concurrent removal between the check and the append is accepted: the id still resolves, shown as removed.
- **Removal is a soft remove.** A removed option is hidden from pickers and cannot be assigned, but it keeps resolving by id, so
  history is never rewritten. Renaming or relabelling applies everywhere the option appears, old rows included.
- **Card numbers are never stored.** A name or label with a run of **13 or more digits** is rejected with `400`, where any Unicode
  decimal digit counts (fullwidth and Arabic-Indic too), a letter resets the run and spaces, dashes, dots, slashes and underscores
  do not. So "4111 1111 1111 1111" is rejected, while "Visa ·· 4242", "Last 4 digits" and a 12-digit reference pass. The rule is
  deliberately conservative: a legitimate name or label with a long reference number is rejected too. The `400` carries a fixed
  message and never the input. A name is 1-40 characters and a label at most 40, neither with control characters.
- **Nothing is logged but reason codes.** The name and label are never logged. The signals are `payment_option.created` and
  `payment_option.removed` (with the opaque option id) and `payment_option.rejected` with one reason code:
  `card_number_like`, `invalid_length` or `control_characters`. Support can use the reason code to explain a rejected label.
- **Frontend.** `/payment-options` manages the options, and the add form and the price-entry form of the detail page have an
  option select. A client-side card-number check gives early feedback only: the server rule is the authority.

**Rollout order.** Schema version 3 adds `mt_doc_payment_option_view` and its grant, so:

1. Take a database backup.
2. Run `admin db migrate` **before** the new app version starts. Until it has run, readiness reports
   not-ready (`schema-object-missing:mt_doc_payment_option_view`, and `schema-version-mismatch` once the table exists) and the app
   does not take traffic, so a skipped migration fails visibly at readiness instead of with `42501` on the first request.
3. Start the new app version.

**Rollback.** Redeploying the old binary after `db migrate` is **not supported**. `ReadinessProbe` reports
`schema-version-mismatch` whenever the stored version differs from `SchemaVersion.Current` (the check is deliberately strict, not
`>=`, so it keeps catching a skipped migration for every future version), so an older binary never becomes ready. Once an option has
been assigned, `subscription.price_entry_payment_changed.v1` events exist and an older binary cannot read those streams
(`UnknownSubscriptionEventAliasException`). To roll back, restore the backup taken before `db migrate` and redeploy the older
binary. Changes made after the migration are lost on restore.

**Existing data.** Existing streams and snapshot rows are valid as they are: a stored subscription snapshot without
`paymentOptionId` on its entries loads as no option, and `admin projections rebuild` reproduces both view tables from the events.

## Subscription catalog

A curated, read-only catalog of known services lives in
`src/Subscribed.Subscriptions/Catalog/catalog.json`, an embedded resource. It is loaded and validated once at startup
(`Catalog` is a singleton); invalid data stops the process, and one log line reports the service count and the oldest
`lastVerified`. Nothing is fetched automatically and catalog data never overwrites household data. How to add or update an
entry, and what counts as verification: [`docs/subscription-catalog.md`](../docs/subscription-catalog.md).

**`GET /api/subscriptions/catalog`** (household members only, `Cache-Control: no-store`) returns
`{ services, defaultCountry }`. Services and their plans keep the file order. Each service has `id`, `name`, `category`,
`logoUrl` (or null), `lastVerified` and `plans`; each plan has `country`, `name`, `currency`, `minorUnit`, `amount` (a decimal
string), `cadenceUnit`, `cadenceCount` and `upcomingChange` (`{ amount, effectiveFrom }` or null). `sourceUrl` is not exposed.

**Linking a subscription.** `POST /api/subscriptions` and `PUT /api/subscriptions/{id}` accept an optional string `catalogId`;
responses carry `catalogId` and `logoUrl` on the overview rows and on the detail.

* The id is trimmed; an empty value means no link. Its shape is lowercase kebab-case, at most 64 characters.
* An unknown id is a 400 with the error under `catalogId`. On `PUT` the id is only checked when it differs from the stored one,
  so a subscription whose catalog entry was removed can still be edited without touching its link.
* `PUT` keeps its full-replace semantics: **an omitted or null `catalogId` unlinks**. The frontend always sends it. An older
  frontend that omits it would unlink; the frontend and the backend ship together, so this is accepted.
* The link is stored as the event `subscription.catalog_linked.v1` (`catalogId`, null for unlinked). An add with a catalog id
  appends `subscription.added.v1` and `subscription.catalog_linked.v1` in one write. No existing event shape changed.
* A stored id that is no longer in the catalog never breaks a read: `catalogId` is returned as stored, `logoUrl` is null, and the
  API logs a warning that names the subscription id and the catalog id (the detail logs one per read, the overview at most one
  per request).

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
| `projections rebuild` | migrator | deletes the household, subscription and payment option view documents and reprojects each from events (prints the three counts) |
| `household init --name <n> --owner-subject <sub> --owner-label <l>` | app | creates the household and grants the first member |
| `household grant --subject <sub> --label <l>` | app | grants membership (no self-registration exists anywhere) |
| `household revoke --member-id <id>` | app | revokes; effective on the member's next request; the last member cannot be revoked |
| `household relabel --member-id <id> --label <l>` | app | changes a member's display label; member id, subscriptions and history are unaffected; an unchanged label is a no-op (no event, version unchanged); unknown member ids and invalid labels exit 2 |
| `household show` | app | lists members, ids and subjects |

Household changes are stored as events in the `household-default` stream: `household.created.v1`,
`household.renamed.v1`, `household.member_granted.v1`, `household.member_revoked.v1` and
`household.member_label_changed.v1`. Members are relabelled over HTTP with
`PUT /api/household/members/{memberId}/label` (body `{"displayLabel": "…"}`, `If-Match` required,
the same authorization and concurrency rules as `PUT /api/household/name`; 404 `not-found` for an
unknown member) or with `household relabel`.

> **Rollout note: roll forward only.** Once the first `household.member_label_changed.v1` event is
> stored, a binary without that alias cannot load the household: `RehydrateAsync` throws
> `UnknownEventAliasException` (alias `household.member_label_changed.v1`) on every household load,
> which also breaks authentication/membership checks, and clients see a generic 500 (the stream id is
> not logged). Deploy the new version first and do not relabel anyone until the release is settled.
> Rolling back the binary is not supported; recovery is redeploying the new version.

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
# Or: python3 scripts/setup-database.py --env-file /absolute/path/to/this/repository/.env
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
The selected file must be named `.env` and resolve inside this repository;
paths and symlinks escaping the repository are rejected.
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

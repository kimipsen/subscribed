# Subscribed - project foundation design

Status: **DRAFT rev. 3 for Copilot review and human approval. BLOCKED on B-1 (§4.1). Not approved;
nothing is implemented.**
All names, IDs and values are synthetic. No real IdP settings, hosts or credentials are recorded.
Companion mockup: [`preview.html`](preview.html) (synthetic static HTML, not application code).

> **Blocker B-1: no event-store library is recommended in this revision.** The requirement is a
> library whose .NET 11 compatibility has been *verified*. For the third time, this design session's
> tool permissions denied every attempt to collect evidence: WebFetch, WebSearch, `curl`,
> `dotnet`, NuGet cache listing, and local `python3` scripts (§17). No library's compatibility can
> be shown, including Npgsql, which every option depends on. Marten is therefore listed only as the
> **preferred candidate**. It is not a recommendation. The same applies to its exact grants and
> exception types (§5, §10.3). §4.1 defines the experiments (E1-E6) that resolve B-1 and a
> decision rule that leaves nothing to judgement. **B-1 must be resolved and recorded in this
> document before approval.** It is never deferred to implementation.

## 1. Scope

In scope: repo structure, Keycloak OIDC login/logout, household-membership authorization, a protected
API, PostgreSQL, event sourcing proven by one aggregate (`Household`), health and config validation,
tests, a reproducible dev environment, and Docker deployment.

Out of scope: subscription CRUD, costs/dashboards, billing, reminders, import/export, bank/payment
integrations, member-management UI, multi-household, and Keycloak back-channel logout.

## 2. Observed environment and assumptions

| Observation (inspected) | Consequence |
| --- | --- |
| No app code; Python `tests/`, skills, `.devcontainer/`, `build.yml` | New `backend/`, `frontend/`, `deploy/` |
| Devcontainer `dotnet:2-10.0-noble`, SDK 10.0.403 | Provision SDK 11.0.100 (§11); no downgrade |
| Compose: `app` shares `db`'s network (PG at `localhost:5432`); `db` = `postgres:18`, `env_file: .env` with no `.env.example`; `app` has no `env_file`; comments cite nonexistent `buddy`/`keycloak` items | P0 fixes the comments, adds `.env.example`, gives `app` the `env_file` |
| `build.yml`: SonarQube on Windows, root `dotnet build`, no `setup-dotnet` | B3 adds `setup-dotnet`, `.slnx` path, a Linux test job with `postgres:18` |
| No Node, npm, Docker CLI, gh | Pinned devcontainer features (§11) |
| Review diff ≤ 60 KB; checks are argv only | Slices (§13); script for compound checks (§14.3) |

Assumptions:

- One deployment serves one household.
- A TLS-terminating reverse proxy exists in production.
- A realm admin creates the client (§6.7); we never configure Keycloak.
- Production PostgreSQL is version 16 or later.
- The external Keycloak is version 18 or later. Version 18 introduced the RP-Initiated Logout
  endpoint with `client_id` (§6.4).

## 3. Architecture

Modular monolith: one container serves the API and the built Angular SPA from one origin.

The browser talks to `Subscribed.Api` (net11.0) only, over the same origin with an HttpOnly cookie. The
API reaches Keycloak (OIDC, confidential client) and PostgreSQL (schema `subscribed`).

| Project (`backend/`) | Responsibility |
| --- | --- |
| `src/Subscribed.Api` | Host, BFF, middleware, static files, `admin` CLI |
| `src/Subscribed.Platform` | Event store, `EventStoreErrors.Map`, options, health, DP keys, problems, `Database/grants.sql` |
| `src/Subscribed.Household` (→ Platform) | Aggregate, events, `HouseholdView`, endpoints, membership handler |
| `tests/Subscribed.UnitTests` | Domain/validation, no I/O |
| `tests/Subscribed.Persistence.Tests` | Event store on PostgreSQL as the least-privilege role |
| `tests/Subscribed.Api.Tests` | HTTP tests + child-process startup tests |
| `tests/Subscribed.TestSupport` | PG fixture (§14.1), log capture, stub IdP |

Module rule: modules expose only commands, queries, and endpoint mappings. No module reads another
module's documents.

## 4. Decisions and alternatives

**D-A Modular monolith** over microservices. There is one household, one deployable, and one DB, and
no requirement for independent scaling. Microservices would add network, auth, and consistency cost
for no benefit.

**D-B Event store** (requirements: PostgreSQL, event sourcing mandatory, .NET 11).

| Option | Fit | Concerns |
| --- | --- | --- |
| **Marten** (JasperFx, MIT) | Native PG event store + documents; expected-version appends; inline (same-transaction) projections; rebuild; upcasting; DDL export; mature | .NET 11 **unverified** (B-1); large dependency surface; generates its own tables/functions, so its grants and exception types are **unverified** (E5, E6) |
| Eventuous + PG store | Clean aggregate model | No same-transaction projection; smaller community; .NET 11 unverified |
| **Custom store on Npgsql** (§5.1) | Full control; Npgsql is needed by every option anyway; grants and errors are known **by construction**, because we write the SQL | We own serialization, projections, rebuild, migrations, and concurrency (~600 lines + tests); Npgsql's .NET 11 support is also **unverified** |
| KurrentDB; Equinox | Purpose-built; PG-capable | Not PostgreSQL; F#-centric |

**Recommendation: none until B-1 is resolved.** The ranking below is conditional. It becomes a
recommendation only when §4.1 records evidence for it.

1. **Marten (preferred candidate).** It is the only option with same-transaction projections,
   rebuild, and upcasting on PostgreSQL without bespoke infrastructure. It qualifies only if E1-E6
   all pass.
2. **Custom Npgsql store (§5.1).** Chosen if Marten fails any of E1-E6 and Npgsql passes E1-E4. Its
   only external evidence need is Npgsql itself. §10.3 then applies the exact grants in §5.1.
3. If neither qualifies, the milestone is blocked. The project never silently retargets to .NET 10.

### 4.1 Blocker B-1: evidence to collect before approval

These steps run in the **design stage**, by the human approver or by a design session with network,
Docker, and `dotnet` permission. They use primary sources only and produce no repository changes.
Every scratch file lives in a new temporary directory outside the repository. The results are written
into the Result column of this table, and then the document is re-reviewed.

| Step | Primary source / command | Records | Result |
| --- | --- | --- | --- |
| E1 | `https://api.nuget.org/v3-flatcontainer/marten/index.json` (and `npgsql`) | latest stable `V` (and `N`) | **blocked: tool permission denied (§17)** |
| E2 | `…/marten/V/marten.nuspec`, the nuspecs of each JasperFx dependency it lists, and `…/npgsql/N/npgsql.nuspec` | every `<group targetFramework>` | **blocked** |
| E3 | `https://github.com/JasperFx/marten/releases/tag/V` and the Npgsql release notes for `N` | statements about .NET 11 | **blocked** |
| E4 | `docker run --rm -v "$TMP:/w" -w /w mcr.microsoft.com/dotnet/sdk:11.0 sh -c 'dotnet --version && dotnet new console -f net11.0 -n probe && cd probe && dotnet add package Marten --version V && dotnet build -warnaserror'` | SDK version; build output; no NU1701/NU1202/NU1605 | **blocked** |
| E5 | Grant derivation experiment (below) | exact (statement, object) set used by the app role; schema object names; function security attributes | **blocked** |
| E6 | Concurrency experiment (below) | the full type name and inheritance chain of each exception; any inner `PostgresException.SqlState` | **blocked** |

**E5, grant derivation.** Run in the E4 container against a throwaway `postgres:18` container on a
private Docker network:

1. As superuser, create role `probe_migrator` (it owns schema `subscribed`) and role `probe_app`
   (LOGIN, `USAGE` only). Run `ALTER ROLE probe_app SET log_statement = 'all'`.
2. The probe app configures Marten exactly as §5 does: schema `subscribed`, string stream identity,
   Rich append mode, an inline `HouseholdView` projection, and a `DataProtectionKey` document.
   `AutoCreate.All` runs once as `probe_migrator`. Then dump the DDL with `pg_dump -s -n subscribed`
   and list the functions with `\df+ subscribed.*`, which shows `SECURITY DEFINER`/`INVOKER`.
3. As `probe_app`, with no grants yet, run: start the stream; append to an existing stream; perform a
   rename that updates the view; store and load a DP key; rehydrate; and make two concurrent appends
   at the same version.
4. Each statement that fails with 42501 gets the narrowest grant that fixes it. Repeat until every
   scenario passes. The PostgreSQL log gives the complete statement list.
5. Record: the final `GRANT` list, which **becomes §10.3 verbatim**; every UPDATE or DELETE that
   targets `mt_events`; and the exact progression table name.

**E6, exceptions.** In the same probe, run three cases. (a) Two sessions append at the same expected
version. (b) Append with a stale explicit version. (c) Two concurrent `StartStream` calls for the same
key. For each case, record the thrown type, its base types, and the inner `PostgresException.SqlState`.
These **become the §5 mapping table verbatim**.

**Decision rule.** No case is left to judgement:

| Result | Outcome |
| --- | --- |
| E2 shows `net11.0` for Marten and every JasperFx dependency, E4 is clean, E5 needs no UPDATE/DELETE/TRUNCATE on `mt_events`, and E6 yields one distinguishable conflict type per case | **Marten verified.** Pin `V`, replace §5 and §10.3 with the recorded results, and re-review. |
| As above, but the highest TFM is `net10.0` (resolved for `net11.0` by NuGet), and E3 states no incompatibility | **Marten compatible, not declared.** Requires the approver's explicit tick in §16. G0 = AC1-AC5, AC19, and AC20 pass on SDK 11.0.x as the first B1 step. |
| E4 fails, E5 needs UPDATE/DELETE/TRUNCATE on `mt_events`, or E6 cannot distinguish a conflict | **Marten rejected.** Run E1-E4 for Npgsql. If they pass, use the custom store (§5.1), whose grants and errors are fixed below. Otherwise the milestone is blocked. |

**D-C Browser auth: BFF cookie (recommended)** vs SPA code+PKCE (public client).

- **SPA code+PKCE** keeps tokens in the browser, where XSS can exfiltrate them. It needs in-browser
  renewal (which breaks with third-party-cookie blocking), JWT bearer validation, and CORS when
  cross-origin.
- **The BFF** (confidential client, code+PKCE server-side) keeps no tokens in the browser, only an
  HttpOnly cookie. It needs no refresh (no downstream APIs) and is same-origin. Its cost is CSRF
  defenses and a server session.

The BFF is chosen because it has the smallest token exposure, the single container is already same
origin, and ASP.NET has built-in handlers.

**D-D Membership is app-owned and event-sourced** (vs a Keycloak role claim). Authentication alone
never grants access. App ownership needs no realm changes, is auditable as events, and gives event
sourcing a real example.

**D-E Single container** for SPA + API: same origin, no CORS, one image.

**D-F Data Protection keys in PostgreSQL** (document `DataProtectionKey`). Cookies survive restarts,
and backups are complete. As a consequence, backups are sensitive (§10.5).

**D-G `response_mode=query`** (vs `form_post`). The callback becomes a top-level cross-site **GET**.
SameSite=Lax correlation/nonce cookies are sent, and no cookie needs SameSite=None. The code in the URL
is mitigated: the code is single-use, PKCE-bound, and redeemable only
with the client secret; the callback 302s immediately; `Referrer-Policy: no-referrer` is set; and
query strings are never logged. If `form_post` is ever adopted, the correlation and nonce cookies must
be `SameSite=None; Secure`, and `/signin-oidc` stays Origin-exempt.

## 5. Event sourcing

**Streams.** One stream per aggregate, keyed by string. The foundation has one aggregate, `Household`,
with stream `household-default`. Future aggregates (e.g. `subscription-<uuid>`) get their own streams.
There are no cross-stream transactions.

**Events** (v1 immutable records, explicit aliases; System.Text.Json, camelCase, UTC
`DateTimeOffset`):

| Alias | Payload |
| --- | --- |
| `household.created.v1` | `name`, `createdAt` |
| `household.renamed.v1` | `name`, `renamedAt` |
| `household.member_granted.v1` | `memberId`, `issuer`, `subject`, `displayLabel`, `grantedAt` |
| `household.member_revoked.v1` | `memberId`, `revokedAt` |

Headers: `correlationId` (the trace id) and `actor` (`member:<memberId>` | `admin-cli`). No emails,
tokens, or other claims are stored.

**Invariants** (unit-tested): the name is trimmed, 1-80 chars, with no control chars; the household is
created once; re-granting an active member is a no-op; revoking a non-member or the last member fails.

**Identity.** The member key is `(issuer, subject)`: `issuer` is the configured authority without a
trailing slash, and `subject` is the `sub` claim. `email` and `preferred_username` are never used.
`memberId` = the first 16 hex chars of SHA-256(`issuer|subject`), which is opaque. If the realm is
recreated, members must be re-granted.

**Writes and concurrency.**

1. Load the stream at the expected version (optimistic, no row lock).
2. Decide and append the events.
3. Commit. The events, the stream version bump, and the inline `HouseholdView` upsert commit in **one
   transaction**.

The expected version comes from `If-Match` (§7). **Library-neutral error contract.**
`EventStoreErrors.Map` is the only place that knows library exception types. It returns exactly one
of the following:

| Condition | Result |
| --- | --- |
| append at a version that is no longer current (cases E6 a/b) | `ConcurrencyConflict` → 412 |
| concurrent creation of the same stream (case E6 c) | `ConcurrencyConflict` (admin init exits 3) |
| `PostgresException` 23505 on the events/streams tables (defensive) | `ConcurrencyConflict` → 412 |
| `PostgresException` 42501 | log `database-privilege-missing` + table name → 500; readiness fails |
| anything else | rethrow → 500 `internal-error` |

The library types for rows 1-2 are **filled in from E6 only**. Candidate Marten names, recalled by the
design author and **unverified**, are `ConcurrencyException`, `EventStreamUnexpectedMaxEventIdException`,
and `ExistingStreamIdCollisionException`. They are not normative. With the §5.1 store, rows 1-2 are
23505 on `events_pkey` or `streams_pkey`, by construction. AC2 checks every row against a real
database. User commands are never auto-retried. The admin CLI retries once after a reload.

**Read model.** `HouseholdView` is an inline single-stream projection (`id`, `name`, `version`,
`members[] {memberId, issuer, subject, displayLabel, grantedAt}`) that never deletes. It serves reads
and the membership check with one uncached primary-key read per request, so a revocation applies on
the next request. DTOs never expose other members' `issuer`/`subject`. Writes rehydrate from events.

**Rebuild.** `admin projections rebuild` runs as the **migrator**. It deletes the view documents and
reprojects them from events in one transaction. The app role never needs DELETE.

**Schema evolution.** Stored events are never edited or deleted. A version only gains optional
fields. A breaking change gets a new alias `...v2` plus an upcaster. An unknown alias fails loudly.
Every alias has a golden JSON fixture test. A projection shape change means a version bump and a
rebuild.

**Migrations.** All objects live in `Database:Schema` (default `subscribed`). At runtime the app
issues no DDL (with Marten, `AutoCreate.None`). `admin db migrate` (migrator) applies the schema and
then re-applies `grants.sql`. `admin db patch --output <file>` exports DDL for DBA review.

### 5.1 Fallback store (used only if Marten is rejected under §4.1)

The fallback is plain Npgsql SQL, versioned as numbered scripts `Database/Migrations/NNNN_*.sql`, with
a `schema_migrations` table that only the migrator writes to.

```sql
CREATE TABLE streams (stream_id text PRIMARY KEY, version bigint NOT NULL, created_at timestamptz NOT NULL);
CREATE TABLE events  (seq bigint GENERATED ALWAYS AS IDENTITY UNIQUE, stream_id text NOT NULL REFERENCES streams,
                      version bigint NOT NULL, alias text NOT NULL, data jsonb NOT NULL, headers jsonb NOT NULL,
                      recorded_at timestamptz NOT NULL, PRIMARY KEY (stream_id, version));
CREATE TABLE views   (id text PRIMARY KEY, kind text NOT NULL, version bigint NOT NULL, data jsonb NOT NULL);
CREATE TABLE dp_keys (name text PRIMARY KEY, xml text NOT NULL);
```

**Append** runs in one transaction:

- for a new stream, `INSERT INTO streams` (a duplicate is 23505 `streams_pkey`, a conflict);
- otherwise, `UPDATE streams SET version=$new WHERE stream_id=$id AND version=$expected`, where 0 rows
  affected is a conflict;
- then `INSERT INTO events` (a duplicate is 23505 `events_pkey`, a conflict);
- then the view upsert, `INSERT … ON CONFLICT (id) DO UPDATE`.

**Grants:**

| Object | Grant |
| --- | --- |
| `streams` | SELECT, INSERT, UPDATE |
| `events` | SELECT, INSERT |
| `views` | SELECT, INSERT, UPDATE |
| `dp_keys` | SELECT, INSERT |
| `events_seq_seq` (identity) | USAGE (allows only `nextval`/`currval`; granted so that sufficiency does not depend on identity-column privilege details) |

No functions are used. `REFERENCES` is not needed by the inserting role.

## 6. Authentication, session, authorization

### 6.1 Login: `GET /bff/login?returnUrl=/household`
This is a top-level navigation, so it needs no `X-CSRF` and no Origin check.

1. Validate `returnUrl`:
   - it is a local path that starts with `/`;
   - it is not `//` or `/\`;
   - it has no control chars and is ≤ 512 chars;
   - default `/`.

   On failure → 400 `invalid-return-url`.
2. Already signed in → 302 to `returnUrl`.
3. Otherwise → challenge with:
   - `response_type=code`, PKCE S256, `response_mode=query`;
   - scopes `openid profile`;
   - `redirect_uri = App:PublicOrigin + /signin-oidc` (never built from `Host`).

   `returnUrl` travels inside the encrypted `state`.

A cross-site trigger of this endpoint only shows the IdP to the victim. Login CSRF (forcing an
attacker's code into the victim's session) is stopped at the callback.

### 6.2 Callback: `GET /signin-oidc`
This is the ASP.NET OIDC handler. It is **explicitly exempt** from the `X-CSRF` and Origin middleware
(a cross-site navigation by design). Required validations:

- `state` matches the **correlation cookie**, and the id_token `nonce` matches the **nonce cookie**;
- the PKCE verifier, RFC 9207 `iss` when present, and the id_token signature, issuer, audience, and
  lifetime are all checked.

Both cookies are HttpOnly, SameSite=Lax, Secure (except Development over http), Path=/signin-oidc,
and live for 15 min.

| Outcome | Response |
| --- | --- |
| success | create session → 302 `returnUrl` (re-validated) |
| `error=access_denied` | 302 `/welcome?authError=cancelled` |
| any other failure | 302 `/welcome?authError=callback-failed`; security log with the failure kind only |

### 6.3 Session
- **Cookie:** `__Host-subscribed`. Development over http uses `subscribed.dev`, non-Secure.
- **Attributes:** HttpOnly, Secure, SameSite=Lax, Path=/.
- **Ticket contents:** only `iss`, `sub`, `name`, `issued_at`. **No tokens are kept**
  (`SaveTokens=false`): the access, refresh, and id tokens are discarded after validation.
- **Bounded size.** The claims are normalized in `OnTokenValidated` before the ticket is built:

  | Claim | Rule | Max |
  | --- | --- | --- |
  | `iss` | the configured `Oidc:Authority`; config validation caps it at 200 chars | 200 |
  | `sub` | as received. If it is empty or longer than 255 chars, the sign-in fails → `authError=callback-failed` | 255 |
  | `name` | from `name`, else `preferred_username`, else `"Signed-in user"`. Control chars are removed, whitespace is trimmed, and the value is truncated to 64 UTF-16 code units without splitting a surrogate pair | 64 |
  | `issued_at` | Unix seconds | 10 |

  The plaintext is therefore under 1 KB. Data Protection and base64 encoding keep the cookie well
  under ASP.NET's 4,050-char chunk threshold, so it is always one cookie. AC12 asserts this with
  worst-case inputs.
- **Lifetime.** The cookie is a browser-session cookie (`IsPersistent=false`). The ticket carries
  `ExpiresUtc`.
  - **Idle:** 2 h, sliding. A renewal sets `ExpiresUtc = min(now + 2 h, issued_at + 12 h)`, so sliding
    can never extend past the absolute limit.
  - **Absolute:** 12 h. In `OnValidatePrincipal`, using the injected `TimeProvider`, the session is
    invalid when `issued_at` is missing or unparseable, lies more than 5 min in the future, or
    `now ≥ issued_at + 12 h`.
  - **When either limit is exceeded,** the response carries a `Set-Cookie` that deletes
    `__Host-subscribed`, and the request continues anonymously. For the absolute limit,
    `OnValidatePrincipal` calls `RejectPrincipal()` and `SignOutAsync(cookie scheme)`. For the idle
    limit, the cookie handler itself fails the ticket as expired, so a small middleware that runs
    after authentication deletes the cookie whenever it was sent but did not authenticate. The same
    middleware also covers undecryptable cookies. The request then gets:

    | Endpoint | Response |
    | --- | --- |
    | `/api/**` | 401 `unauthenticated` |
    | `GET /bff/session` | 200 `{"state":"anonymous"}` |
    | `POST /bff/logout` | the anonymous branch |

    A security log event `session-expired` records `reason=idle|absolute` and nothing else. The
    SPA shows the session-expired banner (§9).
- **Keycloak-side logout:** does not end the app session before it expires (accepted; back-channel
  logout is future work).
- **Membership revocation:** applies immediately.

### 6.4 Logout: `POST /bff/logout`
Requires `X-CSRF` and passes the Origin check. The server deletes the cookie first, then returns
`200 {"redirectUrl","idpLogout"}`. The URL is built only from configuration and discovery, never from
the request.

- **Signed in, `Oidc:IdpLogout=ClientId` (default), discovery cached:** `idpLogout:true`, URL
  `<end_session_endpoint>?client_id=<ClientId>&post_logout_redirect_uri=<PublicOrigin>/signed-out`.
  **No `id_token_hint`:** the token is not retained (§6.3), and a retained hint would usually be
  expired anyway.
- **Anonymous, `Oidc:IdpLogout=None`, or discovery unavailable:** `idpLogout:false`,
  `"/signed-out?idp=0"`. `/signed-out` then notes that the organisation sign-in may persist.

**Evidence for logout without a hint.** These sources were recalled by the design author. They were
**not re-fetched in this session**, because the tools were denied (§17).

- **OpenID Connect RP-Initiated Logout 1.0, §2.** `client_id` is an optional request parameter. When
  `post_logout_redirect_uri` is present without `id_token_hint`, the OP uses `client_id` to verify
  that the URI is registered for that client.
- **Keycloak 18.0 upgrading notes, "OpenID Connect Logout".** The legacy `redirect_uri` parameter was
  replaced by `post_logout_redirect_uri`, which must be accompanied by `id_token_hint` **or**
  `client_id`. Without `id_token_hint`, Keycloak shows a logout confirmation page before it
  redirects.
- **Keycloak Server Administration Guide, client "Login settings".** The URI must match the client's
  *Valid post logout redirect URIs*. The value `+` reuses the valid redirect URIs. An unregistered URI
  produces a Keycloak error page instead of a redirect.

**Fallback and verification.** The behaviour depends on the Keycloak version and on how the client is
configured. The design therefore does not rely on it blindly:

1. §6.7 requires the realm admin to register `<PublicOrigin>/signed-out` explicitly as a valid post
   logout redirect URI.
2. The deployment checklist (§10.4, step 6) has a manual sign-out check against the real Keycloak.
3. If that check fails, or Keycloak is older than 18, the operator sets `Oidc:IdpLogout=None`. Logout
   is then local only: the cookie is deleted and the user is told that their organisation sign-in may
   persist. No code change is needed.
4. Supporting `id_token_hint` would require a server-side ticket store. That is out of scope.

AC12 covers both modes.

The SPA navigates only to the same-origin `/signed-out[?idp=0]` or an absolute `https:` URL (`http:`
in a dev build only). Anything else → `/signed-out?idp=0`.

### 6.5 CSRF, CORS, headers
- **`X-CSRF: 1`** is required on `/api/**`, `GET /bff/session`, and `POST /bff/logout`. A custom
  header forces a CORS preflight that is never approved. Missing → 403 `csrf-header-required`.
- **Exempt from `X-CSRF`:** `/bff/login`, `/signin-oidc`, `/health/*`, static files.
- **Origin check** on unsafe methods on `/api/**` and `/bff/logout`: an `Origin` that is present and
  ≠ `App:PublicOrigin` → 403 `origin-mismatch`. An absent Origin is allowed (`X-CSRF` is still
  required).
- **No CORS policy;** no response ever carries `Access-Control-Allow-*`.
- **No redirects:** `/api/**` returns 401 (unauthenticated) or 403 (non-member) problem+json, never a
  redirect. No `/bff/*` endpoint returns 401 or redirects, except `/bff/login` (302).
- **Response headers:**
  - CSP `default-src 'self'; script-src 'self'; style-src 'self' 'nonce-…'; img-src 'self' data:;
    connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'`;
  - `nosniff`, `Referrer-Policy: no-referrer`, HSTS on https.
- **CSP nonce contract:** the backend replaces `__CSP_NONCE__` in `index.html`; the frontend uses
  `<app-root ngCspNonce="__CSP_NONCE__">` and disables critical-CSS inlining.
- **Limits:** `/api/**` body ≤ 16 KB. Forwarded headers are accepted only from `App:KnownProxies`.

### 6.6 Authorization
Policy `HouseholdMember` covers all `/api/**` (fallback = deny). It requires authentication **and**
`(iss, sub)` in `HouseholdView.members`. A household that is not initialized has no members, so the
result is 403. Bootstrap: `admin household init --name <n> --owner-subject <sub> --owner-label <l>`. A
denied user sees their own `subject` to give to the administrator.

### 6.7 Keycloak client (documented for the realm admin; never automated)
- Confidential client, Standard flow only, PKCE S256 required.
- Valid redirect URI `<PublicOrigin>/signin-oidc`; *Valid post logout redirect URIs* set explicitly
  to `<PublicOrigin>/signed-out` (not left empty).
- No web origins; direct grants and implicit flow off.

## 7. API contracts

Errors are RFC 9457 `application/problem+json` with `type` `urn:subscribed:problem:<code>`, `title`,
`status`, and `traceId`. They never contain stack traces, SQL, hostnames, or config values.

| Method/path | X-CSRF | Auth | Success | Failures |
| --- | --- | --- | --- | --- |
| `GET /bff/login` | no | none | 302 IdP / 302 returnUrl | 400 `invalid-return-url` |
| `GET /signin-oidc` | exempt | callback | 302 returnUrl | 302 `/welcome?authError=cancelled\|callback-failed` |
| `GET /bff/session` | yes | optional, never 401 | 200 | 403 `csrf-header-required`; 503 `database-unavailable` (signed-in lookup only) |
| `POST /bff/logout` | yes | optional, never 401 | 200 | 403 `csrf-header-required\|origin-mismatch` |
| `GET /api/household` | yes | member | 200 + `ETag: "<version>"` | 401 `unauthenticated`; 403 `not-a-household-member\|csrf-header-required`; 503 |
| `PUT /api/household/name` | yes | member | 200 + new ETag | 400 `validation-failed`; 412 `concurrency-conflict`; 428 `if-match-required`; 413; 401; 403 (+`origin-mismatch`); 503 |
| `GET /health/live`, `/health/ready` | no | none | 200 | ready: 503 |

Evaluation order: CSRF/Origin (403) → authentication (401) → membership (403) → validation (400/428)
→ concurrency (412). An unknown `/api/**` path → 404 problem. Other HTML GETs → `index.html`.

```jsonc
// GET /bff/session
{"state":"anonymous"}
{"state":"not-member","user":{"displayName":"Synthetic User B","subject":"00000000-0000-4000-8000-00000000000b"},"expiresAt":"2026-01-01T20:00:00Z"}
{"state":"member","user":{"displayName":"Synthetic User A","memberId":"3f2a9c0d41e7b865"},"expiresAt":"2026-01-01T20:00:00Z"}
// HouseholdDto
{"id":"household-default","name":"Synthetic Household","version":3,"members":[{"memberId":"3f2a9c0d41e7b865","displayLabel":"Member A","grantedAt":"2026-01-01T08:00:00Z"}]}
// PUT /api/household/name, If-Match: "3"
{"name":"Renamed Synthetic Household"}
// 400 / 412
{"type":"urn:subscribed:problem:validation-failed","title":"Validation failed","status":400,"traceId":"00-…","errors":{"name":["Name must be 1–80 characters."]}}
{"type":"urn:subscribed:problem:concurrency-conflict","title":"The household was changed by someone else.","status":412,"traceId":"00-…","currentVersion":4}
// POST /bff/logout (anonymous)
{"redirectUrl":"/signed-out?idp=0","idpLogout":false}
```

`displayName` = the `name` claim, falling back to `preferred_username`. It is for display only.

SPA handling of `/bff/session`: 200 → the given state. 403, 503, network, or any other error →
`unavailable` with the `traceId`. An error is **never** treated as anonymous.

## 8. Operations

**Health.**

- **`/health/live`:** process only, no dependency checks.
- **`/health/ready`:** these checks must be Healthy:
  - `database`: `SELECT 1` as the app role, 2 s timeout;
  - `schema`: the expected tables exist;
  - `privileges`: `has_table_privilege`, `has_sequence_privilege`, and `has_function_privilege` confirm
    every grant in `grants.sql`, and the *absence* of DELETE, TRUNCATE, and UPDATE on the events table.

  `identityProvider` (discovery fetched within 5 min) may be Degraded without failing readiness.
- **Response body:** per-check status only.
- **Docker:** `HEALTHCHECK` uses `/health/live`.

**Configuration.** `ValidateOnStart`. On failure the app writes one stderr line listing the invalid
**keys only** and exits with code 1. `--check-config` validates and exits 0/1 without starting Kestrel.

| Key | Rule |
| --- | --- |
| `App:PublicOrigin` | absolute URI, no path; https unless Development |
| `App:KnownProxies` | optional IP/CIDR list |
| `Oidc:Authority` | absolute https URI (http only in Development), ≤ 200 chars |
| `Oidc:IdpLogout` | `ClientId` (default) or `None` (§6.4) |
| `Oidc:ClientId`, `Oidc:ClientSecret` | required |
| `ConnectionStrings:Subscribed` | required (app role) |
| `ConnectionStrings:SubscribedMigrator` | required for `admin db migrate\|patch`, `admin projections rebuild` |
| `Database:Schema`, `Database:AppRole` | `^[a-z_][a-z0-9_]{0,62}$`; schema default `subscribed`; AppRole required for migrate |

Sources: `appsettings.json` (non-secret defaults), environment variables, and key-per-file
`/run/secrets`. There are no secrets in images or the repo.

**Logging.** JSON console.

- **Per request:** method, route template (never the raw path or query), status, duration, traceId.
- **Security events:** login (memberId or "non-member"), callback failure kind, access denied
  (subject), logout, CSRF/Origin rejection, concurrency conflict.
- **Never logged:** cookies, tokens, codes, `state`/`nonce`, the `Authorization` header, query
  strings, connection strings, passwords, secrets, request bodies.

Npgsql parameter logging is off. The OIDC handler's logs are capped at Warning.

## 9. Frontend

Standalone components, signals, `HttpClient` interceptors, SCSS, no UI library. **Pinned versions**
(exact, no ranges in `package.json`; the lock file pins the transitive dependencies):

| Tool / package | Version | Why |
| --- | --- | --- |
| Node.js | `24.11.0` (`.nvmrc`; devcontainer feature `version: "24.11.0"`) | first Node 24 LTS release |
| npm | `11.6.1` (bundled with Node 24.11.0; `packageManager: "npm@11.6.1"`) | |
| `@angular/*`, `@angular/cli`, `@angular/build` | `21.0.0` | Angular 21: Vitest is the default unit-test runner; LTS through May 2027 |
| `vitest` | `4.0.8` | runner for `@angular/build:unit-test` |
| `jsdom` | `27.1.0` | DOM for Vitest; no browser binary |
| `angular-eslint` | `21.0.1` | `ng lint` |
| `typescript` | `5.9.3` | within Angular 21's supported range |

These numbers come from the design author's knowledge of the Angular 21 release (Nov 2025). Like
§4.1, they could **not be checked against the registry in this session** (§17). They are still
exact, not conditional. F1 step 1 is a deterministic existence check:
`npm view @angular/core@21.0.0 version`, and the same for each row. Any non-zero exit is a
**blocker report**. The worker never substitutes another version. Upgrading to a later 21.x patch,
for security fixes, is a separately reviewed change.

**Scaffold command (F1):**
`npx -y @angular/cli@21.0.0 new subscribed --directory frontend --style=scss --routing --ssr=false
--test-runner=vitest --skip-git --skip-install --package-manager=npm`. Then pin the versions above
and run `npm --prefix frontend install`.

`angular.json` `test` target: builder `@angular/build:unit-test`, options `{"runner":"vitest"}`. jsdom is
Vitest's environment when no `browsers` option is set.

Behaviour:

- **Session:** `SessionService` loads `/bff/session` at bootstrap into `loading | anonymous |
  not-member | member | unavailable`.
- **Interceptor:** adds `X-CSRF: 1`. 401 → session-expired banner (Sign in returns to the current
  URL). 503/network → error view with Retry + `traceId`.
- **Routes:** `/` (empty home), `/household`, `/access-denied`, `/signed-out` (`?idp=0` note),
  `/welcome` (`authError` messages), and `**`. A guard redirects by session state.
- **Sign in / out:** sign-in is a full-page navigation to `/bff/login?returnUrl=`. Sign-out POSTs,
  then calls `location.assign` with the validated `redirectUrl`.
- **Name form:** rules mirror §5. A 400 maps to the field. A 412 shows a conflict with "Reload latest"
  and keeps the input. Save is disabled while pending.
- **Copy reference:** "Copy reference" uses `navigator.clipboard`. On failure it selects the text and
  announces manual copy.
- **Layout:** ≥ 900 px uses a sidebar. < 900 px uses a top bar with a menu disclosure
  (`aria-expanded`/`aria-controls`, Escape closes). Supports widths down to 320 px.
- **WCAG 2.2 AA:**
  - landmarks, and a skip link that is visible on focus and focuses `main`;
  - visible focus, with focus moving to `h1` on route change;
  - loading uses `role="status"`, with skeletons `aria-hidden` under `aria-busy`;
  - errors use `role="alert"`;
  - inputs are labelled, with `aria-describedby`/`aria-invalid` and an error summary;
  - contrast ≥ 4.5:1, reduced motion respected, targets ≥ 44 px.

`preview.html` illustrates these states; this section is the spec.

## 10. Deployment

### 10.1 Image
`deploy/Dockerfile` is a multi-stage build: `node:24` builds the SPA, `dotnet/sdk:11.0` publishes, and
the runtime is `dotnet/aspnet:11.0` (`-noble-chiseled` if published). It runs as a non-root UID on
port 8080, with the SPA in `wwwroot`, on a read-only root filesystem.

### 10.2 Compose (separate concerns)
- **`deploy/compose.yml` (production):** the `subscribed` service only. Endpoints come from
  `deploy/.env` (from `.env.example`, gitignored), and secrets are Docker secrets. There are no PG or
  Keycloak containers. A `migrate` service (profile `tools`) is included.
- **`deploy/compose.dev.yml` (local only):** a `keycloak` `start-dev` service on port 8180. It imports
  `deploy/keycloak/dev-realm.json`, which holds the client only (**no users or passwords**).
  `scripts/dev-keycloak-users.sh` creates synthetic users with random passwords, printed to the
  terminal only.

### 10.3 Least privilege (exact grants)
`deploy/postgres/provision.sql` is a template the DBA runs; we never run it on shared servers. It
creates:

- database `subscribed` (preferred), or a schema in a shared DB;
- role `subscribed_migrator`, which owns the schema;
- role `subscribed_app` (LOGIN, schema `USAGE`, no `CREATE`).

It also runs `REVOKE ALL ON SCHEMA <schema> FROM PUBLIC`. Per-object grants live in **one file**,
`backend/src/Subscribed.Platform/Database/grants.sql`. `admin db migrate` applies it, and so does the
test fixture (§14.1).

**The grant list is part of B-1.** The source of `grants.sql` depends on which store §4.1 selects:

- **Custom store:** the table in §5.1. It is exact by construction.
- **Marten:** the `GRANT` list recorded by E5, verbatim. Until E5 runs, no Marten grant list is
  normative. The design author's recollection of Marten's generated objects is **unverified**. That
  includes the progression table name (`mt_event_progression`), whether documents are written through
  generated `mt_upsert_*` functions or through `INSERT … ON CONFLICT`, and whether the append path or
  the tombstone handling issues UPDATEs on `mt_events`. A draft table based on that recollection was
  removed in rev. 3 so that it cannot be mistaken for evidence.

Invariants for either store. They are checked by readiness (§8), AC19, and AC20:

| Rule | Consequence if violated |
| --- | --- |
| App role: no DELETE, TRUNCATE, REFERENCES, TRIGGER, or CREATE on any object | Marten rejected under §4.1, or a custom-store bug |
| No UPDATE on the events table | Marten rejected under §4.1 |
| Every function the app executes is `SECURITY INVOKER` and owned by the migrator | Marten rejected under §4.1 |
| `grants.sql` is the single source; readiness checks every grant in it **and** the absence of the forbidden ones | readiness 503 |

### 10.4 Deployment steps (`deploy/README.md`)
With `C="docker compose -f deploy/compose.yml"`:

1. The DBA runs `provision.sql`; the realm admin creates the client (§6.7).
2. Fill in `deploy/.env`; put secrets in `deploy/secrets/` (gitignored, mode 600).
3. **Back up**, then `$C --profile tools run --rm migrate`.
4. `$C up -d`; wait for `/health/ready` 200.
5. First install only: `$C run --rm subscribed admin household init …`.
6. **Sign-out check:** sign in as a household member, sign out, and confirm that Keycloak's
   confirmation page returns to `<PublicOrigin>/signed-out`. If Keycloak shows an error instead, fix
   the client's *Valid post logout redirect URIs* (§6.7), or set `Oidc:IdpLogout=None` and restart
   (§6.4).

Upgrades: back up → migrate → replace. Readiness stays 503 until the schema and grants match.

### 10.5 Backup/restore
The events are the source of truth; the view can be rebuilt; the DP keys live in the same schema.
Back up with `pg_dump -Fc -n <schema>`. To restore: stop the app → restore into an empty schema as the
migrator → `admin db migrate` (re-grants) → `admin projections rebuild`.

Events written after the backup are lost. Sessions survive only if the DP keys were restored. Backups
hold subjects and key material, so they must be encrypted and access-restricted.

## 11. Dev environment provisioning (slice P0)

`.devcontainer/**` changes, pinned, with `devcontainer-lock.json` updated:

- **.NET SDK 11.0.100:** the base image `devcontainers/dotnet` 11.0 tag if published, else the
  `features/dotnet` feature with `version: 11.0.100`. `dotnet --version` must match `global.json`.
- **Node 24.11.0:** `features/node` with `version: "24.11.0"` (§9).
- **Docker:** `features/docker-outside-of-docker` installs the **CLI + compose plugin** and mounts the
  **host** socket. Host Docker access is a separate prerequisite: Docker Engine/Desktop must run on
  the host (with WSL2 integration). The CLI alone cannot build. `docker info` is the check.
- **GitHub CLI:** `features/github-cli`, for publication only.
- **Environment file:** `.devcontainer/.env.example` holds placeholder `POSTGRES_USER/PASSWORD/DB` and
  a commented `SUBSCRIBED_TEST_DB`. The `app` service gets `env_file: .env`.
- **Cleanup:** remove the stale comments and the dead `init-keycloak-db.sh` reference.
- **Ports:** forward 4200, 5080, 8180.

Local run:

1. `docker compose -f deploy/compose.dev.yml up -d keycloak`
2. `dotnet run --project backend/src/Subscribed.Api` (`App:PublicOrigin=http://localhost:4200`)
3. `npm --prefix frontend start` (proxies `/api`, `/bff`, `/signin-oidc`, `/health` → `localhost:5080`)

## 12. File ownership

| Owner | Paths |
| --- | --- |
| **Frontend (Codex)** | `frontend/**` (incl. `package.json`, lock file, `angular.json`, `proxy.conf.json`, `.nvmrc`) |
| **Backend (Claude)** | `backend/**`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `deploy/**`, `scripts/dev-keycloak-users.sh`, `scripts/integration-check.sh`, `.github/workflows/**`, root `README.md`, `.devcontainer/**` (P0 only), root `.gitignore` |
| **Design (frozen)** | `docs/agentic/project-foundation/**` |
| **Untouched** | `scripts/agentic_flow.py`, `tests/**`, `.agents/**`, `.claude/**`, `docs/agentic-flow.md`, `LICENSE`, `.vscode/**` |

Shared contracts fixed here: §7 shapes and codes, `X-CSRF`, the `redirectUrl` rules, `__CSP_NONCE__`,
SPA output `frontend/dist/subscribed/browser`, proxy paths/ports, and the `package.json` scripts
`lint` = `ng lint`, `test:ci` = `ng test --watch=false`, `build` = `ng build --configuration
production`.

A role that needs a change reports a blocker instead of editing the other side.

## 13. Delivery slices and gates

| Slice | Scope | Run |
| --- | --- | --- |
| P0 | §11 provisioning; rebuild the container | `dev-toolchain` |
| B1 | G0 first (if applicable); solution, Platform, Household, event-store config, `grants.sql`, admin CLI, TestSupport, persistence/unit tests (AC1-AC7, AC19, AC20) | this run, backend |
| F1 | Angular workspace and all §9 states against mocked HTTP (AC16, AC17) | this run, frontend |
| B2 | Config validation, BFF/OIDC, CSRF/headers, API, health, API tests (AC8-AC15, AC12b, AC21) | `foundation-api` |
| B3 | Dockerfile, compose, `provision.sql`, deploy docs, CI, `integration-check.sh` (AC18) | `foundation-deploy` |

Gates:

- **B-1 (E1-E6):** in the design stage, before approval. This design cannot be approved while B-1
  is open.
- **G0:** at B1 start, only for the "compatible, not declared" outcome.
- **D1, D2:** §16.

A slice whose diff would exceed 60 KB is split, never truncated.

## 14. Tests and acceptance criteria

### 14.1 Services and DB fixture
Required: PostgreSQL 18 (the devcontainer `db` service, auto-started, at `localhost:5432`; a
`postgres:18` service in CI), SDK 11.0.100, Node 24.11.0, and Docker host access (B3 only). Keycloak is not
needed: OIDC tests use an in-process stub IdP with a per-run signing key.

`SUBSCRIBED_TEST_DB` is an Npgsql connection string for a role with `CREATEDB` and `CREATEROLE` (the
devcontainer `POSTGRES_USER`). If it is unset, the fixture builds
`Host=localhost;Port=5432;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD;Database=$POSTGRES_DB`.
If neither is available, or the server is unreachable within 30 s, the tests **fail** (never skip)
with a message naming `SUBSCRIBED_TEST_DB`.

**Once per test run,** the fixture:

1. creates the roles `subscribed_test_<8hex>_migrator` and `…_app` with random passwords;
2. creates the template database `subscribed_test_<8hex>_tpl`;
3. migrates it and applies `grants.sql` as the migrator;
4. closes every connection to it (`Pooling=false`).

**Per test,** it creates a fresh database with
`CREATE DATABASE subscribed_test_<8hex>_<n> TEMPLATE subscribed_test_<8hex>_tpl`. Schema, functions,
and grants are copied. The test runs as `_app`, and the database is dropped with `WITH (FORCE)` when
the test is disposed. At the end of the run, the fixture drops the template and both roles. An
`AppDomain.ProcessExit` hook and a startup sweep of `subscribed_test_*` objects older than 24 h
remove anything left behind by a crash.

Consequences:

- **Isolation by construction.** Every test, including those that use the single
  `household-default` stream, has its own database. xUnit's parallel execution is safe, and no test
  observes another's events, views, or triggers.
- **Production privilege model.** Every persistence and API test runs as `_app` with exactly
  `grants.sql`. Only fixture steps and AC3/AC4 setup use the migrator.

### 14.2 Acceptance criteria

| ID | Criterion (measurable) | Suite |
| --- | --- | --- |
| AC1 | Append to new `household-default`, rehydrate → identical state; version = event count | Persistence |
| AC2 | (a) two sessions at version n both append → one commits, the other gets `ConcurrencyConflict`, and the stream gains exactly one event; (b) stale explicit version → conflict; (c) concurrent stream creation → conflict; no raw library exception escapes `Map`; the exception types asserted are the E6 results (Marten) or 23505 `events_pkey`/`streams_pkey` (§5.1) | Persistence |
| AC3 | Fault injection in the test's **own database** (§14.1). The migrator creates a test-only `BEFORE INSERT OR UPDATE` trigger on the view table that raises when `data->>'name'='FAIL-INJECT'`. That rename throws, and the stream version, event count, and view are unchanged. The trigger is dropped in `finally`, and the database is dropped on dispose, so no other test can encounter it, even in parallel | Persistence |
| AC4 | Delete the view docs (migrator), run rebuild → document equals the pre-delete one | Persistence |
| AC5 | Host A writes and is disposed; a new host B on the same DB rehydrates and reads identical values | Persistence |
| AC6 | Golden JSON fixtures of all v1 events deserialize; an unknown alias fails loudly | Unit |
| AC7 | §5 invariants | Unit |
| AC8 | `GET /api/household`: anonymous → 401 with no `Location`; non-member → 403; member → 200 + ETag; revoked → 403 on the next request | Api |
| AC9 | PUT: stale If-Match → 412 + `currentVersion`; missing → 428; invalid → 400 `errors.name`; valid → 200 and version + 1 | Api |
| AC10 | Missing `X-CSRF` on `/api/household`, `/bff/session`, `/bff/logout` → 403 `csrf-header-required`; foreign Origin on PUT and logout → 403 `origin-mismatch`; no `Access-Control-Allow-*` in any response of the suite | Api |
| AC11 | `/bff/login` → 302 with S256, `response_type=code`, `response_mode=query`, `redirect_uri` from PublicOrigin despite a spoofed `Host`; `//x`, `/\x`, `https://x`, and 513-char returnUrls → 400; already signed in → 302 returnUrl | Api |
| AC12 | Logout signed in → cookie cleared, `idpLogout:true`, `client_id` + `post_logout_redirect_uri`, **no** `id_token_hint`; `Oidc:IdpLogout=None` or anonymous → `/signed-out?idp=0`. With the stub IdP issuing a 255-char `sub` and a 5,000-char `name`, exactly one `__Host-subscribed` `Set-Cookie` is sent (no `C1`/`C2` chunks), its value is ≤ 4,050 chars, the ticket's `name` is 64 code units, and the ticket holds no token. A 256-char `sub` → `callback-failed` | Api |
| AC12b | With `FakeTimeProvider`: at 1 h 59 min idle → 200, and the renewed `ExpiresUtc` is ≤ `issued_at + 12 h`. At 2 h 01 min idle → 401 and a deleting `Set-Cookie`. With activity every hour, at 11 h 59 min → 200 and at 12 h 01 min → 401 `unauthenticated` with a deleting `Set-Cookie`. In both expired cases `/bff/session` → `anonymous`. A missing or future `issued_at` → treated as expired | Api |
| AC13 | Run the built `Subscribed.Api.dll` as a **child process** (`dotnet exec`, Production, invalid keys, 30 s timeout) → exit code 1, stderr names the keys and contains no sentinel value; `--check-config` with valid config → exit 0 | Api |
| AC14 | `/health/ready` 503 for an unreachable DB (unused port), an unmigrated schema, or a missing grant; 200 when ready with the stub IdP stopped | Api |
| AC15 | Captured Api-suite logs contain no **forbidden value**: sentinel secret `SENTINEL-CLIENT-SECRET-<run>`, DB passwords, connection strings, any seen cookie value, the stub IdP's code/tokens/`state`/`nonce`, `Password=`, or `/signin-oidc` query strings | Api |
| AC16 | Each session state renders the right view; 403/503/network → `unavailable` (never anonymous); `X-CSRF` added; 401 → expired banner; `authError` messages; validation, 400 mapping, 412; logout `redirectUrl` allow-list | Frontend |
| AC17 | Labelled inputs, `aria-invalid`, focus to `h1` on navigation, skip link focuses `main`, live regions, menu `aria-expanded` | Frontend |
| AC18 | Image builds, user is non-root, ready against an ephemeral PG, `/household` → `index.html`, `/api/x` → 404 problem | Integration |
| AC19 | Full Persistence and Api suites pass as `_app` with exactly `grants.sql` (§10.3) | Persistence, Api |
| AC20 | As `_app`: DELETE/UPDATE/TRUNCATE on the events table and CREATE in the schema → SQLSTATE 42501 | Persistence |
| AC21 | Tampered `state`, missing correlation cookie, or wrong nonce → 302 `authError=callback-failed` and no session cookie; `error=access_denied` → `cancelled`; callback works without `X-CSRF` | Api |

### 14.3 Exact validation commands (argv, from the worktree root)

Toolchain: .NET SDK `11.0.100` (`global.json`, `rollForward: latestPatch`), Node `24.11.0`, npm
`11.6.1`, and the §9 package versions. Tests use xUnit v3 through the VSTest adapter, so
`dotnet test` takes the solution directly.

**Starting PostgreSQL 18 for the backend tests.** Pick exactly one of these:

```sh
# (A) Inside the devcontainer: the `db` service starts with the container. The app container gets
#     POSTGRES_USER/PASSWORD/DB from .devcontainer/.env (P0). Set the variable explicitly:
export SUBSCRIBED_TEST_DB="Host=localhost;Port=5432;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD;Database=$POSTGRES_DB"

# (B) Outside the devcontainer (host with Docker): ephemeral server, loopback only, random password
PGPW="$(openssl rand -hex 16)"
docker run -d --rm --name subscribed-test-pg -e POSTGRES_PASSWORD="$PGPW" -p 127.0.0.1:55432:5432 postgres:18
until docker exec subscribed-test-pg pg_isready -U postgres -q; do sleep 1; done
export SUBSCRIBED_TEST_DB="Host=127.0.0.1;Port=55432;Username=postgres;Password=$PGPW;Database=postgres"
# ...run the backend commands below, then:
docker stop subscribed-test-pg

# (C) CI: a `postgres:18` job service; the workflow sets SUBSCRIBED_TEST_DB from a generated
#     password with the same format as (B), on port 5432
```

**Commands:**

```sh
# Frontend (F1): Vitest + jsdom, no browser
npm --prefix frontend ci
npm --prefix frontend run lint
npm --prefix frontend run test:ci
npm --prefix frontend run build
# Backend (B1, B2): SUBSCRIBED_TEST_DB exported as above
dotnet build backend/Subscribed.slnx -c Release -warnaserror
dotnet test backend/Subscribed.slnx -c Release --no-build
dotnet format backend/Subscribed.slnx --verify-no-changes
# Integration (B3): host Docker daemon + the above
bash scripts/integration-check.sh
# Orchestrator regression
python3 -m unittest discover -s tests -p 'test_agentic_flow.py' -v
```

`scripts/integration-check.sh` is normative. It runs with `set -euo pipefail`; an EXIT `trap` removes
everything it created; it writes no files to the worktree. Steps:

1. `cd` to the repo root. Require the DB env and `docker info`; otherwise exit 2.
2. Run the frontend `ci` + `build` and the backend `build` + `test` exactly as above.
3. `docker build -f deploy/Dockerfile -t subscribed:it-$$ .`. `docker image inspect -f
   '{{.Config.User}}'` must be non-empty and not `root`/`0`.
4. On a new network `subscribed-it-$$`, start `postgres:18` with a password from `/dev/urandom`
   (never echoed), and wait for `pg_isready`.
5. Pipe `provision.sql` to its `psql`. Run the image's `admin db migrate` as the migrator.
6. Run the image as the app role with `App__PublicOrigin=https://app.example.invalid`,
   `Oidc__Authority=https://idp.example.invalid`, and generated synthetic client credentials.
7. Probe from `curlimages/curl:8.11.1` on the same network:
   - `/health/ready` → 200 within 60 s;
   - `/household` (`text/html`) → 200 containing `<app-root`;
   - `/api/x` → 404 problem+json.

Exit 0 only if all steps pass.

Manual E2E (dev Keycloak, synthetic users): non-member denied → grant → home; two-tab rename conflict;
IdP cancel; sign out via the confirmation page; session survives an API restart.

## 15. Risks

| Risk | Mitigation |
| --- | --- |
| No .NET 11 support | B-1 (E1-E4) before approval; G0; §5.1 fallback; no downgrade |
| Library needs unexpected privileges or throws undistinguishable errors | E5/E6 before approval; the decision rule rejects Marten; AC19/AC20 |
| Keycloak rejects hint-less logout | §6.7 explicit URI; §10.4 step 6; `Oidc:IdpLogout=None` |
| Pinned frontend versions do not exist as recalled | F1 step 1 existence check → blocker, no substitution |
| Code in callback URL | D-G mitigations; reverse-proxy query logging disabled (deploy docs) |
| 60 KB cap / base moves | Slices, D1, D2 |
| Lock-out / stale sessions / realm recreation | Last-member rule + admin CLI; 12 h cap; re-grant procedure |
| Personal data in events / DP keys in backups | Only issuer/subject/label; encrypted, restricted backups |
| SonarQube workflow on .NET 11 | B3 `setup-dotnet` + explicit solution |

## 16. Decisions requested from the human

- [ ] **B-1 (§4.1), required first:** run E1-E6, or grant a design session network, Docker, and
  `dotnet` permission to run them. Record the results in §4.1, §5, and §10.3, and have the document
  re-reviewed. Then apply the decision rule: Marten verified, **or** tick "compatible, not declared"
  with G0, **or** use the §5.1 fallback.
- [ ] **D1 (recommended):** approve the design → run P0 `dev-toolchain` and merge it → start the
  implementation runs from the updated base.
- [ ] **D2 (recommended):** a separately reviewed orchestrator change that reviews `package-lock.json`
  by SHA-256 + `npm ls --depth=0` instead of the inline diff. Alternative: a human-reviewed scaffold
  commit.
- [ ] Confirm D-D (app-owned membership) and D-G (`response_mode=query` + the logout-confirmation
  trade-off).

## 17. Verification record of this design session (rev. 3)

**Attempted, and denied by tool permissions.** Nothing below was retried by other means.

| Attempt | Outcome |
| --- | --- |
| WebFetch `api.nuget.org/v3-flatcontainer/marten/index.json` | permission not granted |
| WebSearch (Marten / .NET 11) | permission not granted |
| `curl` to the same NuGet URL | requires approval; denied in this non-interactive session |
| `dotnet --list-sdks` | requires approval; denied |
| List `~/.nuget/packages` (local cache) | outside the allowed directories; blocked |
| `python3 -I -c …` static parser/structure check of `preview.html` | requires approval; denied (only `python3 --version` ran: 3.12.3) |

**Consequences.**

- B-1 stays open, and §4.1 E1-E6 are unfilled.
- The Keycloak and OIDC citations (§6.4) and the frontend versions (§9) are marked as
  author-recalled, each with a deterministic fallback or check.

**`preview.html` verification.** This was a manual source review only. The mockup was **not**
rendered in a browser: no browser tooling or Node was available, and the Python check was denied. No
screenshots exist, and the mockup makes no claim of having been rendered. What the review checked:

- **Skip link.** The frame click handler tests `.skip` **before** the generic `a,button` "not
  connected" branch, and returns early. It always focuses `#main`. In `solo()` layouts, `#main` is
  the `.panel` element with `tabindex="-1"`, so focus lands on the panel and is not intercepted.
  Rev. 3 adds the skip link to the loading state, which previously lacked one. Every state now
  renders `SKIP` and a `#main` element.
- **Toolbar.** All buttons are created with `tabIndex=-1`. `render()` runs synchronously at the end
  of the same script and sets `tabIndex=0` on the pressed button. An unknown or empty `location.hash`
  falls back to `S[0]` (loading). Rev. 3 adds a `hashchange` listener so that manual hash edits
  re-render.
- **Labelling.** The page banner, every frame `<title>`, and a fixed watermark label the material as a
  synthetic mockup. There are no external URLs or resources. All data is synthetic.

**Required before human sign-off of the visual preview:** open `preview.html` in a browser; step
through all 13 states at the desktop and mobile widths; check that the browser console shows no
errors; and Tab to the skip link in a `solo` state and in a `shell` state.

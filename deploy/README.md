# Running Subscribed with Docker

One container serves the Angular SPA and the API/BFF from one origin, behind a TLS-terminating
reverse proxy. PostgreSQL (≥ 16) and Keycloak (≥ 18) are **external**: the Compose file starts only
Subscribed, not a database or identity provider. Subscribed includes sign-in, household membership
and household naming, and household subscription tracking: add, edit, end, reinstate and remove
subscriptions, with price history and per-currency monthly and yearly totals.

## Before you start

You need:

* Docker Engine with Docker Compose v2 or later (`docker version`, `docker compose version`).
* A Linux amd64 host for the published images.
* A reachable PostgreSQL server, with permission for an administrator to provision a database.
* A reachable Keycloak realm and permission to configure a confidential client.
* An HTTPS hostname and a reverse proxy that terminates TLS.
* This repository checked out on the Docker host. Run **all commands below from its root**.

No local Node or .NET installation is needed to run or build the application with Docker.
The application stores its events, read models and session-encryption keys in PostgreSQL;
it needs no application data volume.

```text
Browser -- HTTPS --> reverse proxy -- HTTP --> Subscribed :8080
                                               |       |
                                          PostgreSQL  Keycloak (HTTPS)
```

This guide assumes a reverse proxy running on the Docker host, forwarding to `127.0.0.1:8080`.
A proxy in another container cannot reach that address as its own localhost. In that case, adapt
the networking to a shared Docker network and proxy to `subscribed:8080`; do not expose the
backend publicly without TLS.

## 1. Choose an image

**Published release (recommended):** copy the full `ghcr.io/kimipsen/subscribed@sha256:...`
reference from a GitHub Release's `IMAGE_DIGEST.txt`. Alternatively, use
`ghcr.io/kimipsen/subscribed:latest` for the most recently built stable release. The release
workflow must have run successfully before that image exists. For a private package, authenticate
with `docker login ghcr.io` using credentials with package-read access; public packages need no login.

**Build locally instead:**

```sh
docker build -f deploy/Dockerfile -t subscribed:local .
```

For a local build, use `SUBSCRIBED_IMAGE=subscribed:local` in the next step and skip the image
pull command. Docker downloads the build SDKs and dependencies.

## 2. Configure endpoints

```sh
cp -n deploy/.env.example deploy/.env
install -d -m 700 deploy/secrets
```

Edit `deploy/.env` (do not overwrite an existing configuration):

```dotenv
SUBSCRIBED_IMAGE=ghcr.io/kimipsen/subscribed@sha256:<digest-from-release>
SUBSCRIBED_PORT=8080
PUBLIC_ORIGIN=https://subscribed.example.com
KNOWN_PROXIES=<proxy-IP-as-seen-by-the-container>
OIDC_AUTHORITY=https://identity.example.com/realms/household
OIDC_CLIENT_ID=subscribed
OIDC_IDP_LOGOUT=ClientId
DB_SCHEMA=subscribed
DB_APP_ROLE=subscribed_app
```

Replace every placeholder. `PUBLIC_ORIGIN` is just the HTTPS origin, with no path or query.
`OIDC_AUTHORITY` is the realm issuer, not its authorization endpoint. Both the browser and the
container must reach Keycloak using that issuer URL. Database hosts in connection strings must
be reachable **from the container**; `localhost` means the container itself, not the Docker host.

Set `KNOWN_PROXIES` to the exact trusted proxy IP(s) or narrowly scoped CIDRs. The backend trusts
forwarded headers only from these addresses; an empty value disables forwarded-header handling.
The source address of a host-side proxy may be a Docker bridge gateway, not `127.0.0.1`.

The reverse proxy must preserve the public host, send `X-Forwarded-Proto: https` and
`X-Forwarded-For`, and **not log query strings**: the sign-in callback contains a single-use code.
It must also **not log the path of `/calendar/`**: the private calendar feed URL carries its secret in the path
(see [Calendar reminders](../backend/README.md#calendar-reminders)).
Do not use plain HTTP as the public origin with the production Compose configuration.

### Reverse proxy settings for MCP

The MCP server answers on `/mcp` (see [docs/mcp.md](../docs/mcp.md)). Agents connect to it through the public host, so the
proxy must:

* **forward `/mcp` to the same upstream** as everything else (`subscribed:8080`); there is no separate MCP service;
* **not buffer responses** for `/mcp`: a response is a `text/event-stream`. The app sends `X-Accel-Buffering: no`, which nginx
  honours (or set `proxy_buffering off` for the `/mcp` location); other proxies need the equivalent setting, which you must
  verify with a real client;
* **allow long reads**: a read timeout of a few minutes, not the 30 or 60 seconds some proxies default to;
* **not log the `Authorization` header** (or any request header) of `/mcp` requests: the header **is** the credential, and
  a proxy log, trace or WAF capture would keep every token in clear text. Use the same care for any log shipper in front of
  the app;
* **rate limit failed authentication**. The app does not rate limit `/mcp`: every request with a wrong token is a `401` and
  one household read. Limit repeated `401` responses per client address at the proxy.

A request body larger than 64 KB on `/mcp` is rejected by the app with `413`; do not raise your proxy's limit above that for `/mcp`.

Every command below supplies `--env-file deploy/.env` explicitly. Do not rely on Compose
automatically finding that file from the repository root.

## 3. Provision PostgreSQL and configure Keycloak

Have the DBA review [`postgres/provision.sql`](postgres/provision.sql) and run it using `psql`
with `ON_ERROR_STOP=1` and the `migrator_password` and `app_password` variables. The template creates:

* Database `subscribed`.
* Role `subscribed_migrator`: owns the schema and performs migrations/rebuilds.
* Role `subscribed_app`: the restricted runtime account.

Use distinct randomly generated passwords supplied through your administrator's secure workflow.
This is a **first-install template**, not a repeatable migration; do not run it blindly on a
shared server or existing installation. Per-object application grants are applied in step 5,
from [`grants.sql`](../backend/src/Subscribed.Platform/Database/grants.sql). Keep the database
behind a firewall that permits only the required application and administrator connections.

In Keycloak, create an OpenID Connect client matching `OIDC_CLIENT_ID`:

| Setting | Value |
| --- | --- |
| Client authentication | On (confidential client) |
| Standard flow | On |
| PKCE method | S256 |
| Implicit flow / direct access grants | Off |
| Valid redirect URIs | `https://subscribed.example.com/signin-oidc` |
| Valid post logout redirect URIs | `https://subscribed.example.com/signed-out` |
| Web origins | Empty (no CORS is used) |

Use your actual `PUBLIC_ORIGIN` in both redirect URIs, without wildcards. Copy the client's
generated secret for the next step. Create the intended users in the realm if they do not
already exist. A Keycloak user is **not automatically a household member**.

## 4. Create the secret files

Compose mounts three files as configuration secrets. File contents are the raw value, not
`KEY=value`, not JSON, and not surrounded by quotes.

| File under `deploy/secrets/` | Contents |
| --- | --- |
| `oidc_client_secret` | Keycloak confidential client secret |
| `app_connection` | Npgsql connection string for `subscribed_app` |
| `migrator_connection` | Npgsql connection string for `subscribed_migrator` |

Connection string shape (substitute the actual host, password and role):

```text
Host=db.example.com;Port=5432;Database=subscribed;Username=subscribed_app;Password=<password>;SSL Mode=VerifyFull
```

`VerifyFull` requires a database certificate whose hostname and CA the container trusts.
For a private CA, arrange a trusted CA mount/configuration before connecting; do not solve
certificate errors by disabling verification. Follow your DBA's approved TLS configuration.
Quote/escape special connection-string characters as required by Npgsql.

On a Linux host using ordinary rootful Docker, this Bash snippet prompts without putting
secret values in shell history or printing them:

```bash
(
  set -euo pipefail
  umask 077
  read -rsp 'Keycloak client secret: ' value; printf '\n'
  printf '%s' "$value" > deploy/secrets/oidc_client_secret
  read -rsp 'Application Npgsql connection string: ' value; printf '\n'
  printf '%s' "$value" > deploy/secrets/app_connection
  read -rsp 'Migrator Npgsql connection string: ' value; printf '\n'
  printf '%s' "$value" > deploy/secrets/migrator_connection
  unset value
  sudo chown 1654:1654 deploy/secrets/oidc_client_secret \
    deploy/secrets/app_connection deploy/secrets/migrator_connection
  sudo chmod 400 deploy/secrets/oidc_client_secret \
    deploy/secrets/app_connection deploy/secrets/migrator_connection
)
```

The image runs as UID 1654. Local Compose secrets are bind-mounted files, not encrypted Swarm
secrets; their host ownership must allow that UID to read them. Rootless Docker/user-namespace
remapping requires the corresponding mapped host UID instead. See the
[secrets layout](secrets.example/README.md). Never commit or share the real files or `deploy/.env`.
The running application mounts only its own connection and the OIDC secret, not the migrator's.

## 5. Migrate and start

For a published image:

```sh
docker compose --env-file deploy/.env -f deploy/compose.yml --profile tools pull
```

Back up an existing database before migrating. For a first installation, run the migration once
the roles/schema have been provisioned:

```sh
docker compose --env-file deploy/.env -f deploy/compose.yml --profile tools run --rm migrate
docker compose --env-file deploy/.env -f deploy/compose.yml up -d subscribed
docker compose --env-file deploy/.env -f deploy/compose.yml ps
curl -fsS http://127.0.0.1:8080/health/live
curl -fsS http://127.0.0.1:8080/health/ready
```

Use your chosen `SUBSCRIBED_PORT` instead of 8080 if changed. The migrator should exit 0.
Readiness returns 503 until the database, schema version and exact privileges are correct.
It can return 200 with overall status `Degraded` when Keycloak is unavailable; that means
the database is ready, **not** that sign-in works. Docker's container healthcheck tests liveness,
not readiness.

## 6. Grant the first household member

Open `PUBLIC_ORIGIN` in a browser and sign in. An authenticated user without membership sees
access denied and their own subject identifier. Copy that **subject**, not the username/email.

Initialize the household exactly once, using that subject:

```sh
docker compose --env-file deploy/.env -f deploy/compose.yml run --rm subscribed \
  admin household init --name "Our household" \
  --owner-subject "<Keycloak subject>" --owner-label "First member"
docker compose --env-file deploy/.env -f deploy/compose.yml run --rm subscribed \
  admin household show
```

Reload the application. The member should now see the household page. Additional users must
sign in to obtain their subject, then an operator grants access:

```sh
docker compose --env-file deploy/.env -f deploy/compose.yml run --rm subscribed \
  admin household grant --subject "<another Keycloak subject>" --label "Another member"
```

There is no self-registration. Membership changes are checked on subsequent requests.
Sign out and confirm Keycloak returns to `<PUBLIC_ORIGIN>/signed-out`. If its logout page reports
an error, check the exact post-logout redirect URI; `OIDC_IDP_LOGOUT=None` intentionally selects
local logout only (restart the app after changing configuration).

## Operating the application

```sh
# Inspect service logs (treat logs as operational data; do not publish them unreviewed).
docker compose --env-file deploy/.env -f deploy/compose.yml logs --tail=100 subscribed

# Stop services without changing the external PostgreSQL database.
docker compose --env-file deploy/.env -f deploy/compose.yml down

# Recreate the application after endpoint/secret changes.
docker compose --env-file deploy/.env -f deploy/compose.yml up -d --force-recreate subscribed
```

### Upgrade

1. Back up PostgreSQL and retain the current image digest for rollback planning.
2. Set `SUBSCRIBED_IMAGE` to the new release digest in `deploy/.env`.
3. Pull the image, stop `subscribed`, and run `migrate` using the step 5 commands.
4. Start `subscribed`; verify readiness, sign-in, household access and sign-out.

The release that adds subscription tracking moves the schema from version 1 to **version 2** (one new
document table, `mt_doc_subscription_view`, and one new grant). `migrate` must run before the app
starts: readiness returns 503 until the database is at schema version 2.

The release that adds the MCP server moves the schema from version 3 to **version 4** (one new
document table, `mt_doc_mcp_token_use`, and one new grant on it). The **migrator must run first**: `admin db migrate`
creates the table through the migrator role and re-applies `grants.sql`, and `/health/ready` returns 503 until the database is
at schema version 4. There is no SQL migration file. Start the new app only after `migrate` exited 0.

Do not assume an older image can run against a newer schema. Test database restoration before
relying on rollback.

### MCP rollout is one-way once a token exists

The MCP server stores its tokens in the household event stream as two new events, `household.mcp_token_created.v1` and
`household.mcp_token_revoked.v1`. An older application version does not know these aliases: when it loads the household stream
after the first token was created it throws `UnknownEventAliasException`, and that takes down **the whole web app**, not
only MCP. Therefore:

* **"Deploy the previous image" is NOT safe once a token has been created.** Neither is a restore-free rollback.
* **`Mcp:Enabled=false` does not help.** The flag stops `/mcp` and new tokens, but it does not undo events that were
  already written. An old image still fails on the stream.
* **Rolling deploys:** while an old instance still serves traffic after a new instance wrote the first token event, the old
  instance fails on the household stream. Drain the old instances before anyone can create a token, or keep
  `Mcp__Enabled=false` until **every** instance runs the new version, then enable it.
* **Forward fix, if the new version must be pulled:** build and deploy a **patched older image** that tolerates the two
  unknown household aliases (skips those events, or maps them to a no-op) and run that instead. Alternatively restore the
  backup taken before the new version served traffic; changes made after it are lost.
* **No patched older image exists yet.** The forward-fix image has not been built and this procedure has **never been
  rehearsed**. If you need rollback safety, ship an earlier release that tolerates unknown household aliases first
  (without any MCP code), and only then ship the MCP release.

Back up PostgreSQL before deploying this release, and keep `Mcp__Enabled=false` until the rollout is complete.

### The `Mcp:Enabled` setting

`Mcp:Enabled` defaults to `true`. Set it to `false` to stop new exposure at once: `/mcp` disappears (a `404` on every
method), and creating a token answers `409` `mcp-disabled`. Listing and revoking tokens keep working. The production
`deploy/compose.yml` does not pass this key through; set it for the `subscribed` service in a Compose override file
(`environment: { Mcp__Enabled: "false" }`) or edit the `environment` list, then recreate the container. A household with no
tokens exposes nothing even when the flag is on.

### Backup and restore

Back up with your DBA's `pg_dump -Fc -n subscribed` procedure. Backups contain member subjects
**and session-encryption keys**: encrypt and restrict them. An example filename is `backup.dump`;
keep backups outside the repository. This schema-scoped dump is not a backup of cluster roles;
retain the role-provisioning procedure and protected credentials separately.

To restore: stop the app, have the DBA restore into an empty schema as the migrator, run
`migrate` to reapply grants, then rebuild projections using the service that mounts the
migrator connection:

```sh
docker compose --env-file deploy/.env -f deploy/compose.yml --profile tools run --rm migrate \
  admin projections rebuild
```

Restart and verify readiness. Events written after the backup are lost; session decryption
requires the original Data Protection keys.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `set SUBSCRIBED_IMAGE` / missing variable | Run from the repository root with `--env-file deploy/.env`; replace sample placeholders. |
| Image denied / not found | Confirm the release exists and the digest is correct; authenticate to GHCR if the package is private. |
| `no matching manifest` | Published images currently target `linux/amd64`, not ARM64. |
| Invalid configuration lists secret keys | Check filenames, nonempty contents and UID 1654 read access; do not print secret files to diagnose. |
| Database connection failure | Use a container-reachable host, inspect firewall/DNS/TLS and both role credentials. |
| Readiness 503 | Run the migrator; check schema version and grants. Do not give the app superuser privileges. |
| Readiness `Degraded` with HTTP 200 | Inspect Keycloak connectivity/discovery and TLS; database readiness alone does not prove login works. |
| Keycloak `invalid_redirect_uri` | Register the exact public HTTPS callback, and check `PUBLIC_ORIGIN` and trusted proxy headers. |
| Access denied after login | Grant the exact subject from this realm; do not use username or email. |
| MCP client gets `401` | The token is wrong, was revoked, or its member was removed; every cause answers the same. Create a new token on the household page. |
| MCP client hangs or is cut off | The proxy buffers `/mcp` or has a short read timeout; see the MCP proxy settings above. |
| MCP client gets `404` on `/mcp` | `Mcp:Enabled` is `false`, or the proxy does not forward `/mcp` to the app. |
| Cannot reach port 8080 remotely | Expected: it is loopback-only. Access through the HTTPS reverse proxy. |
| Login/logout fails after configuration changes | Recreate the app container and check issuer, secret and both registered redirect URIs. |

## GitHub container releases

The [container release workflow](../.github/workflows/release.yml) runs on pushed version tags.
It first runs the complete [CI workflow](../.github/workflows/ci.yml), then builds and publishes
the production image to `ghcr.io/kimipsen/subscribed`. Tags must point to a commit on `master`.
Supported versions are `vMAJOR.MINOR.PATCH`, optionally followed by a SemVer prerelease suffix
(for example `v0.1.0-rc.1`); build-metadata suffixes are not supported.

After committing and pushing the workflow and the desired application changes:

```sh
git tag -a v0.1.0 -m "Subscribed v0.1.0"
git push origin v0.1.0
```

The workflow publishes both version forms (`:v0.1.0` and `:0.1.0`) and
`:sha-<full-source-commit>` image tags, for `linux/amd64`. Version tags are derived from the
pushed Git tag, not the frontend package version; `v0.1.0-rc.1` also gets `:0.1.0-rc.1`.
Stable releases also update `:latest`; prereleases do not. This moving tag follows build
publication order, not semantic-version ordering, so publishing an older stable version later
will also move `latest`. It is updated when the image is pushed, before release assets finish
uploading. For reproducible deployments, use the immutable digest from the release as
`SUBSCRIBED_IMAGE` in `deploy/.env`, for example
`ghcr.io/kimipsen/subscribed@sha256:<digest>`.

Each GitHub Release includes:

* `image.sbom.spdx.json`: SPDX inventory of the published image, scanned by its immutable digest.
* `frontend.sbom.spdx.json`: supplementary source/lockfile inventory including development
  dependencies, since the compiled Angular bundle does not retain its dependency metadata.
* `SOURCE_COMMIT.txt`: full source Git SHA.
* `IMAGE_DIGEST.txt`: fully qualified image reference with its SHA-256 manifest/index digest.
* `release.json`: version, commit, digest, platform, build-run URL and SBOM filenames.
* `SHA256SUMS`: SHA-256 checksums of the five files above. Download all six assets into one
  directory and run `sha256sum --check SHA256SUMS`.

Release notes repeat the commit SHA, image digest and digest-pinned pull command. BuildKit SBOM
and maximum-mode provenance attestations are also attached to the image in GHCR. Checksums
detect file corruption; they are not cryptographic signatures.

No PAT is needed: the workflow uses `GITHUB_TOKEN` with package/release write access only in the
publishing job. Repository/organization policies must permit those permissions and GHCR package
creation. A newly created GHCR package can be private; change its package visibility if anonymous
pulls are required. If a package already exists, grant this repository Actions access to it.

The release is created as a draft and only published after every asset uploads successfully.
Prerelease tags produce prereleases. A failed draft upload can be retried; an already published
release is never overwritten. Do not move release tags or rebuild an already published version:
base images can change, so use a new version instead. A failure after pushing the image may leave
an image without a published release; resolve the failed run before using that image.

## Local development only

[`compose.dev.yml`](compose.dev.yml) runs a throwaway Keycloak (`start-dev`, port 8180, loopback)
importing [`keycloak/dev-realm.json`](keycloak/dev-realm.json): the client only, no users, no
passwords. `scripts/dev-keycloak-users.sh` creates synthetic users with random passwords and prints
them and the generated client secret once. Never use it for production.

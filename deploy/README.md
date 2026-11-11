# Deploying Subscribed

One container serves the Angular SPA and the API/BFF from one origin, behind a TLS-terminating
reverse proxy. PostgreSQL (≥ 16) and Keycloak (≥ 18) are **external**; nothing here creates them.

```sh
C="docker compose -f deploy/compose.yml"
```

## 1. Prerequisites (owned by other teams)

* **DBA** reviews and runs [`postgres/provision.sql`](postgres/provision.sql) with two random
  passwords (`psql -v migrator_password=… -v app_password=… -f …`). It creates database
  `subscribed`, role `subscribed_migrator` (schema owner) and `subscribed_app` (LOGIN, schema USAGE,
  no CREATE). Per-object grants are applied later by the migrator from
  `backend/src/Subscribed.Platform/Database/grants.sql`. Never run the template against a shared
  server without review.
* **Realm admin** creates a confidential OIDC client: Standard flow only; PKCE S256 required; valid
  redirect URI `<PublicOrigin>/signin-oidc`; *Valid post logout redirect URIs* set explicitly to
  `<PublicOrigin>/signed-out`; no web origins; direct access grants and implicit flow off.
* **Reverse proxy** terminates TLS, forwards to `127.0.0.1:8080`, sets `X-Forwarded-For/Proto`,
  and **does not log query strings** (the OIDC callback carries a single-use code).

## 2. Configuration

```sh
cp deploy/.env.example deploy/.env          # endpoints only, gitignored
install -d -m 700 deploy/secrets            # gitignored; see secrets.example/README.md
```

Secrets are Docker secrets mounted as key-per-file (`/run/secrets/Oidc__ClientSecret`, …). Set
`KNOWN_PROXIES` to the proxy address so forwarded headers are trusted from it only.

## 3. Build, back up, migrate, run

### GitHub container releases

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

The workflow publishes `:v0.1.0` and `:sha-<full-source-commit>` image tags, for `linux/amd64`.
There is deliberately no moving `latest` tag. Use the immutable digest from the release as
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

### Local build and deployment

```sh
docker build -f deploy/Dockerfile -t subscribed:<version> .     # from the repo root
pg_dump -Fc -n subscribed … > backup.dump                         # back up first
$C --profile tools run --rm migrate                                # schema + grants (migrator)
$C up -d
curl -fsS http://127.0.0.1:8080/health/ready                       # 200 when schema+grants match
```

`/health/ready` stays 503 until the database is reachable, the schema version matches the image and
every privilege in `grants.sql` is present while DELETE/TRUNCATE/UPDATE on the events table, CREATE
on the schema and SECURITY DEFINER functions are absent. A degraded IdP does not fail readiness.

## 4. First install

```sh
$C run --rm subscribed admin household init --name "<household>" --owner-subject "<sub>" --owner-label "<label>"
$C run --rm subscribed admin household grant --subject "<sub>" --label "<label>"
$C run --rm subscribed admin household show
```

A signed-in user who is not a member sees their own subject on the access-denied page and gives it
to the administrator. There is no self-registration.

## 5. Sign-out check (manual)

Sign in as a member, sign out, and confirm Keycloak's confirmation page returns to
`<PublicOrigin>/signed-out`. If Keycloak shows an error instead, fix the client's *Valid post logout
redirect URIs*, or set `OIDC_IDP_LOGOUT=None` and `$C up -d` (local logout only).

## Upgrades, backup and restore

* Upgrade: back up → `$C --profile tools run --rm migrate` → replace the image → `$C up -d`.
* Back up with `pg_dump -Fc -n subscribed`. Backups contain member subjects **and the Data
  Protection keys** (session encryption): encrypt and restrict them.
* Restore: stop the app → restore into an empty schema as the migrator → `migrate` (re-grants) →
  `$C --profile tools run --rm migrate admin projections rebuild` (the `migrate` service is the only
  one that mounts the migrator connection). Events written after the backup are lost; sessions
  survive only if the Data Protection keys were restored.

## Local development only

[`compose.dev.yml`](compose.dev.yml) runs a throwaway Keycloak (`start-dev`, port 8180, loopback)
importing [`keycloak/dev-realm.json`](keycloak/dev-realm.json): the client only, no users, no
passwords. `scripts/dev-keycloak-users.sh` creates synthetic users with random passwords and prints
them and the generated client secret once. Never use it for production.

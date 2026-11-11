# Development-toolchain prerequisite

Status: **corrected coordinator draft; NOT approved**. The automated run stopped
after three attempts. This correction is not a fourth review or an approval.
Earlier author/review snapshots remain in the run logs; counters are unchanged.

## Scope and boundaries

Provision the development environment only. No application scaffolding, API,
authentication, database schema, event-store choice, UI, or external infrastructure
changes. No container rebuild, publication, credential inspection, or database
volume removal without explicit permission.

Keep the existing .NET 10 devcontainer base and the three installed agent
features. Add .NET 11, Node/npm, Python's missing standard-library package, GitHub
CLI, and the Docker client/Compose plugin. Do not mount a Docker socket or start a
daemon. Docker client availability does not establish access to a Docker host.

## Verified evidence

The coordinator checked the following public sources or local commands on
2026-11-11. These establish availability, not a successful container build.

| Item | Observed evidence |
| --- | --- |
| .NET SDK | Official [release metadata][sdk] contains SDK `11.0.100` and Linux x64/arm64 archive URLs with SHA-512 hashes. |
| Node/npm | Official [Node index][node] contains `version=v24.22.2`, `date=2026-10-27`, `npm=11.19.1`, `lts=Krypton`. |
| Angular compatibility | Official [version matrix][angular] lists Angular 22 with Node `^24.15.0`; Node 24.22.2 meets that range. No Angular package is installed in this task. |
| GitHub CLI | GitHub [release API][gh] returned `tag_name=v2.103.3`, `published_at=2026-10-26T19:20:08Z`. |
| Docker CLI | Official Ubuntu Noble amd64 [package index][docker] contains `docker-ce-cli` version `5:29.9.3-1~ubuntu.24.04~noble`. |
| Compose plugin | That same package index contains `docker-compose-plugin` version `5.7.0-1~ubuntu.24.04~noble`. The previous inferred `5.7.1` apt pin is withdrawn. |
| Python failure/fix | The original orchestrator failed with `ModuleNotFoundError: No module named 'json'`. Installing `libpython3.12-stdlib` fixed startup. `dpkg -S /usr/lib/python3.12/json/__init__.py` identifies `libpython3.12-stdlib:amd64`. Imports of `json, encodings, ssl, sqlite3` now pass. |
| Ignore rules | `git check-ignore -q .devcontainer/.env.example` returned 1; the same check for `.devcontainer/.env` returned 0. Neither check requires the path to exist or reads its contents. |
| Dev Container CLI | The npm [package metadata][cli-npm] reports `0.90.0`. Its pinned [lockfile implementation][lock] derives the filename from the config basename: `devcontainer.json` uses `devcontainer-lock.json`, not `.devcontainer-lock.json`. |

These are point-in-time pins. If a pinned artifact is unavailable at
implementation, report the exact missing artifact and stop; do not silently
substitute a version. Docker package evidence above covers amd64 only. Supporting
arm64 requires checking its separate package index before implementing that path.

## Proposed implementation

1. **Python:** install `libpython3.12-stdlib` through apt in the Dockerfile. Do not
   pin the Ubuntu patch version, because security updates replace old packages.
   Record the installed version in validation output.
2. **.NET:** download the exact SDK `11.0.100` archive for the build architecture
   from the official release metadata and check its published SHA-512 before
   extraction. Install into isolated `/opt/dotnet11`; set `DOTNET_ROOT` and put its
   directory first on `PATH`. This avoids relying on unverified shared-directory
   installer behavior. Verify both the version and resolved executable during
   the image build. SDK 10 may remain in its original directory.
3. **SDK selection:** add `global.json` with SDK version `11.0.100` and
   `rollForward: disable`. Until the new SDK is installed, repo-local `dotnet`
   commands will fail, including editor/build tooling. Land this file together
   with setup/rebuild instructions. Static validation must not require this
   deliberate temporary failure; after provisioning the required result is
   always a successful `dotnet --version` equal to `11.0.100`.
4. **Node:** download official Node `24.22.2` for the build architecture, validate
   it against that release's `SHASUMS256.txt`, and expose its bundled `node`, `npm`,
   and `npx` on `PATH`. Do not globally install Angular or app packages.
5. **GitHub CLI:** add the official `github-cli:1` feature with version `2.103.3`.
   Preserve existing agent feature IDs, options, and resolved digests.
6. **Docker client:** configure Docker's official Noble apt repository and install
   only `docker-ce-cli` and `docker-compose-plugin` at the verified package
   versions above. No `docker-ce`, `containerd.io`, privileged mode, or socket
   mount. If daemon access is later required, request separate explicit approval.
7. **Sample configuration:** add `.devcontainer/.env.example` containing obvious
   local-only placeholders for `POSTGRES_USER`, `POSTGRES_PASSWORD`, and
   `POSTGRES_DB`. Never overwrite, print, or copy an existing `.env`. Changing
   initialization values does not change credentials in an existing DB volume.
8. **Compose:** correct only stale comments mentioning an unrelated application
   or nonexistent Keycloak service. Preserve service names, the Compose project
   name, networking, `postgres:18`, `postgres-data:/var/lib/postgresql`, health
   check, and environment-file path. Do not run `down -v`.
9. **Lockfile:** use the official CLI, never invented hashes. With the proposed
   config on a Docker-capable host, run the two commands below. Inspect the
   generated lockfile; reject unexpected changes to existing agent feature
   resolutions. Do not delete the lockfile or accept a second filename.

```sh
npx --yes @devcontainers/cli@0.90.0 build --workspace-folder .
npx --yes @devcontainers/cli@0.90.0 build --workspace-folder . --frozen-lockfile
```

These commands build images and require host resources. They are instructions,
not commands already executed. Replacing the running development container
requires a separate user-approved rebuild and may end the current sessions.

## Ownership and minimality

The backend implementation role owns only:

- `.devcontainer/Dockerfile`
- `.devcontainer/devcontainer.json`
- `.devcontainer/devcontainer-lock.json` (official CLI output)
- `.devcontainer/docker-compose.yml` (comments only)
- `.devcontainer/.env.example` (new)
- `global.json` (new)
- `docs/dev-environment.md` (new)

No frontend changes, custom diagnostic framework, extra validation helper, or
application test tooling is needed for this prerequisite. Existing workflow
tests remain unchanged by its implementation. The coordinator's separate
workflow permission fix is not part of this design's implementation diff.

## Validation and acceptance

### Runnable now, without rebuilding or a Docker client

```sh
python3 -I -c 'import json, encodings, ssl, sqlite3'
dpkg -S /usr/lib/python3.12/json/__init__.py
python3 -m unittest discover -s tests -p 'test_agentic_flow.py' -v
git diff --check
git status --short
```

Review the owned-file diff and unchanged Compose settings. The Python import and
package-ownership checks establish the observed repair; the workflow tests
protect orchestration behavior. They do **not** prove image builds, runtime
versions, DB health, or Docker connectivity. The workflow permission patch
currently passes all 25 tests.

### After an explicitly authorized image build/rebuild

Run from the repo root:

```sh
python3 -I -c 'import json, encodings, ssl, sqlite3'
dotnet --version
node --version
npm --version
gh --version
docker --version
docker compose version
claude --version
codex --version
copilot --version
docker compose --env-file .devcontainer/.env.example \
  -f .devcontainer/docker-compose.yml config --no-env-resolution --quiet
```

Required outputs: .NET `11.0.100`, Node `v24.22.2`, npm `11.19.1`, gh `2.103.3`,
Docker CLI `29.9.3`, Compose `5.7.0`; all other commands exit 0. The official
[Compose reference][compose] documents `--no-env-resolution` and `--quiet`.
Using the sample interpolation file and disabling service-env-file resolution
avoids reading or printing the private local env file during structural checks.

On the host, verify a frozen-lockfile build and compare the actual
project-prefixed PostgreSQL volume identity before and after the approved
rebuild. Keep the same Compose project name; compare the exact volume name and
creation timestamp. Require a healthy existing DB service. Never delete or
reinitialize the volume to obtain passing checks.

Missing host daemon access is a reported host-validation blocker, not a passing
result. Do not auto-rebuild, change credentials, or auto-login on the user's behalf.

## Current gate

No toolchain changes described here have been implemented. This corrected draft
has not passed independent review. The automated run remains blocked with its
three attempts preserved. Explicit human intervention is required to choose the
next process; do not restart the same task under a new name or self-approve it.

[sdk]: https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json
[node]: https://nodejs.org/dist/index.json
[angular]: https://angular.dev/reference/versions
[gh]: https://api.github.com/repos/cli/cli/releases/tags/v2.103.3
[docker]: https://download.docker.com/linux/ubuntu/dists/noble/stable/binary-amd64/Packages.gz
[cli-npm]: https://registry.npmjs.org/@devcontainers/cli/latest
[lock]: https://raw.githubusercontent.com/devcontainers/cli/v0.90.0/src/spec-configuration/lockfile.ts
[compose]: https://docs.docker.com/reference/cli/docker/compose/config/

#!/usr/bin/env bash
# Integration check (design §14.3, AC18). Requires: SUBSCRIBED_TEST_DB (backend tests), a working
# host Docker daemon, Node/npm and the .NET SDK. Everything it creates is removed by the EXIT trap;
# it writes no files to the worktree (build outputs aside). Exit 0 only if every step passes.
set -euo pipefail

cd "$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

if [[ -z "${SUBSCRIBED_TEST_DB:-}" ]]; then
  echo "integration-check: SUBSCRIBED_TEST_DB is not set" >&2
  exit 2
fi
if ! docker info >/dev/null 2>&1; then
  echo "integration-check: docker daemon is not reachable (docker info failed)" >&2
  exit 2
fi

ID="$$"
IMAGE="subscribed:it-$ID"
NET="subscribed-it-$ID"
PG="subscribed-it-pg-$ID"
APP="subscribed-it-app-$ID"
CURL_IMAGE="curlimages/curl:8.11.1"

cleanup() {
  docker rm -f "$APP" "$PG" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  docker image rm -f "$IMAGE" >/dev/null 2>&1 || true
}
trap cleanup EXIT

step() { printf '\n== %s\n' "$*"; }

step "frontend: ci + build"
npm --prefix frontend ci
npm --prefix frontend run build

step "backend: build + test"
dotnet build backend/Subscribed.slnx -c Release -warnaserror
dotnet test backend/Subscribed.slnx -c Release --no-build

step "image build"
docker build -f deploy/Dockerfile -t "$IMAGE" .
USER_IN_IMAGE="$(docker image inspect -f '{{.Config.User}}' "$IMAGE")"
if [[ -z "$USER_IN_IMAGE" || "$USER_IN_IMAGE" == "root" || "$USER_IN_IMAGE" == "0" || "$USER_IN_IMAGE" == 0:* ]]; then
  echo "integration-check: image runs as root ('$USER_IN_IMAGE')" >&2
  exit 1
fi
echo "image user: $USER_IN_IMAGE"

step "ephemeral PostgreSQL 18 on a private network"
docker network create "$NET" >/dev/null
rand() { od -An -N16 -tx1 /dev/urandom | tr -d ' \n'; }
SUPER_PW="$(rand)"; MIG_PW="$(rand)"; APP_PW="$(rand)"
docker run -d --name "$PG" --network "$NET" -e POSTGRES_PASSWORD="$SUPER_PW" postgres:18 >/dev/null
for _ in $(seq 1 60); do
  docker exec "$PG" pg_isready -U postgres -q && break
  sleep 1
done
docker exec "$PG" pg_isready -U postgres -q

step "provision.sql (as the DBA would) + admin db migrate (as the migrator)"
docker exec -i -e PGPASSWORD="$SUPER_PW" "$PG" psql -U postgres -v ON_ERROR_STOP=1 \
  -v migrator_password="$MIG_PW" -v app_password="$APP_PW" -f - < deploy/postgres/provision.sql >/dev/null
docker run --rm --network "$NET" \
  -e ConnectionStrings__SubscribedMigrator="Host=$PG;Database=subscribed;Username=subscribed_migrator;Password=$MIG_PW" \
  -e Database__AppRole=subscribed_app \
  "$IMAGE" admin db migrate

step "run the image as the app role"
docker run -d --name "$APP" --network "$NET" --read-only --tmpfs /tmp \
  -e App__PublicOrigin=https://app.example.invalid \
  -e Oidc__Authority=https://idp.example.invalid \
  -e Oidc__ClientId="it-client-$ID" \
  -e Oidc__ClientSecret="$(rand)" \
  -e ConnectionStrings__Subscribed="Host=$PG;Database=subscribed;Username=subscribed_app;Password=$APP_PW" \
  "$IMAGE" >/dev/null

probe() { docker run --rm --network "$NET" "$CURL_IMAGE" -s -o /dev/null -w '%{http_code}' "$@"; }
body() { docker run --rm --network "$NET" "$CURL_IMAGE" -s "$@"; }

step "probe /health/ready (≤ 60 s)"
ready=""
for _ in $(seq 1 30); do
  ready="$(probe "http://$APP:8080/health/ready" || true)"
  [[ "$ready" == "200" ]] && break
  sleep 2
done
if [[ "$ready" != "200" ]]; then
  echo "integration-check: /health/ready returned '$ready'" >&2
  docker logs "$APP" 2>&1 | tail -n 40 >&2
  exit 1
fi

step "probe /household → index.html"
html="$(body -H 'Accept: text/html' "http://$APP:8080/household")"
grep -q '<app-root' <<<"$html" || { echo "integration-check: /household did not serve index.html" >&2; exit 1; }
grep -q '__CSP_NONCE__' <<<"$html" && { echo "integration-check: CSP nonce placeholder not replaced" >&2; exit 1; }

step "probe /api/x → 404 problem+json"
code="$(probe "http://$APP:8080/api/x")"
ctype="$(docker run --rm --network "$NET" "$CURL_IMAGE" -s -o /dev/null -w '%{content_type}' "http://$APP:8080/api/x")"
[[ "$code" == "404" && "$ctype" == application/problem+json* ]] || { echo "integration-check: /api/x gave $code $ctype" >&2; exit 1; }

echo
echo "integration-check: all steps passed"

#!/usr/bin/env bash
# LOCAL DEVELOPMENT ONLY. Creates synthetic users in the dev realm of deploy/compose.dev.yml with
# random passwords and prints them (and the generated client secret) to this terminal only.
# Nothing is written to disk. Requires KC_DEV_ADMIN_PASSWORD (the value used to start Keycloak).
set -euo pipefail

: "${KC_DEV_ADMIN_PASSWORD:?export the KC_DEV_ADMIN_PASSWORD used for compose.dev.yml}"
cd "$(dirname "$0")/.."
COMPOSE=(docker compose -f deploy/compose.dev.yml)
KCADM=("${COMPOSE[@]}" exec -T keycloak /opt/keycloak/bin/kcadm.sh)
REALM=subscribed-dev

"${KCADM[@]}" config credentials --server http://localhost:8180 --realm master \
  --user dev-admin --password "$KC_DEV_ADMIN_PASSWORD" >/dev/null

create_user() {
  local username="$1" name="$2" password
  password="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
  local id
  id="$("${KCADM[@]}" get users -r "$REALM" -q "username=$username" -q exact=true --fields id --format csv --noquotes)"
  if [[ -z "$id" ]]; then
    id="$("${KCADM[@]}" create users -r "$REALM" -s "username=$username" -s enabled=true \
      -s "firstName=$name" -s "lastName=(synthetic)" -i)"
  fi
  "${KCADM[@]}" set-password -r "$REALM" --username "$username" --new-password "$password" >/dev/null
  printf '%-14s password=%s  subject=%s\n' "$username" "$password" "$id"
}

echo "Synthetic users (shown once; not stored):"
create_user synthetic-a "Synthetic User A"
create_user synthetic-b "Synthetic User B"

client_uuid="$("${KCADM[@]}" get clients -r "$REALM" -q clientId=subscribed-dev --fields id --format csv --noquotes | head -n1)"
secret="$("${KCADM[@]}" get "clients/$client_uuid/client-secret" -r "$REALM" --fields value --format csv --noquotes)"
echo
echo "Client secret for Oidc:ClientSecret (pass via environment or user-secrets, never commit):"
echo "  $secret"
echo
echo "Grant the first member:  dotnet run --project backend/src/Subscribed.Api -- admin household init \\"
echo "  --name \"Synthetic Household\" --owner-subject <subject of synthetic-a> --owner-label \"Member A\""

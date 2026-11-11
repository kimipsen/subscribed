# Subscribed

A self-hosted household subscription tracker. This foundation provides: Keycloak sign-in through a
backend-for-frontend (HttpOnly cookie, no tokens in the browser), app-owned household membership,
an event-sourced `Household` aggregate on PostgreSQL (Marten), health/config validation, tests and a
single-container Docker deployment.

| Path | Contents |
| --- | --- |
| [`backend/`](backend/README.md) | .NET 11 modular monolith (API/BFF, admin CLI, tests) |
| `frontend/` | Angular SPA (built to `frontend/dist/subscribed/browser`, served by the backend) |
| [`deploy/`](deploy/README.md) | Dockerfile, production compose (external PostgreSQL/Keycloak), provisioning template, dev Keycloak |
| `scripts/integration-check.sh` | image + ephemeral PostgreSQL end-to-end check |
| [`docs/dev-environment.md`](docs/dev-environment.md) | pinned development tools and container rebuild instructions |

## Quick start (development)

Prerequisites: .NET SDK 11.0.100, Node 24.22.2, PostgreSQL 18 (devcontainer `db` service),
optionally Docker for the dev Keycloak.

```sh
export SUBSCRIBED_TEST_DB='Host=localhost;Port=5432;Username=…;Password=…;Database=…'   # CREATEDB+CREATEROLE role
dotnet build backend/Subscribed.slnx -c Release -warnaserror
dotnet test backend/Subscribed.slnx -c Release --no-build
dotnet format backend/Subscribed.slnx --verify-no-changes
```

Running the app locally, the admin CLI and the configuration keys are described in
[`backend/README.md`](backend/README.md); production deployment in [`deploy/README.md`](deploy/README.md).

## Security model in one paragraph

The browser only talks to the same-origin backend. The backend is a confidential OIDC client
(authorization code + PKCE, `response_mode=query`) and keeps a session cookie
(`__Host-subscribed`, HttpOnly, Secure, SameSite=Lax, 2 h idle / 12 h absolute) holding only issuer,
subject, display name and sign-in time. Being signed in does not grant access: every API request
checks `(issuer, subject)` against the household's event-sourced member list, so revocation is
immediate. API requests, session checks and logout need `X-CSRF: 1`; there is no CORS.
`Origin`, when present on an unsafe request, must match the configured public origin.
The database application role can only append events (no UPDATE/DELETE on the event table),
which readiness verifies on every probe.

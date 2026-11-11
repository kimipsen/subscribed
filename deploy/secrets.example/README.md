# Docker secrets layout (examples only — no real values)

Create `deploy/secrets/` (gitignored) with one file per secret. Each file contains only the
value, with no trailing newline (`printf '%s' "$VALUE" > file`).

| File | Content (format) | Used by |
| --- | --- | --- |
| `oidc_client_secret` | the confidential client's secret from the realm admin | `subscribed` |
| `app_connection` | `Host=<db-host>;Port=5432;Database=subscribed;Username=subscribed_app;Password=<app password>;SSL Mode=Require` | `subscribed` |
| `migrator_connection` | `Host=<db-host>;Port=5432;Database=subscribed;Username=subscribed_migrator;Password=<migrator password>;SSL Mode=Require` | `migrate` |

Compose (non-swarm) bind-mounts these files with their host ownership, and the container runs as
UID 1654 (`APP_UID` of the official .NET image). Restrict them to that UID:

```sh
install -d -m 700 deploy/secrets
printf '%s' "$OIDC_CLIENT_SECRET" > deploy/secrets/oidc_client_secret
# ... the two connection strings ...
sudo chown 1654:1654 deploy/secrets/*
sudo chmod 400 deploy/secrets/*
```

Never commit these files, never pass them as environment variables in shell history, and never
reuse the migrator password for the application role.

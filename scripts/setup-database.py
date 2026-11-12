#!/usr/bin/env python3
"""Provision the local PostgreSQL database and persist development settings."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "backend/src/Subscribed.Api"
IDENTIFIER = re.compile(r"[a-z_][a-z0-9_]{0,62}\Z")


def parse_env_value(value, number):
    value = value.strip()
    if value.startswith(("'", '"')):
        match = re.fullmatch(r"""(['"])(.*?)\1\s*(?:#.*)?""", value)
        if not match:
            raise ValueError(f"Invalid quoted .env value on line {number}.")
        return match[2]
    comment = re.search(r"\s#", value)
    return value[:comment.start()].rstrip() if comment else value


def read_env(path):
    """Read literal dotenv values, without executing shell code or expanding variables."""
    path = path.resolve(strict=True)
    if not path.is_relative_to(ROOT.resolve()) or path.name != ".env" or not path.is_file():
        raise ValueError("The environment file must be named .env and located inside this repository.")
    values = {}
    for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[7:].lstrip()
        key, separator, value = line.partition("=")
        key = key.strip()
        if not separator or not re.fullmatch(r"[A-Za-z_]\w*", key, flags=re.ASCII):
            raise ValueError(f"Invalid .env assignment on line {number}.")
        if key in values:
            raise ValueError(f"Duplicate .env key: {key}.")
        values[key] = parse_env_value(value, number)
    return values


def required(values, key):
    value = values.get(key, "")
    if not value or value.startswith("CHANGE_ME") or "\0" in value:
        raise ValueError(f"Set {key} in the .env file (not a placeholder).")
    return value


def identifier(value, key):
    if not IDENTIFIER.fullmatch(value):
        raise ValueError(f"{key} must match [a-z_][a-z0-9_]{{0,62}}.")
    return value


def literal(value):
    return "'" + value.replace("'", "''") + "'"


def connection_string(host, port, database, user, password):
    def quoted(value):
        return '"' + str(value).replace('"', '""') + '"'

    return ";".join(
        f"{key}={quoted(value)}" for key, value in (
            ("Host", host), ("Port", port), ("Database", database),
            ("Username", user), ("Password", password),
        )
    )


def run(command, environment, secrets, input_text=None):
    try:
        result = subprocess.run(
            command, input=input_text, text=True, capture_output=True,
            cwd=ROOT, env=environment, check=False,
        )
    except FileNotFoundError:
        raise RuntimeError(f"Required tool not found: {command[0]}.") from None
    if result.returncode:
        output = result.stdout + result.stderr
        redactions = {
            variant for secret in secrets if secret
            for variant in (
                secret, secret.replace("'", "''"), secret.replace('"', '""'),
                json.dumps(secret, ensure_ascii=False)[1:-1],
            )
        }
        for secret in sorted(redactions, key=len, reverse=True):
            output = output.replace(secret, "[redacted]")
        raise RuntimeError(f"{command[0]} failed (exit {result.returncode}):\n{output}")
    return result.stdout.strip()


def check_existing_objects(sql, database, schema, app, migrator):
    owner = sql(f"SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = {literal(database)};")
    if owner and owner != migrator:
        raise ValueError("Existing application database is not owned by the configured migrator; refusing to modify it.")
    for role in (app, migrator):
        unsafe = sql(
            f"SELECT 1 FROM pg_roles WHERE rolname = {literal(role)} AND "
            "(rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls "
            "OR EXISTS (SELECT 1 FROM pg_auth_members WHERE member = pg_roles.oid));"
        )
        if unsafe:
            raise ValueError(f"Existing role {role} has elevated privileges or memberships; refusing to modify it.")
    if owner:
        schema_owner = sql(
            f"SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = {literal(schema)};",
            database,
        )
        if schema_owner and schema_owner != migrator:
            raise ValueError("Existing schema is not owned by the configured migrator; refusing to modify it.")
    return bool(owner)


def setup(values):
    admin = required(values, "POSTGRES_USER")
    admin_password = required(values, "POSTGRES_PASSWORD")
    host = values.get("POSTGRES_HOST", "localhost")
    port = values.get("POSTGRES_PORT", "5432")
    if not port.isascii() or not port.isdecimal() or not 1 <= int(port) <= 65535:
        raise ValueError("POSTGRES_PORT must be between 1 and 65535.")
    database = identifier(values.get("SUBSCRIBED_DB", "subscribed"), "SUBSCRIBED_DB")
    app = identifier(values.get("SUBSCRIBED_APP_USER", "subscribed_app"), "SUBSCRIBED_APP_USER")
    migrator = identifier(values.get("SUBSCRIBED_MIGRATOR_USER", "subscribed_migrator"), "SUBSCRIBED_MIGRATOR_USER")
    schema = identifier(values.get("SUBSCRIBED_SCHEMA", "subscribed"), "SUBSCRIBED_SCHEMA")
    app_password = required(values, "SUBSCRIBED_APP_PASSWORD")
    migrator_password = required(values, "SUBSCRIBED_MIGRATOR_PASSWORD")
    admin_db = values.get("POSTGRES_DB", "postgres")
    if database in {admin_db, "postgres", "template0", "template1"}:
        raise ValueError("SUBSCRIBED_DB must be a dedicated application database, not the administrator database.")
    if len({admin, app, migrator}) != 3:
        raise ValueError("Administrator, application and migrator usernames must be distinct.")
    if schema in {"public", "pg_catalog", "information_schema"}:
        raise ValueError("SUBSCRIBED_SCHEMA must be a dedicated application schema.")
    if app_password == migrator_password:
        raise ValueError("Application and migrator passwords must be different.")

    environment = os.environ | {
        "PGHOST": host, "PGPORT": port, "PGUSER": admin, "PGPASSWORD": admin_password,
        "PGDATABASE": admin_db, "PGCONNECT_TIMEOUT": "10", "PGOPTIONS": "",
    }
    secrets = [admin_password, app_password, migrator_password]

    def sql(text, target=admin_db):
        return run(
            ["psql", "-X", "--quiet", "--no-password", "--set=ON_ERROR_STOP=1", "--tuples-only", "--no-align"],
            environment | {"PGDATABASE": target}, secrets,
            "SET standard_conforming_strings = on;\n" + text,
        )

    run(["dotnet", "--version"], environment, secrets)
    database_exists = check_existing_objects(sql, database, schema, app, migrator)

    for role, password in ((migrator, migrator_password), (app, app_password)):
        sql(
            f"SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT', {literal(role)}) "
            f"WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = {literal(role)})\n\\gexec\n"
            f"ALTER ROLE {role} LOGIN PASSWORD {literal(password)};"
        )
    # PostgreSQL 16+ requires explicit SET membership for non-superuser administrators.
    quoted_admin = '"' + admin.replace('"', '""') + '"'
    sql(f"GRANT {migrator} TO {quoted_admin} WITH SET TRUE, INHERIT TRUE;")
    if not database_exists:
        sql(f"CREATE DATABASE {database} OWNER {migrator} ENCODING 'UTF8' TEMPLATE template0;")
    sql(
        f"REVOKE ALL ON DATABASE {database} FROM PUBLIC;\n"
        f"GRANT CONNECT ON DATABASE {database} TO {migrator}, {app};"
    )
    sql(
        f"REVOKE ALL ON SCHEMA public FROM PUBLIC;\n"
        f"CREATE SCHEMA IF NOT EXISTS {schema} AUTHORIZATION {migrator};\n"
        f"REVOKE ALL ON SCHEMA {schema} FROM PUBLIC;",
        database,
    )
    app_cs = connection_string(host, port, database, app, app_password)
    migrator_cs = connection_string(host, port, database, migrator, migrator_password)
    secrets.extend([app_cs, migrator_cs])
    with tempfile.TemporaryDirectory(prefix="subscribed-db-") as empty_secrets:
        migration_environment = environment | {
            "ASPNETCORE_ENVIRONMENT": "Development",
            "DOTNET_ENVIRONMENT": "Development",
            "SUBSCRIBED_SECRETS_DIR": empty_secrets,
            "ConnectionStrings__Subscribed": app_cs,
            "ConnectionStrings__SubscribedMigrator": migrator_cs,
            "Database__Schema": schema, "Database__AppRole": app,
        }
        run(
            ["dotnet", "run", "--project", str(PROJECT), "--no-launch-profile", "--", "admin", "db", "migrate"],
            migration_environment, secrets,
        )
        run(
            ["dotnet", "run", "--project", str(PROJECT), "--no-build", "--no-launch-profile", "--", "admin", "household", "show"],
            migration_environment, secrets,
        )
    run(
        ["dotnet", "user-secrets", "set", "--project", str(PROJECT)],
        environment, secrets, json.dumps({
            "ConnectionStrings:Subscribed": app_cs,
            "Database:Schema": schema, "Database:AppRole": app,
            "App:PublicOrigin": "http://localhost:5080",
        }),
    )
    print(f"Database {database} provisioned and migrated; app-role access verified.")
    print("Development connection saved in .NET user-secrets. Restart backend debugging.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--env-file", type=Path, default=ROOT / ".devcontainer/.env")
    args = parser.parse_args()
    try:
        setup(read_env(args.env_file))
    except (OSError, ValueError, RuntimeError) as error:
        print(f"Database setup failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

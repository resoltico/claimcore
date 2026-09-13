#!/usr/bin/env bash
# Runs INSIDE the Linux PostgreSQL container, not in the user's macOS/Windows shell.
set -euo pipefail
: "${CLAIMCORE_APP_PASSWORD:?Missing development runtime password}"
: "${POSTGRES_PASSWORD:?Missing development owner password}"

if [[ "$CLAIMCORE_APP_PASSWORD" == "$POSTGRES_PASSWORD" ]]; then
  printf '%s\n' 'Development owner and runtime passwords must be distinct.' >&2
  exit 64
fi

psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set ON_ERROR_STOP=1 \
  --set app_password="$CLAIMCORE_APP_PASSWORD" <<'SQL'
DO $baseline$
BEGIN
    IF current_setting('server_version_num')::integer < 180006
       OR current_setting('server_version_num')::integer >= 190000 THEN
        RAISE EXCEPTION 'PostgreSQL 18.6 or later within major 18 is required';
    END IF;
END;
$baseline$;
CREATE ROLE claimcore_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD :'app_password';
REVOKE ALL ON DATABASE claimcore FROM PUBLIC;
GRANT CONNECT ON DATABASE claimcore TO claimcore_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL

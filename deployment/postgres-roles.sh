#!/usr/bin/env bash
set -euo pipefail
: "${POSTGRES_DB:?}" "${POSTGRES_USER:?}"
[[ "${POSTGRES_DB}" == claimcore ]] || exit 1
runtime_password="$(cat /etc/claimcore-postgres/runtime.password)"
[[ "${runtime_password}" =~ ^[0-9a-f]{64}$ ]] || exit 1
# The validated secret goes through stdin, never argv or a tracked/image file.
psql -X -v ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname "${POSTGRES_DB}" >/dev/null <<SQL
CREATE ROLE claimcore_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${runtime_password}';
REVOKE ALL ON DATABASE claimcore FROM PUBLIC;
GRANT CONNECT ON DATABASE claimcore TO claimcore_runtime;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
unset runtime_password

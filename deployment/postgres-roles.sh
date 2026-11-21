#!/usr/bin/env bash
set -euo pipefail
: "${POSTGRES_DB:?}" "${POSTGRES_USER:?}"
[[ "${POSTGRES_DB}" == claimcore && "${POSTGRES_USER}" == postgres ]] || exit 1
owner_password="$(cat /etc/claimcore-postgres/owner.password)"
runtime_password="$(cat /etc/claimcore-postgres/runtime.password)"
[[ "${owner_password}" =~ ^[0-9a-f]{64}$ && "${runtime_password}" =~ ^[0-9a-f]{64}$ ]] || {
  printf 'The owner and runtime password files must contain 64 lowercase hexadecimal characters.\n' >&2
  exit 1
}
# The validated secrets go through stdin.
psql -Xw -v ON_ERROR_STOP=1 --host /var/run/postgresql --username "${POSTGRES_USER}" --dbname "${POSTGRES_DB}" >/dev/null <<SQL
BEGIN;
SET LOCAL log_min_error_statement = 'panic';
CREATE ROLE claimcore_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '${owner_password}';
CREATE ROLE claimcore_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${runtime_password}';
ALTER DATABASE claimcore OWNER TO claimcore_owner;
REVOKE ALL ON DATABASE claimcore FROM PUBLIC;
GRANT CONNECT ON DATABASE claimcore TO claimcore_runtime;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
COMMIT;
SQL
unset owner_password runtime_password

#!/usr/bin/env bash
set -euo pipefail
: "${POSTGRES_DB:?}" "${POSTGRES_USER:?}"

read_secret() {
  local value
  value="$(cat "$1")"
  [[ "${value}" =~ ^[0-9a-f]{64}$ ]] || exit 1
  printf '%s' "${value}"
}

if [[ "${POSTGRES_DB}" == claimcore ]]; then
  app_password="$(read_secret /etc/claimcore-postgres/app.password)"
  psql -X -v ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname "${POSTGRES_DB}" >/dev/null 2>&1 <<SQL
CREATE ROLE claimcore_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${app_password}';
REVOKE ALL ON DATABASE claimcore FROM PUBLIC;
GRANT CONNECT ON DATABASE claimcore TO claimcore_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
elif [[ "${POSTGRES_DB}" == claimcore_witness ]]; then
  writer_password="$(read_secret /etc/claimcore-postgres/writer.password)"
  auditor_password="$(read_secret /etc/claimcore-postgres/auditor.password)"
  psql -X -v ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname "${POSTGRES_DB}" >/dev/null 2>&1 <<SQL
CREATE ROLE claimcore_witness_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${writer_password}';
CREATE ROLE claimcore_witness_auditor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${auditor_password}';
REVOKE ALL ON DATABASE claimcore_witness FROM PUBLIC;
GRANT CONNECT ON DATABASE claimcore_witness TO claimcore_witness_writer, claimcore_witness_auditor;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
else
  exit 1
fi

#!/usr/bin/env bash
# PostgreSQL runs this once, before the witness database accepts TCP connections.
set -euo pipefail
: "${POSTGRES_USER:?}" "${POSTGRES_DB:?}"
owner_password="$(< /etc/claimcore-witness/owner.password)"
runtime_password="$(< /etc/claimcore-witness/runtime.password)"
for password in "${owner_password}" "${runtime_password}"; do
  [[ "${password}" =~ ^[a-f0-9]{64}$ ]] || exit 1
done
psql -Xwq --set ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname "${POSTGRES_DB}" <<SQL
CREATE ROLE claimcore_witness_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '${owner_password}';
CREATE ROLE claimcore_witness_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${runtime_password}';
ALTER DATABASE witness OWNER TO claimcore_witness_owner;
REVOKE ALL ON DATABASE witness FROM PUBLIC;
GRANT CONNECT ON DATABASE witness TO claimcore_witness_runtime;
GRANT EXECUTE ON FUNCTION pg_catalog.pg_control_system() TO claimcore_witness_owner, claimcore_witness_runtime;
SET ROLE claimcore_witness_owner;
\i /etc/claimcore-witness.sql
SQL

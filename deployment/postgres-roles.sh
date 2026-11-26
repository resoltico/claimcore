#!/usr/bin/env bash
# Run against the final listener so a successful initdb is recoverable independently of provisioning.
set -euo pipefail

refuse() {
  printf '%s\n' "$1" >&2
  exit 1
}

validate_tls() {
  local configuration=/etc/claimcore-postgres cert key trust file key_stat key_owner key_mode server_user cert_public key_public
  cert="${configuration}/server.pem"
  key="${configuration}/server.key"
  trust=/etc/claimcore-postgres-ca.pem

  for file in "${cert}" "${key}"; do
    if [[ ! -f "${file}" ]] || ! gosu postgres test -r "${file}" || ! gosu postgres test -s "${file}"; then
      refuse 'PostgreSQL TLS certificate and key must be readable nonempty regular files.'
    fi
  done

  key_stat="$(stat -Lc '%u %a' "${key}")" ||
    refuse 'Cannot inspect PostgreSQL TLS key ownership and permissions.'
  read -r key_owner key_mode <<<"${key_stat}"
  server_user="$(id -u postgres)"
  # PostgreSQL permits a root-owned private key readable by the server group. A
  # server-owned key must have no group or other permissions.
  if [[ "${key_owner}" == "${server_user}" ]]; then
    (( (8#${key_mode} & 077) == 0 )) || refuse 'PostgreSQL server-owned TLS key must have owner-only permissions.'
  elif [[ "${key_owner}" == 0 ]]; then
    (( (8#${key_mode} & 037) == 0 )) || refuse 'PostgreSQL root-owned TLS key must have mode 0640 or stricter.'
  else
    refuse 'PostgreSQL TLS key must be owned by postgres or root.'
  fi

  cert_public="$(gosu postgres openssl x509 -in "${cert}" -noout -pubkey 2>/dev/null)" ||
    refuse 'PostgreSQL TLS server certificate is not a valid PEM certificate.'
  if grep -Eq '^(-----BEGIN ENCRYPTED PRIVATE KEY-----|Proc-Type:[[:space:]]*4,ENCRYPTED)' "${key}"; then
    refuse 'PostgreSQL TLS private key must be a valid unencrypted PEM key.'
  fi
  key_public="$(gosu postgres openssl pkey -in "${key}" -passin pass: -pubout 2>/dev/null)" ||
    refuse 'PostgreSQL TLS private key must be a valid unencrypted PEM key.'
  [[ "${cert_public}" == "${key_public}" ]] ||
    refuse 'PostgreSQL TLS private key does not match the server certificate.'
  [[ -f "${trust}" && -r "${trust}" && -s "${trust}" ]] ||
    refuse 'PostgreSQL probe CA must be a readable nonempty regular file.'
  if ! openssl verify -CAfile "${trust}" -no-CApath -no-CAstore \
      -untrusted "${cert}" -purpose sslserver -verify_hostname postgres "${cert}" >/dev/null 2>&1; then
    refuse 'PostgreSQL TLS certificate does not validate for postgres against the configured CA.'
  fi
}

preflight() {
  [[ "${POSTGRES_DB:-}" == claimcore && "${POSTGRES_USER:-}" == postgres ]] ||
    refuse 'Expected database claimcore and local bootstrap user postgres.'

  local configuration=/etc/claimcore-postgres superuser_password
  owner_password="$(cat "${configuration}/owner.password")"
  runtime_password="$(cat "${configuration}/runtime.password")"
  superuser_password="$(cat "${POSTGRES_PASSWORD_FILE:?}")"
  [[ "${owner_password}" =~ ^[0-9a-f]{64}$ && "${runtime_password}" =~ ^[0-9a-f]{64}$ &&
     "${superuser_password}" =~ ^[0-9a-f]{64}$ ]] ||
    refuse 'The PostgreSQL password files must contain 64 lowercase hexadecimal characters.'

}

socket_psql=(psql -XwqAt -v ON_ERROR_STOP=1 -h /var/run/postgresql -U postgres -d claimcore)

control_psql=(psql -XwqAt -v ON_ERROR_STOP=1 -h /var/run/postgresql -U postgres -d postgres)

listener_status() {
  "${control_psql[@]}" -c "SELECT current_setting('listen_addresses') <> ''" 2>/dev/null || true
}

application_database() {
  "${control_psql[@]}" -c "SELECT EXISTS (SELECT FROM pg_database WHERE datname='claimcore')" 2>/dev/null || true
}

provision() {
# Local bootstrap socket only. Passwords are validated before interpolation and never passed on argv.
if ! "${socket_psql[@]}" >/dev/null 2>&1 <<SQL
BEGIN;
SET LOCAL log_min_error_statement = 'panic';
SET LOCAL log_error_verbosity = 'terse';
DO \$provision\$
DECLARE db pg_database%ROWTYPE;
BEGIN
    PERFORM pg_advisory_xact_lock(1936341228);
    SELECT * INTO STRICT db FROM pg_database WHERE datname=current_database();
    IF db.encoding <> pg_char_to_encoding('UTF8') OR db.datlocprovider <> 'b'
       OR db.datlocale IS DISTINCT FROM 'C.UTF-8'
       OR current_setting('data_checksums') <> 'on' OR current_setting('ssl') <> 'on' THEN
        RAISE EXCEPTION 'Unexpected PostgreSQL profile. Check the database locale, checksums and TLS.';
    END IF;

    IF db.datdba='postgres'::regrole AND db.datacl IS NULL
       AND NOT EXISTS(SELECT FROM pg_roles WHERE rolname IN ('claimcore_owner','claimcore_runtime'))
       AND NOT EXISTS(SELECT FROM pg_namespace
                      WHERE nspname NOT IN ('pg_catalog','pg_toast','public','information_schema')
                        AND nspname NOT LIKE 'pg_temp_%' AND nspname NOT LIKE 'pg_toast_temp_%')
       AND NOT EXISTS(SELECT FROM pg_class WHERE relnamespace='public'::regnamespace)
       AND NOT EXISTS(SELECT FROM pg_proc WHERE pronamespace='public'::regnamespace)
       AND NOT EXISTS(SELECT FROM pg_type WHERE typnamespace='public'::regnamespace)
       AND NOT EXISTS(SELECT FROM pg_extension WHERE extname<>'plpgsql') THEN
        CREATE ROLE claimcore_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '${owner_password}';
        CREATE ROLE claimcore_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '${runtime_password}';
        ALTER DATABASE claimcore OWNER TO claimcore_owner;
        REVOKE ALL ON DATABASE claimcore FROM PUBLIC;
        GRANT CONNECT ON DATABASE claimcore TO claimcore_runtime;
        REVOKE CREATE ON SCHEMA public FROM PUBLIC;

    END IF;
END;
\$provision\$;
COMMIT;
SQL
then
  printf '%s\n' 'PostgreSQL role provisioning failed. Correct the configuration or inspect the existing volume, then restart.' >&2
fi
}

probe() {
  local role="$1" password="$2"
  PGPASSWORD="${password}" psql -XwqAt \
    "host=postgres hostaddr=127.0.0.1 dbname=claimcore user=${role} sslmode=verify-full sslrootcert=/etc/claimcore-postgres-ca.pem gssencmode=disable connect_timeout=2" \
    -c 'SELECT 1 FROM pg_stat_ssl WHERE pid=pg_backend_pid() AND ssl' || true
}

ready() {
  local structure owner_tls runtime_tls listen database
  listen="$(listener_status)"
  [[ "${listen}" == t ]] || refuse 'Waiting for the final PostgreSQL TCP listener.'
  database="$(application_database)"
  [[ "${database}" == t ]] || refuse 'Cannot admit the claimcore database. Preserve the volume and have the database operator inspect it.'
  structure="$("${socket_psql[@]}" <<'SQL'
SELECT d.datdba=to_regrole('claimcore_owner')
   AND d.encoding=pg_char_to_encoding('UTF8') AND d.datlocprovider='b' AND d.datlocale='C.UTF-8'
   AND current_setting('data_checksums')='on' AND current_setting('ssl')='on'
   AND (SELECT count(*) FROM pg_roles
        WHERE rolname IN ('claimcore_owner','claimcore_runtime')
          AND rolcanlogin AND NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
          AND rolinherit=(rolname='claimcore_owner'))=2
   AND NOT EXISTS(SELECT FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member
                  WHERE r.rolname IN ('claimcore_owner','claimcore_runtime'))
   AND NOT EXISTS(SELECT FROM aclexplode(d.datacl)
                  WHERE grantee<>d.datdba
                    AND (grantee IS DISTINCT FROM to_regrole('claimcore_runtime') OR privilege_type<>'CONNECT' OR is_grantable))
   AND EXISTS(SELECT FROM aclexplode(d.datacl)
              WHERE grantee=to_regrole('claimcore_runtime') AND privilege_type='CONNECT')
   AND NOT coalesce(has_schema_privilege(to_regrole('claimcore_runtime'),'public','CREATE'),true)
   AND NOT EXISTS(SELECT FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) a
                  WHERE n.nspname='public' AND a.grantee=0 AND a.privilege_type='CREATE')
FROM pg_database d WHERE d.datname=current_database();
SQL
)" || refuse 'Cannot inspect PostgreSQL provisioning.'
  [[ "${structure}" == t ]] || refuse 'PostgreSQL provisioning is incomplete or has drifted. Have the database operator inspect the existing volume.'
  owner_tls="$(probe claimcore_owner "${owner_password}")"
  runtime_tls="$(probe claimcore_runtime "${runtime_password}")"
  [[ "${owner_tls}" == 1 ]] ||
    refuse 'The provisioned owner TLS credentials do not match. Check the password file and PostgreSQL certificate.'
  [[ "${runtime_tls}" == 1 ]] ||
    refuse 'The provisioned runtime TLS credentials do not match. Check the password file and PostgreSQL certificate.'
}

start() {
  validate_tls
  /usr/local/bin/docker-entrypoint.sh "$@" &
  local postgres_pid=$!
  trap 'stop_postgres; exit 0' TERM INT

  local listen database
  listen="$(listener_status)"
  while [[ "${listen}" != t ]]; do
    if [[ ! -d "/proc/${postgres_pid}" ]]; then
      wait "${postgres_pid}"
      return 1
    fi
    sleep 1
    listen="$(listener_status)"
  done

  database="$(application_database)"
  if [[ "${database}" != t ]]; then
    stop_postgres
    if [[ "${database}" == f ]]; then
      refuse 'PostgreSQL cluster has no claimcore database. Startup stopped. Preserve the volume and have the database operator inspect or restore it.'
    fi
    refuse 'Cannot inspect the PostgreSQL database inventory. Startup stopped. Preserve the volume and have the database operator inspect it.'
  fi

  printf '%s\n' 'PostgreSQL final listener is available. Checking role provisioning.'
  provision
  wait "${postgres_pid}"
}

stop_postgres() {
  # PostgreSQL changes UID. The container intentionally lacks CAP_KILL.
  kill -TERM "${postgres_pid}" 2>/dev/null ||
    gosu postgres kill -TERM "${postgres_pid}" 2>/dev/null || true
  wait "${postgres_pid}" || true
}

preflight
mode="${1:-}"
shift
case "${mode}" in
  start) start "$@" ;;
  ready) ready ;;
  *) refuse 'Expected PostgreSQL start or ready mode.' ;;
esac

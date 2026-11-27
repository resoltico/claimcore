#!/usr/bin/env bash
# Builds the Compose evaluation stack from this checkout and proves that a stop and restart keeps the
# installation: first start initializes, `down` then `up` skips initialization and Web becomes healthy again.
# Usage: bash eng/compose-restart-smoke.sh   (Docker Engine with Compose, OpenSSL; ports are configurable)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
web_port="${CLAIMCORE_SMOKE_WEB_PORT:-17443}"
identity_port="${CLAIMCORE_SMOKE_IDENTITY_PORT:-17444}"
work="$(mktemp -d)"
config="${work}/config"
build_source="${work}/source"

compose=(docker compose --env-file "${config}/compose.env" -f "${build_source}/deployment/compose.yaml")
production=()
qualification_network=""

cleanup() {
  if (( ${#production[@]} )); then
    "${production[@]}" down --volumes --timeout 20 > /dev/null 2>&1 || true
  fi
  if [[ -f "${config}/compose.env" ]]; then
    "${compose[@]}" down --volumes --timeout 20 > /dev/null 2>&1 || true
  fi
  if [[ -n "${qualification_network}" ]]; then
    docker network rm "${qualification_network}" > /dev/null 2>&1 || true
  fi
  # On Linux, configuration hands the PostgreSQL and Keycloak folders to their container users;
  # remove them with the same pinned image that configuration used.
  local image
  image="$(sed -nE 's/^[[:space:]]+image: (postgres:[^[:space:]]+)$/\1/p' "${root}/deployment/compose.yaml" | sort -u)"
  if [[ -n "${image}" && -d "${config}" ]]; then
    docker run --rm --network none --user 0 --entrypoint /bin/rm \
      --mount "type=bind,source=${work},target=/work" "${image}" -rf /work/config > /dev/null 2>&1 || true
  fi
  chmod -R u+w "${work}" 2> /dev/null || true
  rm -rf "${work}"
}
trap cleanup EXIT

diagnose() {
  "${compose[@]}" ps --all || true
  "${compose[@]}" logs --tail 80 postgres administration web || true
}

wait_for_web() {
  local container status
  for _ in $(seq 1 60); do
    container="$("${compose[@]}" ps --quiet web)"
    status="$(docker inspect --format '{{.State.Health.Status}}' "${container}" 2> /dev/null || true)"
    if [[ "${status}" == "healthy" ]]; then
      return 0
    fi
    sleep 5
  done
  echo "Web did not become healthy." >&2
  diagnose
  return 1
}

start() {
  if ! "${compose[@]}" up "$@" --detach; then
    echo "Compose start failed." >&2
    diagnose
    return 1
  fi
  wait_for_web
}

require_log() {
  local logs
  logs="$("${compose[@]}" logs administration)"
  if ! grep --fixed-strings --quiet -- "$1" <<< "${logs}"; then
    echo "Administration log lacks: $1" >&2
    diagnose
    return 1
  fi
}

verify_database_owner() {
  local owner
  owner="$("${compose[@]}" exec -T postgres psql -XwAt -h /var/run/postgresql -U postgres -d claimcore \
    -c "SELECT r.rolname, r.rolsuper, r.rolcreaterole, r.rolreplication, r.rolbypassrls
        FROM pg_catalog.pg_database d JOIN pg_catalog.pg_roles r ON r.oid = d.datdba
        WHERE d.datname = current_database()")"
  if [[ "${owner}" != 'claimcore_owner|f|f|f|f' ]]; then
    printf 'Unexpected ClaimCore database ownership and role attributes: %s\n' "${owner}" >&2
    return 1
  fi
  printf 'Database owner and elevated attributes: %s\n' "${owner}"
}

database_probe() {
  local role="$1" secret="$2" sslmode="$3" expected="$4" output
  local mounts=(--mount "type=bind,source=${work}/probe-ca.pem,target=/run/claimcore-ca.pem,readonly")
  if [[ -n "${secret}" ]]; then
    mounts+=(--mount "type=bind,source=${config}/postgres/${secret}.password,target=/run/claimcore-password,readonly")
  fi
  if output="$(docker run --rm --network "${database_network}" --user postgres --read-only \
    --cap-drop ALL --security-opt no-new-privileges "${mounts[@]}" \
    --entrypoint /bin/bash "${database_image}" -euc '
      if [[ -r /run/claimcore-password ]]; then
        PGPASSWORD="$(< /run/claimcore-password)"
        export PGPASSWORD
      fi
      export LC_ALL=C
      exec psql -XwAt "$1" -c "SELECT current_user"
    ' probe "host=postgres dbname=claimcore user=${role} sslmode=${sslmode} sslrootcert=/run/claimcore-ca.pem gssencmode=disable connect_timeout=5" 2>&1)"; then
    if [[ "${expected}" == admitted && "${output}" == "${role}" ]]; then
      printf 'PostgreSQL admitted %s with sslmode=%s.\n' "${role}" "${sslmode}"
      return 0
    fi
    printf 'PostgreSQL unexpectedly admitted %s with sslmode=%s.\n' "${role}" "${sslmode}" >&2
  else
    if [[ "${expected}" == refused && "${output}" == *"pg_hba.conf rejects connection"* ]]; then
      printf 'HBA refused %s with sslmode=%s.\n' "${role}" "${sslmode}"
      return 0
    fi
    printf 'Unexpected connection failure for %s with sslmode=%s.\n%s\n' "${role}" "${sslmode}" "${output}" >&2
  fi
  return 1
}

verify_database_network() {
  local database web web_network database_network database_image
  database="$("${compose[@]}" ps --quiet postgres)"
  database_network="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{println .NetworkID}}{{end}}' "${database}")"
  web="$("${compose[@]}" ps --quiet web)"
  web_network="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{println .NetworkID}}{{end}}' "${web}")"
  if [[ ! "${database_network}" =~ ^[0-9a-f]{64}$ || "${database_network}" != "${web_network}" ]]; then
    printf 'The database and Web must share the evaluation network.\n' >&2
    return 1
  fi
  database_image="$(docker inspect --format '{{.Config.Image}}' "${database}")"
  # The probe runs as postgres so it can read the private password bind. Only its CA is public.
  cp "${config}/ca.pem" "${work}/probe-ca.pem"
  chmod 0644 "${work}/probe-ca.pem"

  database_probe claimcore_owner owner verify-full admitted
  database_probe claimcore_runtime runtime verify-full admitted
  database_probe claimcore_owner owner disable refused
  database_probe postgres "" verify-full refused
}

verify_owner_commands() {
  local pause_id=00000000-0000-0000-0000-000000000001
  local checkpoint=/etc/claimcore/smoke-checkpoint.json
  local admin=("${compose[@]}" run --rm --no-deps -T administration)
  "${admin[@]}" status
  (umask 077; "${admin[@]}" head >"${config}/administration/smoke-checkpoint.json")
  "${admin[@]}" verify --checkpoint "${checkpoint}"
  "${admin[@]}" maintenance acquire 0 "${pause_id}"
  "${admin[@]}" rebuild --checkpoint "${checkpoint}" --pause-generation 1 --pause-id "${pause_id}"
  "${admin[@]}" maintenance resume 1 "${pause_id}"
}

verify_container_settings() {
  local service="$1" pids_limit="$2" container
  container="$("${compose[@]}" ps --all --quiet "${service}")"
  docker inspect --format '{{json .HostConfig}}' "${container}" | node --input-type=module -e '
    import assert from "node:assert/strict";
    import { readFileSync } from "node:fs";
    const [service, limit] = process.argv.slice(1);
    const settings = JSON.parse(readFileSync(0, "utf8"));
    const capabilities = (values) => (values ?? []).map((value) => value.replace(/^CAP_/, "")).sort();
    const dropped = capabilities(settings.CapDrop);
    const added = capabilities(settings.CapAdd);
    const security = settings.SecurityOpt ?? [];
    assert.equal(settings.PidsLimit, Number(limit), "Incorrect PID limit for " + service);
    if (service !== "identity") {
      assert.deepEqual(dropped, ["ALL"], "Incorrect dropped capabilities for " + service);
      assert.deepEqual(added, service === "postgres" ? ["CHOWN", "DAC_OVERRIDE", "FOWNER", "SETGID", "SETUID"] : [],
        "Incorrect added capabilities for " + service);
      assert.ok(security.some((value) => /^no-new-privileges(?:(?:=|:)true)?$/.test(value)),
        "Missing no-new-privileges for " + service);
    }
    console.log(JSON.stringify({ service, pidsLimit: settings.PidsLimit, dropped, added, security }));
  ' "${service}" "${pids_limit}"
}

# Keep negative controls inside the disposable configuration and Compose projects.
database_secret() {
  docker run --rm --network none --user 0 --read-only \
    --mount "type=bind,source=${config},target=/fixture" \
    --entrypoint /bin/bash "${postgres_image}" -euc '
      cd /fixture/postgres
      case "$1" in
        invalidate)
          cp -p runtime.password runtime.password.witness
          printf invalid >runtime.password
          ;;
        correct) cat runtime.password.witness >runtime.password; rm runtime.password.witness ;;
        malformed-cert)
          cp -p server.pem server.pem.witness
          printf "not a PEM certificate\n" >server.pem
          ;;
        wrong-host)
          cp -p server.pem server.pem.witness
          openssl req -new -key server.key -subj /CN=another.example.test \
            -addext subjectAltName=DNS:another.example.test -addext extendedKeyUsage=serverAuth |
            openssl x509 -req -CA /fixture/ca.pem -CAkey /fixture/ca.key -set_serial 701 \
              -days 1 -copy_extensions copy >server.pem
          ;;
        wrong-trust)
          cp -p server.pem server.pem.witness
          openssl req -x509 -key server.key -subj /CN=postgres -addext subjectAltName=DNS:postgres \
            -addext extendedKeyUsage=serverAuth -days 1 >server.pem
          ;;
        restore-cert) cat server.pem.witness >server.pem; rm server.pem.witness ;;
        mismatched-key)
          cp -p server.key server.key.witness
          openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 2>/dev/null >server.key
          ;;
        restore-key) cat server.key.witness >server.key; rm server.key.witness ;;
        open-key) chmod 0644 server.key ;;
        secure-key) chmod 0600 server.key ;;
      esac
    ' witness "$1"
}

wait_postgres() {
  local expected="$1" cid="$2" diagnostic="${3:-must contain 64 lowercase hexadecimal characters}" state output
  for _ in $(seq 1 45); do
    state="$(docker inspect --format '{{.State.Health.Status}}' "${cid}")"
    # Health status and recorded probe output can outlive a docker restart. Require
    # a live socket and run the role witness against this listener before trusting it.
    if docker exec "${cid}" pg_isready -q -h /var/run/postgresql -U postgres -d claimcore; then
      if output="$(docker exec "${cid}" /bin/bash /etc/claimcore-postgres-roles.sh ready 2>&1)"; then
        if [[ "${expected}" == refused ]]; then
          echo 'Invalid provisioning was announced as healthy.' >&2
          return 1
        fi
        if [[ "${state}" == healthy ]]; then
          return 0
        fi
      elif [[ "${expected}" == refused && "${output}" == *"${diagnostic}"* ]]; then
        return 0
      fi
    fi
    sleep 2
  done
  docker logs --tail 25 "${cid}" >&2
  docker inspect --format '{{json .State.Health}}' "${cid}" >&2
  return 1
}

require_boot_refusal() {
  local cid="$1" diagnostic="$2" output
  for _ in $(seq 1 25); do
    output="$(docker logs "${cid}" 2>&1)"
    if [[ "${output}" == *"${diagnostic}"* ]]; then
      return 0
    fi
    sleep 1
  done
  docker logs --tail 25 "${cid}" >&2
  return 1
}

verify_preflight() {
  local fault="$1" correct="$2" diagnostic="$3"
  shift 3
  local stack=("$@") cid
  database_secret "${fault}"
  "${stack[@]}" up --detach postgres
  cid="$("${stack[@]}" ps --all --quiet postgres)"
  require_boot_refusal "${cid}" "${diagnostic}"
  "${stack[@]}" stop postgres
  "${stack[@]}" run --rm --no-deps -T --entrypoint /bin/bash postgres -ec "test ! -e \"\$PGDATA/PG_VERSION\""
  database_secret "${correct}"
}

wait_for_control_database() {
  local stack=("$@") listening
  for _ in $(seq 1 45); do
    listening="$("${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d postgres \
      -c "SELECT current_setting('listen_addresses') <> ''" 2>/dev/null || true)"
    if [[ "${listening}" == t ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

verify_interrupted_bootstrap() {
  local stack=("$@") cid owner stored cluster
  verify_preflight invalidate correct 'must contain 64 lowercase hexadecimal characters' "${stack[@]}"
  verify_preflight malformed-cert restore-cert 'TLS server certificate is not a valid PEM' "${stack[@]}"
  verify_preflight mismatched-key restore-key 'TLS private key does not match' "${stack[@]}"
  verify_preflight wrong-host restore-cert 'TLS certificate does not validate for postgres' "${stack[@]}"
  verify_preflight wrong-trust restore-cert 'TLS certificate does not validate for postgres' "${stack[@]}"
  verify_preflight open-key secure-key 'TLS key must have owner-only permissions' "${stack[@]}"
  echo 'Invalid password, PEM, key pairing, hostname, trust and key mode all left PG_VERSION absent.'

  # Start the pinned upstream entrypoint alone to leave an initialized volume without app roles.
  local interrupted="${work}/interrupted.yaml"
  cat >"${interrupted}" <<'YAML'
services:
  postgres:
    entrypoint: [/usr/local/bin/docker-entrypoint.sh]
    environment:
      POSTGRES_DB: postgres
YAML
  "${stack[@]}" -f "${interrupted}" up --detach --force-recreate postgres
  wait_for_control_database "${stack[@]}"
  owner="$("${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d postgres \
    -c "SELECT EXISTS(SELECT FROM pg_database WHERE datname='claimcore'), (SELECT count(*) FROM pg_roles WHERE rolname LIKE 'claimcore_%')")"
  [[ "${owner}" == 'f|0' ]]
  cluster="$("${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d postgres \
    -c 'SELECT system_identifier FROM pg_control_system()')"

  "${stack[@]}" stop postgres
  "${stack[@]}" up --detach --force-recreate postgres
  cid="$("${stack[@]}" ps --all --quiet postgres)"
  require_boot_refusal "${cid}" 'no claimcore database. Startup stopped'
  "${stack[@]}" stop postgres

  # Only this fixture's operator completes the pristine database creation. The runtime
  # wrapper never guesses whether a cluster lacking the database contained real data.
  "${stack[@]}" -f "${interrupted}" up --detach --force-recreate postgres
  wait_for_control_database "${stack[@]}"
  stored="$("${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d postgres \
    -c "SELECT system_identifier, EXISTS(SELECT FROM pg_database WHERE datname='claimcore'), (SELECT count(*) FROM pg_roles WHERE rolname LIKE 'claimcore_%') FROM pg_control_system()")"
  [[ "${stored}" == "${cluster}|f|0" ]]
  "${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d postgres -c 'CREATE DATABASE claimcore TEMPLATE template0'
  "${stack[@]}" stop postgres
  "${stack[@]}" up --detach --force-recreate postgres
  cid="$("${stack[@]}" ps --quiet postgres)"
  wait_postgres healthy "${cid}"
  stored="$("${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d claimcore \
    -c 'SELECT system_identifier FROM pg_control_system()')"
  [[ "${stored}" == "${cluster}" ]]
  echo 'Startup refused before database creation without mutating the cluster, then provisioned the same volume after operator creation.'

  # An initialized cluster with a partial or drifted grant remains preserved and unhealthy.
  "${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d claimcore \
    -c 'CREATE TABLE public.compose_witness(i integer); INSERT INTO public.compose_witness VALUES (7);'
  "${stack[@]}" restart postgres
  wait_postgres healthy "${cid}"
  stored="$("${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d claimcore \
    -c 'SELECT i FROM public.compose_witness; DROP TABLE public.compose_witness;')"
  [[ "${stored}" == 7 ]]

  "${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d claimcore \
    -c 'ALTER ROLE claimcore_runtime CREATEDB'
  "${stack[@]}" restart postgres
  wait_postgres refused "${cid}" 'the database operator inspect the existing volume'
  stored="$("${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d claimcore \
    -c "SELECT rolcreatedb FROM pg_roles WHERE rolname='claimcore_runtime'")"
  [[ "${stored}" == t ]]
  "${stack[@]}" exec -T postgres psql -XwqAt -U postgres -d claimcore \
    -c 'ALTER ROLE claimcore_runtime NOCREATEDB'
  wait_postgres healthy "${cid}"
  echo 'PostgreSQL resumed an interrupted pristine bootstrap and preserved provisioned state on restart.'
}

# A copied locale retains its source mode before packaging. Stage a private-mode locale for the non-root start.
bash "${root}/eng/source-archive.sh" "${work}/source.tar.gz"
mkdir "${build_source}"
tar -xzf "${work}/source.tar.gz" -C "${build_source}"
chmod 0600 "${build_source}/web/locales/ar.json"
bash "${build_source}/eng/source-archive.sh" "${build_source}/SOURCE.tar.gz"
bash "${build_source}/deployment/configure-local.sh" "${config}" "Smoke owner" Etc/UTC "${web_port}" "${identity_port}"

postgres_image="$(sed -nE 's/^[[:space:]]+image: (postgres:[^[:space:]]+)$/\1/p' "${root}/deployment/compose.yaml" | sort -u)"
# Preflight the production database provisioning before building the candidate runtime image.
export CLAIMCORE_SERVER_SHA256=0000000000000000000000000000000000000000000000000000000000000000
export CLAIMCORE_BIND_ADDRESS=127.0.0.1
export CLAIMCORE_PRODUCTION_SMOKE_ORIGIN="https://localhost:${web_port}"
production=(docker compose --project-name "claimcore-smoke-$(basename "${work}" | tr '[:upper:]_.' '[:lower:]--')" \
  --env-file "${config}/compose.env" --env-file "${config}/web.env" -f "${build_source}/deployment/production/compose.yaml")
verify_interrupted_bootstrap "${production[@]}"
"${production[@]}" down --volumes --timeout 20
verify_interrupted_bootstrap "${compose[@]}"

start --build
require_log '"kind":"INITIALIZED"'
require_log '"caseReferencePolicy":"SYSTEM_ASSIGNED_ONLY"'
verify_cold_identity_dependency() {
  local url="https://localhost:${web_port}" status
  "${compose[@]}" stop identity
  "${compose[@]}" restart web
  wait_for_web
  status="$(curl --silent --show-error --max-time 12 --cacert "${config}/ca.pem" \
    --output "${work}/identity-dependency.json" --write-out '%{http_code}' "${url}/health/identity")"
  [[ "${status}" == 503 ]]
  grep -Fq 'IDENTITY_PROVIDER_UNAVAILABLE' "${work}/identity-dependency.json"
  status="$(curl --silent --show-error --max-time 12 --cacert "${config}/ca.pem" \
    --output "${work}/identity-login.json" --write-out '%{http_code}' "${url}/auth/login")"
  [[ "${status}" == 503 ]]
  grep -Fq 'IDENTITY_PROVIDER_UNAVAILABLE' "${work}/identity-login.json"
  "${compose[@]}" start identity
  for _ in $(seq 1 45); do
    status="$(curl --silent --show-error --max-time 12 --cacert "${config}/ca.pem" \
      --output /dev/null --write-out '%{http_code}' "${url}/health/identity")"
    if [[ "${status}" == 200 ]]; then
      echo 'Local readiness survived cold discovery failure and the issuer recovered without restarting Web.'
      return 0
    fi
    sleep 2
  done
  return 1
}

verify_database_owner
verify_database_network
verify_owner_commands
verify_cold_identity_dependency
verify_container_settings postgres 512
verify_container_settings web 1024
verify_container_settings identity 512
verify_container_settings administration 256

"${compose[@]}" down --timeout 20
start
require_log '"kind":"INITIALIZATION_SKIPPED"'
require_log '"caseReferencePolicy":"SYSTEM_ASSIGNED_ONLY"'

echo "Compose restart smoke passed: the installation survived down and up."

# Start the production template with this checkout's exact final image and the disposable
# identity and witness on the evaluation network. No registry image is fetched or published.
evaluation_project="$(sed -n 's/^CLAIMCORE_COMPOSE_PROJECT=//p' "${config}/compose.env")"
[[ "${evaluation_project}" =~ ^claimcore-[a-f0-9]{16}$ ]]
"${compose[@]}" stop web
qualification_network="${evaluation_project}-qualification-dependencies"
docker network create "${qualification_network}" > /dev/null
qualification_witness="$("${compose[@]}" ps --quiet witness)"
qualification_identity="$("${compose[@]}" ps --quiet identity)"
docker network connect --alias witness "${qualification_network}" "${qualification_witness}"
docker network connect --alias identity.localhost "${qualification_network}" "${qualification_identity}"
web_image="${evaluation_project}-runtime"
production_override="${work}/production.override.yaml"
cat >"${production_override}" <<YAML
services:
  administration:
    image: ${web_image}
    pull_policy: never
    volumes:
      - "${config}/ca.pem:/etc/claimcore/ca.pem:ro"
    networks: [database, evaluation]
  web:
    image: ${web_image}
    pull_policy: never
    environment:
      CLAIMCORE_WEB_PROBE_CA_CERT_FILE: /etc/claimcore/runtime/ca.pem
      CLAIMCORE_OIDC_CA_CERT_FILE: /etc/claimcore/runtime/ca.pem
    networks: [default, database, evaluation]
networks:
  evaluation:
    external: true
    name: ${qualification_network}
YAML
production+=(-f "${production_override}")
"${production[@]}" up --wait --wait-timeout 120 -d postgres
owner_subject="$(sed -n 's/^CLAIMCORE_OWNER_SUBJECT=//p' "${config}/compose.env")"
"${production[@]}" --profile administration run --rm administration initialize Etc/UTC \
  --owner-issuer "https://identity.localhost:${identity_port}/realms/claimcore" \
  --owner-subject "${owner_subject}" --owner-name 'Synthetic production-template owner' \
  --owner-role CASE_HANDLER --owner-role PAYMENT_RECORDER --owner-role DATA_STEWARD
"${production[@]}" up --wait --wait-timeout 180 -d web
curl --fail --silent --show-error --max-time 15 --cacert "${config}/ca.pem" \
  "${CLAIMCORE_PRODUCTION_SMOKE_ORIGIN}/health/recovery"
# Test the complete omission against the evaluator's exact packaged executable.
production_web="$("${production[@]}" ps --quiet web)"
missing_lazy="${work}/missing-lazy"
mkdir -m 755 "${missing_lazy}"
docker cp "${production_web}:/app/wwwroot/." "${missing_lazy}/"
python3 - "${missing_lazy}" <<'PYTHON'
import json
from pathlib import Path
import sys

root = Path(sys.argv[1])
manifest = root / "public-assets.json"
inventory = json.loads(manifest.read_bytes())
html = (root / "index.html").read_text()
lazy, = (path for path in inventory["graph"] if "/assets/workspace~RecoveryPanel-" in path)
assert lazy not in html
assert any(lazy in dependencies for dependencies in inventory["graph"].values())
(root / lazy.lstrip("/")).unlink()
del inventory["graph"][lazy]
for dependencies in inventory["graph"].values():
    if lazy in dependencies:
        dependencies.remove(lazy)
inventory["assets"] = [asset for asset in inventory["assets"] if asset["path"] != lazy]
manifest.write_text(json.dumps(inventory))
PYTHON
if docker run --rm --network none --entrypoint dotnet \
    --mount "type=bind,source=${missing_lazy},target=/qa,readonly" "${web_image}" \
    /app/claimcore-server.dll validate-browser-assets /qa > "${work}/missing-lazy.json" 2>&1; then
  echo "The packaged server admitted a wholly omitted lazy module." >&2
  exit 1
fi
grep -Fq '"code":"ASSETSMISSING"' "${work}/missing-lazy.json"
echo "The packaged server refused a wholly omitted lazy module."
(cd "${build_source}/web" && npm ci && npm exec -- playwright install chromium)
node "${build_source}/web/e2e/production-image-smoke.mjs" \
  "${CLAIMCORE_PRODUCTION_SMOKE_ORIGIN}" "${config}/owner-login.password"
echo "Production template boot, session handshake, lazy entry and viewer checks passed."

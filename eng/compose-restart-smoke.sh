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

cleanup() {
  if [[ -f "${config}/compose.env" ]]; then
    "${compose[@]}" down --volumes --timeout 20 > /dev/null 2>&1 || true
  fi
  # On Linux, configuration hands the PostgreSQL and Keycloak folders to their container users;
  # remove them with the same pinned image that configuration used.
  local image
  image="$(sed -nE 's/^[[:space:]]+image: (postgres:[^[:space:]]+)$/\1/p' "${root}/deployment/compose.yaml")"
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

# A copied locale retains its source mode before packaging. Stage a private-mode locale for the non-root start.
bash "${root}/eng/source-archive.sh" "${work}/source.tar.gz"
mkdir "${build_source}"
tar -xzf "${work}/source.tar.gz" -C "${build_source}"
chmod 0600 "${build_source}/web/locales/ar.json"
bash "${build_source}/eng/source-archive.sh" "${build_source}/SOURCE.tar.gz"
bash "${build_source}/deployment/configure-local.sh" "${config}" "Smoke owner" Etc/UTC "${web_port}" "${identity_port}"

start --build
require_log '"kind":"INITIALIZED"'
verify_database_owner
verify_database_network
verify_owner_commands
verify_container_settings postgres 512
verify_container_settings web 1024
verify_container_settings identity 512
verify_container_settings administration 256

"${compose[@]}" down --timeout 20
start
require_log '"kind":"INITIALIZATION_SKIPPED"'

echo "Compose restart smoke passed: the installation survived down and up."

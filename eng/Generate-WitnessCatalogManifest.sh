#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
image="$(jq -r '.containerImage' db/postgresql-baseline.json)"
run_tag="$(openssl rand -hex 12)"
container="claimcore-witness-catalog-${run_tag}"
password="$(openssl rand -hex 24)"
mkdir -p artifacts
temporary="$(mktemp artifacts/witness-catalog.XXXXXX)"
container_id=""

cleanup() {
  local status=$? container_owner
  trap - EXIT
  if [[ -n "${container_id}" ]]; then
    if ! container_owner="$(docker inspect --format '{{index .Config.Labels "org.claimcore.witness-catalog"}}' "${container_id}" 2>/dev/null)"; then
      status=1
    elif [[ "${container_owner}" == "${run_tag}" ]]; then
      docker stop "${container_id}" >/dev/null 2>&1 || status=1
    else
      status=1
    fi
  fi
  if [[ "${status}" == 0 ]]; then rm -f "${temporary}" || status=1; fi
  return "${status}"
}
trap cleanup EXIT

container_id="$(docker run --rm -d --name "${container}" \
  --label "org.claimcore.witness-catalog=${run_tag}" \
  -e POSTGRES_USER=claimcore_witness_owner \
  -e POSTGRES_PASSWORD="${password}" \
  -e POSTGRES_DB=claimcore_witness_synthetic \
  "${image}" -c fsync=on -c full_page_writes=on -c synchronous_commit=on)"

for _ in $(seq 1 40); do
  if docker exec "${container_id}" psql -X -q -A -t -U claimcore_witness_owner \
    -d claimcore_witness_synthetic -c 'SELECT 1' >/dev/null 2>&1; then break; fi
  sleep 1
done

psql=(docker exec -i "${container_id}" psql -X -v ON_ERROR_STOP=1
  -U claimcore_witness_owner -d claimcore_witness_synthetic)
printf 'CREATE ROLE claimcore_witness_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;\nCREATE ROLE claimcore_witness_auditor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;\n' |
  "${psql[@]}" >/dev/null
node eng/ci/database/baseline-source.mjs db/witness-baseline.json | "${psql[@]}" >/dev/null
actual_version="$(printf "SELECT current_setting('server_version_num');\n" | "${psql[@]}" -q -A -t)"
[[ "${actual_version}" == 180006 ]] || {
  printf 'Witness catalog generation requires PostgreSQL 18.6.\n' >&2
  exit 1
}
digest="$("${psql[@]}" -q -A -t <db/witness-catalog.sql)"
[[ "${digest}" =~ ^[0-9a-f]{64}$ ]] || {
  printf 'Witness catalog projection failed.\n' >&2
  exit 1
}
jq -n --arg digest "${digest}" '{serverVersion:"18.6",sha256:$digest}' >"${temporary}"
mv "${temporary}" db/witness-catalog.pg18.6.json
printf 'Generated isolated PostgreSQL 18.6 witness catalog manifest.\n'
cleanup

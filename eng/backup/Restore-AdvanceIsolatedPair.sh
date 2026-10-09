#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
image="$(jq -er '.containerImage' "${repo_root}/db/postgresql-baseline.json")"
primary_source="${1:?labelled primary source ID}"
witness_source="${2:?labelled witness source ID}"
scratch="${3:?owner-private synthetic scratch}"
primary_prefix="${4:?finite completed primary WAL prefix}"
witness_prefix="${5:?finite completed witness WAL prefix}"
pg_bin="${CLAIMCORE_PG_BIN:-/opt/homebrew/opt/libpq/bin}"
export PATH="${pg_bin}:${PATH}"

primary_restored=""
witness_restored=""
stage="source-validation"
cleanup_error() {
  printf 'restore-stage=%s\n' "${stage}" >&2
  for current in "${witness_restored}" "${primary_restored}"; do
    if [[ -n "${current}" ]]; then docker stop "${current}" >/dev/null 2>&1 || true; fi
  done
}
trap cleanup_error ERR

[[ "${primary_source}" =~ ^[0-9a-f]{64}$ && "${witness_source}" =~ ^[0-9a-f]{64}$ ]]
[[ "${primary_source}" != "${witness_source}" ]]
[[ "${primary_prefix}" =~ ^[0-9A-F]{24}(,[0-9A-F]{24})*$ ]]
[[ "${witness_prefix}" =~ ^[0-9A-F]{24}(,[0-9A-F]{24})*$ ]]
physical_scratch="$(cd "${scratch}" && pwd -P)"
[[ -d "${scratch}" && ! -L "${scratch}" && "${physical_scratch}" == "${scratch}" ]]

stage="source-image-label"
for source in "${primary_source}" "${witness_source}"; do
  label="$(docker inspect --format '{{index .Config.Labels "org.claimcore.test-run"}}' "${source}")"
  source_image="$(docker inspect --format '{{.Config.Image}}' "${source}")"
  [[ "${label}" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$ && "${source_image}" == "${image}" ]]
done

stage="original-base-copy"
mkdir -m 700 "${scratch}/advanced"
for name in primary witness; do
  mkdir -m 700 "${scratch}/advanced/${name}"
  cp -Rp "${scratch}/${name}/." "${scratch}/advanced/${name}/"
  pg_verifybackup "${scratch}/advanced/${name}" >/dev/null
done

stage="completed-wal-tail"
for name in primary witness; do
  source="${primary_source}"
  prefix="${primary_prefix}"
  if [[ "${name}" == witness ]]; then
    source="${witness_source}"
    prefix="${witness_prefix}"
  fi
  IFS=, read -r -a segments <<<"${prefix}"
  [[ "${#segments[@]}" -ge 1 && "${#segments[@]}" -le 1000 ]]
  for segment in "${segments[@]}"; do
    docker cp "${source}:/var/lib/postgresql/18/docker/pg_wal/${segment}" \
      "${scratch}/advanced/${name}/pg_wal/${segment}" >/dev/null
  done
  size_kib="$(du -sk "${scratch}/advanced/${name}/pg_wal" | awk '{print $1}')"
  [[ "${size_kib}" =~ ^[0-9]+$ && "${size_kib}" -gt 0 && "${size_kib}" -le 1048576 ]]
done

start_one() {
  local data="$1" name="$2" container port
  stage="${name}-container"
  container="$(docker run --rm -d -P \
    --label "org.claimcore.restore-test=$$" --entrypoint sleep "${image}" 900)"
  [[ "${container}" =~ ^[0-9a-f]{64}$ ]]
  if [[ "${name}" == primary ]]; then primary_restored="${container}"; else witness_restored="${container}"; fi
  stage="${name}-directory"
  docker exec -u root "${container}" mkdir -p /var/lib/postgresql/18/docker
  stage="${name}-copy"
  docker cp "${data}/." "${container}:/var/lib/postgresql/18/docker" >/dev/null
  stage="${name}-permissions"
  docker exec -u root "${container}" chown -R postgres:postgres /var/lib/postgresql/18/docker
  docker exec -u root "${container}" chmod 700 /var/lib/postgresql/18/docker
  stage="${name}-start"
  docker exec -u postgres "${container}" pg_ctl -D /var/lib/postgresql/18/docker \
    -l /tmp/claimcore-restore.log \
    -o "-c fsync=on -c full_page_writes=on -c synchronous_commit=on -c wal_keep_size=1024MB -c listen_addresses='*'" \
    start >/dev/null
  # Port publication and TCP readiness are separate asynchronous Docker boundaries.
  for attempt in {1..30}; do
    stage="${name}-port"
    port="$(docker port "${container}" 5432/tcp |
      sed -nE 's/^(0\.0\.0\.0|127\.0\.0\.1):([0-9]+)$/\2/p' | sort -u)"
    if [[ "${port}" =~ ^[0-9]+$ ]]; then
      stage="${name}-ready"
      if pg_isready -q -h 127.0.0.1 -p "${port}" -t 2; then break; fi
    fi
    [[ "${attempt}" != 30 ]]
    sleep 0.5
  done
  started_port="${port}"
}

stage="primary-boot"
start_one "${scratch}/advanced/primary" primary
primary_port="${started_port}"
stage="witness-boot"
start_one "${scratch}/advanced/witness" witness
witness_port="${started_port}"
printf '%s %s %s %s\n' "${primary_restored}" "${primary_port}" \
  "${witness_restored}" "${witness_port}"
trap - ERR

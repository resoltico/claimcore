#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
image="$(jq -er '.containerImage' "${repo_root}/db/postgresql-baseline.json")"
primary_source="${1:?primary test container ID}"
witness_source="${2:?witness test container ID}"
primary_role="${3:?primary owner role}"
witness_role="${4:?witness owner role}"
scratch="${5:?owner-private test scratch directory}"
pg_bin="${CLAIMCORE_PG_BIN:-/opt/homebrew/opt/libpq/bin}"
export PATH="${pg_bin}:${PATH}"

primary_restored=""
witness_restored=""
stage="source-validation"
preserve_diagnostics() {
  local directory current name
  mkdir -p "${repo_root}/artifacts/restore-failures" || return
  directory="$(mktemp -d "${repo_root}/artifacts/restore-failures/restore.XXXXXXXX")" || return
  printf '%s\n' "${stage}" >"${directory}/stage.txt" || return
  for name in primary witness; do
    if [[ "${name}" == primary ]]; then current="${primary_restored}"; else current="${witness_restored}"; fi
    if [[ -z "${current}" ]]; then continue; fi
    docker exec "${current}" tail -c 65536 /tmp/claimcore-restore.log \
      >"${directory}/${name}.postgres.log" 2>/dev/null || true
    docker inspect --format 'status={{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}}' \
      "${current}" >"${directory}/${name}.state.txt" 2>/dev/null || true
    docker port "${current}" 5432/tcp >"${directory}/${name}.port.txt" 2>/dev/null || true
  done
}
cleanup_error() {
  # lint-exception: LX-0049
  # shellcheck disable=SC2310
  preserve_diagnostics >/dev/null 2>&1 || true
  printf 'restore-stage=%s\n' "${stage}" >&2
  for current in "${witness_restored}" "${primary_restored}"; do
    if [[ -n "${current}" ]]; then docker stop "${current}" >/dev/null 2>&1 || true; fi
  done
}
trap cleanup_error ERR

[[ "${primary_source}" =~ ^[0-9a-f]{64}$ && "${witness_source}" =~ ^[0-9a-f]{64}$ ]]
[[ "${primary_source}" != "${witness_source}" ]]
[[ "${primary_role}" =~ ^[a-z][a-z0-9_]{0,62}$ && "${witness_role}" =~ ^[a-z][a-z0-9_]{0,62}$ ]]
physical_scratch="$(cd "${scratch}" && pwd -P)"
[[ -d "${scratch}" && ! -L "${scratch}" && "${physical_scratch}" == "${scratch}" ]]

stage="source-image-label"
for source in "${primary_source}" "${witness_source}"; do
  label="$(docker inspect --format '{{index .Config.Labels "org.claimcore.test-run"}}' "${source}")"
  source_image="$(docker inspect --format '{{.Config.Image}}' "${source}")"
  [[ "${label}" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$ && "${source_image}" == "${image}" ]]
done

capture_one() {
  local source="$1" role="$2" name="$3" backup="$4"
  local inside="/tmp/claimcore-restored-pair-${name}-$$"
  docker exec -u postgres "${source}" pg_basebackup \
    -h /var/run/postgresql -U "${role}" -D "${inside}" \
    --format=plain --wal-method=stream --checkpoint=fast \
    --manifest-checksums=SHA256 --no-password >/dev/null
  mkdir -m 700 "${backup}"
  docker cp "${source}:${inside}/." "${backup}" >/dev/null
  pg_verifybackup "${backup}" >/dev/null
}

stage="primary-base-backup"
capture_one "${primary_source}" "${primary_role}" primary "${scratch}/primary"
stage="witness-base-backup"
capture_one "${witness_source}" "${witness_role}" witness "${scratch}/witness"

stage="backup-encryption"
mkdir -m 700 "${scratch}/archive" "${scratch}/recovered"
age-keygen -o "${scratch}/identity.age" >/dev/null 2>&1
recipient="$(age-keygen -y "${scratch}/identity.age")"

for name in primary witness; do
  tar -C "${scratch}/${name}" -cf - . |
    age --encrypt --recipient "${recipient}" --output "${scratch}/archive/${name}.tar.age"
  mkdir -m 700 "${scratch}/recovered/${name}" "${scratch}/archive/${name}-wal"
  age --decrypt --identity "${scratch}/identity.age" \
    "${scratch}/archive/${name}.tar.age" |
    tar -C "${scratch}/recovered/${name}" -xf -
  pg_verifybackup "${scratch}/recovered/${name}" >/dev/null

  found_wal=0
  find "${scratch}/${name}/pg_wal" -maxdepth 1 -type f >"${scratch}/${name}.wal-list"
  while IFS= read -r segment; do
    segment_name="${segment##*/}"
    if [[ ! "${segment_name}" =~ ^[0-9A-F]{24}$ ]]; then continue; fi
    found_wal=1
    pg_waldump --quiet --limit=1 "${segment}" >/dev/null
    age --encrypt --recipient "${recipient}" \
      --output "${scratch}/archive/${name}-wal/${segment_name}.age" "${segment}"
    age --decrypt --identity "${scratch}/identity.age" \
      "${scratch}/archive/${name}-wal/${segment_name}.age" |
      cmp - "${segment}" >/dev/null
  done <"${scratch}/${name}.wal-list"
  [[ "${found_wal}" == 1 ]]
done

start_one() {
  local data="$1" name="$2"
  local container
  stage="${name}-container"
  container="$(docker run --rm -d -P \
    --label "org.claimcore.restore-test=$$" --entrypoint sleep "${image}" 900)"
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
    -o "-c fsync=on -c full_page_writes=on -c synchronous_commit=on -c listen_addresses='*'" \
    start >/dev/null
  local port
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
start_one "${scratch}/recovered/primary" primary
primary_port="${started_port}"
stage="witness-boot"
start_one "${scratch}/recovered/witness" witness
witness_port="${started_port}"
printf '%s %s %s %s\n' "${primary_restored}" "${primary_port}" \
  "${witness_restored}" "${witness_port}"
trap - ERR

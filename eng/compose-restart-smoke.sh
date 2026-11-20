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
  "${compose[@]}" logs --tail 80 administration web || true
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

# A copied locale retains its source mode before packaging. Stage a private-mode locale for the non-root start.
bash "${root}/eng/source-archive.sh" "${work}/source.tar.gz"
mkdir "${build_source}"
tar -xzf "${work}/source.tar.gz" -C "${build_source}"
chmod 0600 "${build_source}/web/locales/ar.json"
bash "${build_source}/eng/source-archive.sh" "${build_source}/SOURCE.tar.gz"
bash "${build_source}/deployment/configure-local.sh" "${config}" "Smoke owner" Etc/UTC "${web_port}" "${identity_port}"

start --build
require_log '"kind":"INITIALIZED"'

"${compose[@]}" down --timeout 20
start
require_log '"kind":"INITIALIZATION_SKIPPED"'

echo "Compose restart smoke passed: the installation survived down and up."

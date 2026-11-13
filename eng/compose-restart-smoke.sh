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

compose=(docker compose --env-file "${config}/compose.env" -f "${root}/deployment/compose.yaml")

cleanup() {
  if [[ -f "${config}/compose.env" ]]; then
    "${compose[@]}" down --volumes --timeout 20 > /dev/null 2>&1 || true
  fi
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

cd "${root}"
bash deployment/configure-local.sh "${config}" "Smoke owner" Etc/UTC "${web_port}" "${identity_port}"
bash eng/source-archive.sh SOURCE.tar.gz

start --build
require_log '"kind":"INITIALIZED"'

"${compose[@]}" down --timeout 20
start
require_log "Existing installation found; initialization skipped."

echo "Compose restart smoke passed: the installation survived down and up."

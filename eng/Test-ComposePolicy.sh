#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
compose_file="$repo_root/compose.yaml"
nonce="$(printf '%s' "$$-$(date -u +%s)" | shasum -a 256 | cut -c1-12)"
equal_project="claimcore-policy-equal-$nonce"
drift_project="claimcore-policy-drift-$nonce"
owner_one="cc-owner-policy-7bcd260eebf13d2a"
runtime_one="cc-runtime-policy-6d956624e981f3f0"
runtime_two="cc-runtime-policy-e63a1d766184a69d"

require_project_name() {
  local project="$1"
  [[ "$project" =~ ^claimcore-policy-(equal|drift)-[a-f0-9]{12}$ ]]
}

volume_name() {
  printf '%s_postgres-data' "$1"
}

cleanup_project() {
  local project="$1"
  local volume
  local label
  require_project_name "$project"
  volume="$(volume_name "$project")"

  if docker volume inspect "$volume" >/dev/null 2>&1; then
    label="$(docker volume inspect --format '{{ index .Labels "com.docker.compose.project" }}' "$volume")"

    if [[ "$label" != "$project" ]]; then
      printf '%s\n' 'Refusing cleanup because a disposable volume label did not match.' >&2
      return 1
    fi
  fi

  CLAIMCORE_COMPOSE_PROJECT="$project" \
    POSTGRES_PASSWORD="$owner_one" \
    CLAIMCORE_APP_PASSWORD="$runtime_one" \
    CLAIMCORE_POSTGRES_PORT=0 \
    docker compose --file "$compose_file" --project-name "$project" \
      down --volumes --remove-orphans >/dev/null 2>&1 || true
}

cleanup_all() {
  cleanup_project "$equal_project"
  cleanup_project "$drift_project"
}

trap cleanup_all EXIT

require_project_name "$equal_project"
require_project_name "$drift_project"

CLAIMCORE_COMPOSE_PROJECT="$drift_project" \
  POSTGRES_PASSWORD="$owner_one" \
  CLAIMCORE_APP_PASSWORD="$runtime_one" \
  CLAIMCORE_POSTGRES_PORT=0 \
  docker compose --file "$compose_file" --project-name "$drift_project" config --quiet

if CLAIMCORE_COMPOSE_PROJECT="$drift_project" POSTGRES_PASSWORD='' \
  CLAIMCORE_APP_PASSWORD="$runtime_one" CLAIMCORE_POSTGRES_PORT=0 \
  docker compose --file "$compose_file" --project-name "$drift_project" config --quiet \
  >/dev/null 2>&1; then
  printf '%s\n' 'Compose accepted an empty owner password.' >&2
  exit 1
fi

if CLAIMCORE_COMPOSE_PROJECT="$drift_project" POSTGRES_PASSWORD="$owner_one" \
  CLAIMCORE_APP_PASSWORD='' CLAIMCORE_POSTGRES_PORT=0 \
  docker compose --file "$compose_file" --project-name "$drift_project" config --quiet \
  >/dev/null 2>&1; then
  printf '%s\n' 'Compose accepted an empty runtime password.' >&2
  exit 1
fi

if CLAIMCORE_COMPOSE_PROJECT="$equal_project" POSTGRES_PASSWORD="$owner_one" \
  CLAIMCORE_APP_PASSWORD="$owner_one" CLAIMCORE_POSTGRES_PORT=0 \
  docker compose --file "$compose_file" --project-name "$equal_project" \
  up --detach --wait >/dev/null 2>&1; then
  printf '%s\n' 'Compose accepted equal owner and runtime passwords.' >&2
  exit 1
fi

cleanup_project "$equal_project"

CLAIMCORE_COMPOSE_PROJECT="$drift_project" \
  POSTGRES_PASSWORD="$owner_one" \
  CLAIMCORE_APP_PASSWORD="$runtime_one" \
  CLAIMCORE_POSTGRES_PORT=0 \
  docker compose --file "$compose_file" --project-name "$drift_project" \
  up --detach --wait >/dev/null

CLAIMCORE_COMPOSE_PROJECT="$drift_project" \
  POSTGRES_PASSWORD="$owner_one" \
  CLAIMCORE_APP_PASSWORD="$runtime_one" \
  CLAIMCORE_POSTGRES_PORT=0 \
  docker compose --file "$compose_file" --project-name "$drift_project" \
  down >/dev/null

if CLAIMCORE_COMPOSE_PROJECT="$drift_project" POSTGRES_PASSWORD="$owner_one" \
  CLAIMCORE_APP_PASSWORD="$runtime_two" CLAIMCORE_POSTGRES_PORT=0 \
  docker compose --file "$compose_file" --project-name "$drift_project" \
  up --detach --wait >/dev/null 2>&1; then
  printf '%s\n' 'Retained-volume credential drift unexpectedly became healthy.' >&2
  exit 1
fi

printf '%s\n' 'Compose credential and retained-volume policy checks passed.'

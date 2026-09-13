#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 1 ]] || (( ${#1} > 100 )) ||
  [[ ! "$1" =~ ^claimcore-(integration|acceptance|browser)-[A-Za-z0-9][A-Za-z0-9_.-]*$ ]]; then
  echo 'Pass one bounded ClaimCore integration, acceptance, or browser test-run label.' >&2
  exit 64
fi

run_label="$1"

container_absent() {
  local existing
  if ! existing="$(docker container ls --all --quiet --no-trunc --filter "id=$1" 2>/dev/null)"; then
    echo 'Could not verify whether a test container disappeared.' >&2
    return 2
  fi
  [[ -z "$existing" ]]
}

if ! containers="$(docker container ls --all --quiet --no-trunc 2>/dev/null)"; then
  echo 'Could not enumerate Docker containers for scoped test cleanup.' >&2
  exit 1
fi

while IFS= read -r container; do
  [[ -z "$container" ]] && continue
  if [[ ! "$container" =~ ^[a-f0-9]{64}$ ]]; then
    echo 'Docker returned an invalid container ID during scoped test cleanup.' >&2
    exit 1
  fi

  if ! label="$(docker container inspect \
    --format '{{ index .Config.Labels "org.claimcore.test-run" }}' "$container" 2>/dev/null)"; then
    if container_absent "$container"; then continue; fi
    echo 'Could not inspect a container during scoped test cleanup.' >&2
    exit 1
  fi

  if [[ "$label" != "$run_label" ]]; then continue; fi

  if ! docker container rm --force --volumes "$container" >/dev/null 2>&1; then
    echo 'Could not remove an exactly labeled test container and its anonymous volumes.' >&2
    exit 1
  fi
done <<< "$containers"

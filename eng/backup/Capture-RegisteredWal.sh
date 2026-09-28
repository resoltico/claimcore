#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
image="$(jq -er '.containerImage' "$repo_root/db/postgresql-baseline.json")"
primary_source="${1:?labelled primary source ID}"
witness_source="${2:?labelled witness source ID}"
scratch="${3:?owner-private synthetic scratch}"
primary_prefix="${4:?finite primary WAL prefix}"
witness_prefix="${5:?finite witness WAL prefix}"
pg_bin="${CLAIMCORE_PG_BIN:-/opt/homebrew/opt/libpq/bin}"
export PATH="$pg_bin:$PATH"
stage="source-validation"
trap 'printf "wal-capture-stage=%s\n" "$stage" >&2' ERR

[[ "$primary_source" =~ ^[0-9a-f]{64}$ && "$witness_source" =~ ^[0-9a-f]{64}$ ]]
[[ "$primary_source" != "$witness_source" ]]
[[ "$primary_prefix" =~ ^[0-9A-F]{24}(,[0-9A-F]{24})*$ ]]
[[ "$witness_prefix" =~ ^[0-9A-F]{24}(,[0-9A-F]{24})*$ ]]
[[ -d "$scratch" && ! -L "$scratch" && "$(cd "$scratch" && pwd -P)" == "$scratch" ]]
[[ -f "$scratch/identity.age" && ! -L "$scratch/identity.age" ]]

stage="source-image-label"
for source in "$primary_source" "$witness_source"; do
  label="$(docker inspect --format '{{index .Config.Labels "org.claimcore.test-run"}}' "$source")"
  source_image="$(docker inspect --format '{{.Config.Image}}' "$source")"
  [[ "$label" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$ && "$source_image" == "$image" ]]
done

stage="age-recipient"
recipient="$(age-keygen -y "$scratch/identity.age")"
stage="fresh-wal-source"
mkdir -m 700 "$scratch/registered-wal"
for name in primary witness; do
  source="$primary_source"
  prefix="$primary_prefix"
  if [[ "$name" == witness ]]; then
    source="$witness_source"
    prefix="$witness_prefix"
  fi
  IFS=, read -r -a segments <<< "$prefix"
  [[ "${#segments[@]}" -ge 1 && "${#segments[@]}" -le 1000 ]]
  mkdir -m 700 "$scratch/registered-wal/$name" \
    "$scratch/archive/$name-registered-wal"
  stage="${name}-wal-copy"
  docker cp "$source:/var/lib/postgresql/18/docker/pg_wal/." \
    "$scratch/registered-wal/$name" >/dev/null
  for segment in "${segments[@]}"; do
    path="$scratch/registered-wal/$name/$segment"
    stage="${name}-wal-present"
    [[ -f "$path" && -r "$path" ]]
    stage="${name}-wal-parse"
    if ! pg_waldump --quiet --limit=1 --path "$scratch/registered-wal/$name" \
        "$segment" >/dev/null \
        2>"$scratch/registered-wal/$name/pg-waldump.err"; then
      if grep -qi 'permission denied' "$scratch/registered-wal/$name/pg-waldump.err"; then
        stage="${name}-wal-permission"
      elif grep -qi 'valid record' "$scratch/registered-wal/$name/pg-waldump.err"; then
        stage="${name}-wal-no-record"
      else
        stage="${name}-wal-invalid"
      fi
      false
    fi
    stage="${name}-wal-encrypt"
    age --encrypt --recipient "$recipient" \
      --output "$scratch/archive/$name-registered-wal/$segment.age" "$path"
  done
done
trap - ERR
printf 'registered-wal-capture=synthetic-only\n'

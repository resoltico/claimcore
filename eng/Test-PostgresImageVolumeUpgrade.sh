#!/usr/bin/env bash
set -euo pipefail

# Qualify the official-to-patched image handoff on one newly created synthetic
# volume. Never use an existing Compose project, container, or database volume.
if [[ "$#" -ne 2 ]]; then
  printf 'Usage: %s <local-image-ref> <linux/amd64|linux/arm64>\n' "$0" >&2
  exit 2
fi

image="$1"
platform="$2"
if [[ -z "$image" || ( "$platform" != linux/amd64 && "$platform" != linux/arm64 ) ]]; then
  printf '%s\n' 'A local image reference and a supported platform are required.' >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
base_image='postgres:18.6@sha256:4ef4dbc939d61acea57712655ddb4b4ab27419c913f94cca0cd57cb3ea3c2280'
image_arch="$(docker image inspect --format '{{.Architecture}}' "$image")"
if [[ "$image_arch" != "${platform#linux/}" ]]; then
  printf 'Image architecture %s does not match %s.\n' "$image_arch" "$platform" >&2
  exit 1
fi

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-pg-upgrade.XXXXXXXX")"
nonce="${tmp_dir##*.}"
volume="claimcore-pg-upgrade-$nonce"
base_container="claimcore-pg-old-$nonce"
patched_container="claimcore-pg-new-$nonce"
created_volume=0
created_base=0
created_patched=0

cleanup() {
  if [[ "$created_patched" -eq 1 ]]; then
    docker rm --force "$patched_container" >/dev/null 2>&1 || true
  fi
  if [[ "$created_base" -eq 1 ]]; then
    docker rm --force "$base_container" >/dev/null 2>&1 || true
  fi
  if [[ "$created_volume" -eq 1 ]]; then
    if [[ "$(docker volume inspect --format '{{ index .Labels "claimcore.postgres-image-upgrade" }}' "$volume" 2>/dev/null)" == "$nonce" ]]; then
      docker volume rm "$volume" >/dev/null 2>&1 || true
    else
      printf '%s\n' 'Refusing to remove a volume whose isolation label changed.' >&2
    fi
  fi
  rmdir "$tmp_dir"
}
trap cleanup EXIT

if docker volume inspect "$volume" >/dev/null 2>&1; then
  printf '%s\n' 'Refusing to adopt an existing volume with the disposable name.' >&2
  exit 1
fi
docker volume create --label "claimcore.postgres-image-upgrade=$nonce" "$volume" >/dev/null
if [[ "$(docker volume inspect --format '{{ index .Labels "claimcore.postgres-image-upgrade" }}' "$volume")" != "$nonce" ]]; then
  printf '%s\n' 'A pre-existing volume has the requested disposable name.' >&2
  exit 1
fi
created_volume=1

psql_in() {
  local container="$1"
  local sql="$2"
  docker exec --user postgres "$container" psql --dbname claimcore \
    --no-psqlrc --set ON_ERROR_STOP=1 --tuples-only --no-align \
    --command "$sql"
}

wait_ready() {
  local container="$1"
  local initialized="$2"
  for _ in {1..60}; do
    if [[ "$(docker inspect --format '{{.State.Running}}' "$container")" != true ]]; then
      printf '%s\n' 'Disposable PostgreSQL container exited during startup.' >&2
      return 1
    fi
    # The first image runs a temporary postmaster during initdb. Wait for the
    # entrypoint's init-complete marker before accepting final SQL readiness.
    if [[ "$initialized" == yes ]] &&
      ! docker logs "$container" 2>&1 | grep -F 'PostgreSQL init process complete; ready for start up.' >/dev/null; then
      sleep 1
      continue
    fi
    if [[ "$(psql_in "$container" 'SELECT 1' 2>/dev/null)" == 1 ]]; then
      return 0
    fi
    sleep 1
  done
  printf '%s\n' 'Disposable PostgreSQL startup did not become ready.' >&2
  return 1
}

start_container() {
  local container="$1"
  local container_image="$2"
  docker run --detach --name "$container" --platform "$platform" --network none \
    --env POSTGRES_USER=postgres \
    --env POSTGRES_DB=claimcore \
    --env POSTGRES_PASSWORD=synthetic-owner-image-upgrade \
    --env CLAIMCORE_APP_PASSWORD=synthetic-runtime-image-upgrade \
    --env POSTGRES_INITDB_ARGS=--auth-host=scram-sha-256 \
    --mount "type=volume,src=$volume,dst=/var/lib/postgresql" \
    --mount "type=bind,src=$repo_root/db/dev-init.sh,dst=/docker-entrypoint-initdb.d/10-claimcore.sh,readonly" \
    "$container_image" >/dev/null
}

verify_state() {
  local container="$1"
  local pgdata
  local data_directory
  local valid
  pgdata="$(docker exec "$container" printenv PGDATA)"
  data_directory="$(psql_in "$container" 'SHOW data_directory')"
  if [[ "$pgdata" != /var/lib/postgresql/18/docker || "$data_directory" != "$pgdata" ]]; then
    printf '%s\n' 'PGDATA or the live data directory differs from PostgreSQL 18 layout.' >&2
    return 1
  fi

  valid="$(psql_in "$container" "
    SELECT current_setting('server_version_num') = '180006'
       AND (SELECT count(*) = 1 FROM pg_roles
            WHERE rolname = 'claimcore_app' AND rolcanlogin AND NOT rolsuper
              AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
              AND NOT rolbypassrls AND NOT rolinherit)
       AND has_database_privilege('claimcore_app', 'claimcore', 'CONNECT')
       AND NOT has_schema_privilege('claimcore_app', 'public', 'CREATE')
       AND (SELECT string_agg(id::text || ':' || marker, ',' ORDER BY id)
            FROM public.image_recipe_probe) =
           '1:synthetic-old-row,2:synthetic-second-row'
       AND (SELECT count(*) = 2 AND count(*) FILTER
             (WHERE i.indisvalid AND i.indisready) = 2
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relnamespace = 'public'::regnamespace
              AND c.relname IN
                ('image_recipe_probe_pkey', 'image_recipe_probe_marker_idx'))
       AND (SELECT d.datcollversion IS NOT DISTINCT FROM
                   pg_database_collation_actual_version(d.oid)
            FROM pg_database d WHERE d.datname = current_database());
  ")"
  if [[ "$valid" != t ]]; then
    printf '%s\n' 'Synthetic rows, role, collation, indexes, or PostgreSQL version failed qualification.' >&2
    return 1
  fi
}

cluster_signature() {
  local container="$1"
  psql_in "$container" "
    SELECT s.system_identifier::text || '|' || d.oid::text || '|' ||
           current_setting('data_directory') || '|' ||
           coalesce(d.datcollversion, '<none>') || '|' ||
           coalesce(pg_database_collation_actual_version(d.oid), '<none>')
    FROM pg_database d CROSS JOIN pg_control_system() s
    WHERE d.datname = current_database();
  "
}

start_container "$base_container" "$base_image"
created_base=1
wait_ready "$base_container" yes
psql_in "$base_container" "
  CREATE TABLE public.image_recipe_probe
    (id integer PRIMARY KEY, marker text NOT NULL);
  CREATE INDEX image_recipe_probe_marker_idx
    ON public.image_recipe_probe (marker);
  INSERT INTO public.image_recipe_probe (id, marker)
    VALUES (1, 'synthetic-old-row'), (2, 'synthetic-second-row');
" >/dev/null
verify_state "$base_container"
before="$(cluster_signature "$base_container")"

docker stop "$base_container" >/dev/null
docker rm "$base_container" >/dev/null
created_base=0

start_container "$patched_container" "$image"
created_patched=1
wait_ready "$patched_container" no
if ! docker logs "$patched_container" 2>&1 | grep -F 'Skipping initialization' >/dev/null; then
  printf '%s\n' 'Patched image did not reuse the initialized database directory.' >&2
  exit 1
fi
verify_state "$patched_container"
after="$(cluster_signature "$patched_container")"
if [[ "$before" != "$after" ]]; then
  printf '%s\n' 'Cluster identity, PGDATA, or database collation version changed after image handoff.' >&2
  exit 1
fi

printf 'PostgreSQL same-volume upgrade passed for %s: synthetic data, role, PGDATA, collation and indexes retained.\n' "$platform"

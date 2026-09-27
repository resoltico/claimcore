#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

image="$(jq -r '.containerImage' db/postgresql-baseline.json)"
expected_version="$(jq -r '.major * 10000 + .minimumMinor' db/postgresql-baseline.json)"
run_tag="$(openssl rand -hex 12)"
container="claimcore-catalog-$run_tag"
container_id=""
password="$(openssl rand -hex 24)"
output="db/catalog-manifest.pg18.6.json"
mkdir -p artifacts
temporary="$(mktemp artifacts/catalog-manifest.XXXXXX)"

cleanup() {
    if [[ -n "$container_id" ]] &&
        [[ "$(docker inspect --format '{{index .Config.Labels "org.claimcore.catalog-manifest"}}' "$container_id" 2>/dev/null)" == "$run_tag" ]]; then
        docker stop "$container_id" >/dev/null 2>&1 || true
    fi
    rm -f "$temporary"
}
trap cleanup EXIT

container_id="$(docker run --rm -d --name "$container" \
    --label "org.claimcore.catalog-manifest=$run_tag" \
    -e POSTGRES_USER=claimcore_catalog_owner \
    -e POSTGRES_PASSWORD="$password" \
    -e POSTGRES_DB=claimcore_catalog_synthetic \
    "$image" -c fsync=on -c full_page_writes=on -c synchronous_commit=on)"

for _ in $(seq 1 40); do
    if docker exec "$container_id" pg_isready -q -U claimcore_catalog_owner \
        -d claimcore_catalog_synthetic; then
        break
    fi
    sleep 1
done

psql=(docker exec -i "$container_id" psql -X -v ON_ERROR_STOP=1 \
    -U claimcore_catalog_owner -d claimcore_catalog_synthetic)

printf 'CREATE ROLE claimcore_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;\n' \
    | "${psql[@]}" >/dev/null
"${psql[@]}" < db/baseline.sql >/dev/null

actual_version="$(printf 'SELECT current_setting('\''server_version_num'\'')::integer;\n' \
    | "${psql[@]}" -A -t)"
if [[ "$actual_version" != "$expected_version" ]]; then
    printf 'Catalog generation requires PostgreSQL %s, found %s.\n' \
        "$expected_version" "$actual_version" >&2
    exit 1
fi

{ printf "SET search_path = pg_catalog; SET DateStyle = 'ISO, YMD';\n"; \
    sed -n 'p' db/catalog-manifest.sql; } | "${psql[@]}" -q -A -t \
    | jq -s --argjson serverVersionNum "$actual_version" \
      'if length == 1 then
         {schemaVersion: 1, serverVersionNum: $serverVersionNum, catalog: .[0]}
       else error("Catalog projection must contain exactly one JSON value") end' \
      > "$temporary"
mv "$temporary" "$output"
printf 'Generated %s from isolated PostgreSQL %s. Review the diff before committing.\n' \
    "$output" "$actual_version"

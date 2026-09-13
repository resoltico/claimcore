#!/usr/bin/env bash
set -euo pipefail

# Validate one locally built architecture image. Build the image from an empty
# context first; this script never starts Compose or adopts an existing volume.
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
grep -Fqx "FROM $base_image" "$repo_root/db/Dockerfile.postgres-patched"

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-pg-image.XXXXXXXX")"
nonce="${tmp_dir##*.}"
container="claimcore-pg-recipe-$nonce"
volume="claimcore-pg-recipe-$nonce"
created_container=0
created_volume=0

cleanup() {
  if [[ "$created_container" -eq 1 ]]; then
    docker rm --force "$container" >/dev/null 2>&1 || true
  fi
  if [[ "$created_volume" -eq 1 ]]; then
    if [[ "$(docker volume inspect --format '{{ index .Labels "claimcore.postgres-image-recipe" }}' "$volume" 2>/dev/null)" == "$nonce" ]]; then
      docker volume rm "$volume" >/dev/null 2>&1 || true
    else
      printf '%s\n' 'Refusing to remove a volume whose isolation label changed.' >&2
    fi
  fi
  rm -rf -- "$tmp_dir"
}
trap cleanup EXIT

image_arch="$(docker image inspect --format '{{.Architecture}}' "$image")"
if [[ "$image_arch" != "${platform#linux/}" ]]; then
  printf 'Image architecture %s does not match %s.\n' "$image_arch" "$platform" >&2
  exit 1
fi

docker run --rm --platform "$platform" --entrypoint dpkg-query "$base_image" \
  -W '-f=${Package}\t${Version}\n' | LC_ALL=C sort > "$tmp_dir/base-packages"
docker run --rm --platform "$platform" --entrypoint dpkg-query "$image" \
  -W '-f=${Package}\t${Version}\n' | LC_ALL=C sort > "$tmp_dir/patched-packages"

awk -F '\t' '
  BEGIN {
    version["gzip"] = "1.13-1+deb13u1"
    version["libpcre2-8-0"] = "10.46-1~deb13u2"
    version["libperl5.40"] = "5.40.1-6+deb13u1"
    version["perl"] = "5.40.1-6+deb13u1"
    version["perl-base"] = "5.40.1-6+deb13u1"
    version["perl-modules-5.40"] = "5.40.1-6+deb13u1"
    version["libsqlite3-0"] = "3.46.1-7+deb13u2"
  }
  {
    if ($1 in version) {
      $2 = version[$1]
      seen[$1]++
    }
    print $1 "\t" $2
  }
  END {
    for (package in version)
      if (seen[package] != 1) {
        print "Missing or duplicated base package: " package > "/dev/stderr"
        exit 1
      }
  }
' "$tmp_dir/base-packages" > "$tmp_dir/expected-packages"

if ! cmp -s "$tmp_dir/expected-packages" "$tmp_dir/patched-packages"; then
  printf '%s\n' 'Patched image changed packages beyond the seven pinned Debian upgrades.' >&2
  diff -u "$tmp_dir/expected-packages" "$tmp_dir/patched-packages" >&2 || true
  exit 1
fi

postgres_version="$(docker run --rm --platform "$platform" --entrypoint postgres "$image" --version)"
if [[ "$postgres_version" != 'postgres (PostgreSQL) 18.6' &&
      "$postgres_version" != 'postgres (PostgreSQL) 18.6 '* ]]; then
  printf 'Unexpected PostgreSQL executable version: %s\n' "$postgres_version" >&2
  exit 1
fi

for source in debian.sources pgdg.list; do
  docker run --rm --platform "$platform" --entrypoint cat "$base_image" \
    "/etc/apt/sources.list.d/$source" > "$tmp_dir/base-$source"
  docker run --rm --platform "$platform" --entrypoint cat "$image" \
    "/etc/apt/sources.list.d/$source" > "$tmp_dir/patched-$source"
  if ! cmp -s "$tmp_dir/base-$source" "$tmp_dir/patched-$source"; then
    printf 'Patched image did not restore the official %s bytes.\n' "$source" >&2
    exit 1
  fi
done

docker run --rm --platform "$platform" --entrypoint sh "$image" -ec '
  test ! -e /etc/apt/sources.list.d/pgdg.list.disabled
  test ! -e /tmp/claimcore-debian.sources.original
  test -f /usr/share/keyrings/debian-archive-keyring.pgp
'

if docker volume inspect "$volume" >/dev/null 2>&1; then
  printf '%s\n' 'Refusing to adopt an existing volume with the disposable name.' >&2
  exit 1
fi
docker volume create --label "claimcore.postgres-image-recipe=$nonce" "$volume" >/dev/null
if [[ "$(docker volume inspect --format '{{ index .Labels "claimcore.postgres-image-recipe" }}' "$volume")" != "$nonce" ]]; then
  printf '%s\n' 'A pre-existing volume has the requested disposable name.' >&2
  exit 1
fi
created_volume=1

docker run --detach --name "$container" --platform "$platform" --network none \
  --env POSTGRES_PASSWORD=synthetic-image-recipe-only \
  --mount "type=volume,src=$volume,dst=/var/lib/postgresql" \
  "$image" >/dev/null
created_container=1

ready=0
for _ in {1..60}; do
  # initdb briefly starts and stops a temporary server. Only accept readiness
  # after the entrypoint has completed initialization and begun final startup.
  if docker logs "$container" 2>&1 | grep -F 'PostgreSQL init process complete; ready for start up.' >/dev/null &&
    docker exec --user postgres "$container" pg_isready \
    --dbname postgres --username postgres >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
if [[ "$ready" -ne 1 ]]; then
  printf '%s\n' 'Isolated PostgreSQL startup did not become ready.' >&2
  exit 1
fi

server_version="$(docker exec --user postgres "$container" psql \
  --dbname postgres --no-psqlrc --tuples-only --no-align \
  --command "SELECT split_part(current_setting('server_version'), ' ', 1)")"
if [[ "$server_version" != 18.6 ]]; then
  printf 'Unexpected running PostgreSQL version: %s\n' "$server_version" >&2
  exit 1
fi

printf 'PostgreSQL image recipe passed for %s: seven pinned package changes and isolated 18.6 startup.\n' "$platform"

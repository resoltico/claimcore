#!/usr/bin/env bash
set -euo pipefail

# Keep the official image's inherited docker-library scripts with their exact
# upstream MIT notice. This is the only permitted build-context content.
if [[ "$#" -ne 0 ]]; then
  printf 'Usage: %s\n' "$0" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
notice_dir="$repo_root/db/postgres-upstream"
dockerfile="$repo_root/db/Dockerfile.postgres-patched"
upstream_commit=e00e1bd34ec5c8a8e7ad89b273b3d42efaf6d5bc
license_sha=87ffd2c45e3f90cfa3407b5c40ef8333e87c3e875e4895f8b64df758198deafc
authors_sha=a016549e5373f5fd36b698ac4d73a894e7818b50a0c0837f18788cccd6a6da1a

shopt -s nullglob dotglob
entries=("$notice_dir"/*)
if [[ "${#entries[@]}" -ne 2 || ! -f "$notice_dir/LICENSE" || -L "$notice_dir/LICENSE" ||
      ! -f "$notice_dir/AUTHORS" || -L "$notice_dir/AUTHORS" ]]; then
  printf '%s\n' 'The PostgreSQL notice context must contain only regular LICENSE and AUTHORS files.' >&2
  exit 1
fi

for item in LICENSE AUTHORS; do
  case "$item" in
    LICENSE) expected="$license_sha" ;;
    AUTHORS) expected="$authors_sha" ;;
  esac
  actual="$(shasum -a 256 "$notice_dir/$item" | cut -d ' ' -f 1)"
  if [[ "$actual" != "$expected" ]]; then
    printf 'The pinned upstream PostgreSQL %s bytes changed.\n' "$item" >&2
    exit 1
  fi
done

if ! grep -Fqx "# docker-library/postgres@$upstream_commit." "$dockerfile" ||
   [[ "$(grep -Fxc 'COPY --chmod=0644 LICENSE AUTHORS /usr/share/doc/docker-library-postgres/' "$dockerfile")" -ne 1 ]]; then
  printf '%s\n' 'The PostgreSQL Dockerfile lost its pinned upstream attribution COPY.' >&2
  exit 1
fi

printf 'Pinned PostgreSQL upstream MIT notice passed: %s.\n' "$upstream_commit"

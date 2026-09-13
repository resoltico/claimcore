#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root"

run_pattern() {
  local root="$1"
  local extension="$2"
  if find "$root" -type f -name "*.$extension" -print -quit | grep --quiet .; then
    dotnet fsharplint lint --file-type wildcard "$root/**/*.$extension" \
      --lint-config fsharplint.json
  fi
}

run_individual() {
  local root="$1"
  local extension="$2"
  while IFS= read -r -d '' source; do
    dotnet fsharplint lint --file-type file "$source" --lint-config fsharplint.json
  done < <(find "$root" -type f -name "*.$extension" -print0)
}

for source_root in src tests eng; do
  run_pattern "$source_root" fs
  run_individual "$source_root" fsi
  run_individual "$source_root" fsx
done

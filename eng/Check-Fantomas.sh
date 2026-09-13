#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root"

mapfile -d '' sources < <(
  find src tests eng -type f \
    \( -name '*.fs' -o -name '*.fsi' -o -name '*.fsx' \) -print0
)

if [[ "${#sources[@]}" -eq 0 ]]; then
  printf '%s\n' 'No F# sources were discovered for formatting.' >&2
  exit 1
fi

dotnet fantomas "${sources[@]}" --check

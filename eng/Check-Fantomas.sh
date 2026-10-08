#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "${repo_root}"

source_list="$(mktemp "${TMPDIR:-/tmp}/claimcore-format-sources.XXXXXXXX")"
trap 'rm -f -- "${source_list}"' EXIT
find src tests eng -type f \
  \( -name '*.fs' -o -name '*.fsi' -o -name '*.fsx' \) -print0 >"${source_list}"
sources=()
while IFS= read -r -d '' source; do
  sources+=("${source}")
done <"${source_list}"

if [[ "${#sources[@]}" -eq 0 ]]; then
  printf '%s\n' 'No F# sources were discovered for formatting.' >&2
  exit 1
fi

dotnet fantomas "${sources[@]}" --check

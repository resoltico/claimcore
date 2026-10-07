#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "${repo_root}"

run_pattern() {
  local root="$1"
  local extension="$2"
  local first_source
  first_source="$(find "${root}" -type f -name "*.${extension}" -print -quit)"
  if [[ -n "${first_source}" ]]; then
    dotnet fsharplint lint --file-type wildcard "${root}/**/*.${extension}" \
      --lint-config config/fsharplint.json
  fi
}

run_individual() {
  local root="$1"
  local extension="$2"
  local source_list
  source_list="$(mktemp "${TMPDIR:-/tmp}/claimcore-lint-sources.XXXXXXXX")"
  find "${root}" -type f -name "*.${extension}" -print0 >"${source_list}"
  while IFS= read -r -d '' source; do
    dotnet fsharplint lint --file-type file "${source}" --lint-config config/fsharplint.json
  done <"${source_list}"
  rm -f -- "${source_list}"
}

run_root() {
  local source_root="$1"
  run_pattern "${source_root}" fs
  run_individual "${source_root}" fsi
  run_individual "${source_root}" fsx
}

# Each root is read-only and uses an independent compiler-service process. Keep the bound fixed
# at the three roots and collect every exit status, including failures before the last root.
pids=()
for source_root in src tests eng; do
  run_root "${source_root}" &
  pids+=("$!")
done

status=0
for pid in "${pids[@]}"; do
  if ! wait "${pid}"; then
    status=1
  fi
done
exit "${status}"

#!/usr/bin/env bash
# Only completed directories created by this verifier participate in retention.
prune_native_reports() {
  local root directory marker count
  root="$(cd "$1" && pwd -P)"
  local completed=()
  for marker in "${root}"/native.*/.claimcore-native-complete; do
    [[ -f "${marker}" && ! -L "${marker}" ]] || continue
    directory="${marker%/*}"
    [[ ! -L "${directory}" && ! -e "${directory}/.claimcore-native-active" ]] || continue
    completed+=("${marker}")
  done
  [[ ${#completed[@]} -gt 10 ]] || return 0
  local ordered
  # Another completed run may already have pruned an old marker; keep the surviving sorted paths.
  ordered="$(ls -1t -- "${completed[@]}" 2>/dev/null)" || true
  count=0
  # Names come from mktemp's native.XXXXXXXX pattern; marker mtimes order completed runs.
  while IFS= read -r marker; do
    count=$((count + 1))
    [[ ${count} -gt 10 ]] || continue
    directory="${marker%/*}"
    if [[ "${directory%/*}" == "${root}" && "${directory##*/}" == native.* && ! -L "${directory}" && ! -e "${directory}/.claimcore-native-active" && -f "${marker}" && ! -L "${marker}" ]]; then
      rm -rf -- "${directory}"
    fi
  done <<< "${ordered}"
}

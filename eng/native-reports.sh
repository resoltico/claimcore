#!/usr/bin/env bash
# Expire only explicitly successful, completed directories created by this verifier.
# Failed, active and historical directories with no outcome marker require owned review.
native_report_can_expire() {
  local directory="$1"
  [[ -d "${directory}" && ! -L "${directory}" \
    && ! -e "${directory}/.claimcore-native-active" \
    && ! -L "${directory}/.claimcore-native-active" \
    && ! -e "${directory}/.claimcore-native-failed" \
    && ! -L "${directory}/.claimcore-native-failed" \
    && -f "${directory}/.claimcore-native-complete" \
    && ! -L "${directory}/.claimcore-native-complete" \
    && -f "${directory}/.claimcore-native-passed" \
    && ! -L "${directory}/.claimcore-native-passed" ]]
}

prune_native_reports() {
  local root directory marker count
  root="$(cd "$1" && pwd -P)"
  local completed=()
  for marker in "${root}"/native.*/.claimcore-native-complete; do
    directory="${marker%/*}"
    native_report_can_expire "${directory}" || continue
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
    if [[ "${directory%/*}" == "${root}" && "${directory##*/}" == native.* ]] && native_report_can_expire "${directory}"; then
      rm -rf -- "${directory}"
    fi
  done <<< "${ordered}"
}

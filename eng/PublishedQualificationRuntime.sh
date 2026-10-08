#!/usr/bin/env bash
# Source from published qualification entry points after defining their physical repo_root.
repo_root="${repo_root:?Qualification requires its physical repository root.}"
enter_qualification_run() {
  if [[ -n "${DOTNET_ROOT:-}" ]]; then
    [[ -x "${DOTNET_ROOT}/dotnet" ]] || {
      printf 'The explicitly selected .NET root has no executable.\n' >&2
      exit 64
    }
    export PATH="${DOTNET_ROOT}:${PATH}"
  fi
  if [[ -n "${CLAIMCORE_RUN_CONTEXT:-}" || -n "${CLAIMCORE_JOB_CONTEXT:-}" ]]; then
    node "${repo_root}/eng/ci/run-command.mjs" check
  else
    exec node "${repo_root}/eng/ci/run-command.mjs" bash "eng/$(basename "$0")" "$@"
  fi
}

select_qualification_runtime() {
  expected_node="$(tr -d '\r\n' <"${repo_root}/.node-version")"
  expected_npm="$(jq -r '.engines.npm' "${repo_root}/web/package.json")"
  runtime_prefix=()
  if [[ "$(node --version 2>/dev/null || true)" != "v${expected_node}" ]] ||
    [[ "$(npm --version 2>/dev/null || true)" != "${expected_npm}" ]]; then
    command -v mise >/dev/null || {
      printf 'Locked Node/npm is unavailable.\n' >&2
      exit 64
    }
    runtime_prefix=(mise exec "node@${expected_node}" --)
  fi
  selected_node="$("${runtime_prefix[@]}" node --version)"
  selected_npm="$("${runtime_prefix[@]}" npm --version)"
  if [[ "${selected_node}" != "v${expected_node}" ]] ||
    [[ "${selected_npm}" != "${expected_npm}" ]]; then
    printf 'Locked Node/npm could not be selected.\n' >&2
    exit 64
  fi
}

# Consumers invoke npm through the same validated runtime selection owned here.
qualification_npm() {
  "${runtime_prefix[@]}" npm "$@"
}

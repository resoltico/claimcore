#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 1 && -f "${CLAIMCORE_CLI_TEST_DRIVER:-}" ]] || exit 64
if [[ -n "${CLAIMCORE_CLI_TEST_PROGRESS_FILE:-}" ]]; then
  printf '%s\n' wrapper-started >"${CLAIMCORE_CLI_TEST_PROGRESS_FILE}"
fi
exec node "${CLAIMCORE_CLI_TEST_DRIVER}" "$1"

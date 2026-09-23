#!/usr/bin/env bash
set -euo pipefail

database_dll="${1:?Pass the published Database assembly.}"
owner_connection="${2:?Pass the private schema-owner connection file.}"
private_state="${3:?Pass the isolated private state directory.}"

diagnostic_from() {
  jq -r '.diagnostic.id // empty' "$1" 2>/dev/null || true
}

report_failure() {
  local phase="$1" diagnostic
  diagnostic="$(diagnostic_from "$2")"
  if [[ "$diagnostic" =~ ^DB_[A-Z_]+$ ]]; then
    printf 'The published database %s failed: %s.\n' "$phase" "$diagnostic" >&2
  else
    printf 'The published database %s failed.\n' "$phase" >&2
  fi
}

# Probe only with read-only verify; an unconfirmed initializer must never be retried.
owner_ready=""
for _ in $(seq 1 15); do
  if CLAIMCORE_ADMIN_CONNECTION_FILE="$owner_connection" \
    dotnet "$database_dll" verify >"$private_state/database-probe.out" 2>"$private_state/database-probe.err"; then
    echo "The isolated database unexpectedly has an installed baseline." >&2
    exit 1
  fi
  diagnostic="$(diagnostic_from "$private_state/database-probe.err")"
  if [[ "$diagnostic" == DB_BASELINE_MISSING ]]; then
    owner_ready="yes"
    break
  fi
  if [[ "$diagnostic" != DB_DATABASE_UNAVAILABLE ]]; then
    report_failure readiness "$private_state/database-probe.err"
    exit 1
  fi
  sleep 1
done
if [[ "$owner_ready" != yes ]]; then
  report_failure readiness "$private_state/database-probe.err"
  exit 1
fi

if ! CLAIMCORE_ADMIN_CONNECTION_FILE="$owner_connection" \
  dotnet "$database_dll" initialize Etc/UTC >"$private_state/database-init.out" 2>"$private_state/database-init.err"; then
  report_failure initialization "$private_state/database-init.err"
  exit 1
fi
if ! CLAIMCORE_ADMIN_CONNECTION_FILE="$owner_connection" \
  dotnet "$database_dll" verify >"$private_state/database-verify.out" 2>"$private_state/database-verify.err"; then
  report_failure verification "$private_state/database-verify.err"
  exit 1
fi

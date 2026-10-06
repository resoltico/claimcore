#!/usr/bin/env bash
set -euo pipefail
umask 077

export CLAIMCORE_ADMIN_CONNECTION_FILE=/etc/claimcore/administration/primary-owner.connection
export CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE=/etc/claimcore/administration/witness-owner.connection
export CLAIMCORE_WITNESS_CONNECTION_FILE=/etc/claimcore/web/witness.connection
export CLAIMCORE_WITNESS_KEY_FILE=/etc/claimcore/web/witness-key.json
export CLAIMCORE_WRITER_CAPABILITY_FILE=/etc/claimcore/web/writer.capability
export CLAIMCORE_SUPPRESSION_KEY_FILE=/etc/claimcore/web/suppression-key.json
export CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE=/etc/claimcore/administration/initial-owner.json

report=/tmp/claimcore-initialization
mkdir -m 700 "${report}"
invoke() {
  if ! dotnet /app/database/ClaimCore.Database.dll "$@" >"${report}/out" 2>"${report}/error"; then
    printf 'Owner initialization stopped; no reset or uncertain write retry was attempted.\n' >&2
    exit 1
  fi
}

if dotnet /app/database/ClaimCore.Database.dll verify >"${report}/verify.out" 2>"${report}/verify.error"; then
  printf 'Existing primary was refused; initialization is a one-time operation.\n' >&2
  exit 1
fi
if ! grep -q '"DB_BASELINE_MISSING"' "${report}/verify.error"; then
  printf 'Existing or unavailable primary was refused untouched.\n' >&2
  exit 1
fi
zone="$(cat /etc/claimcore/administration/business-zone)"
invoke initialize "${zone}"
invoke initialize-witness
invoke provision-initial-owner
printf 'Persistent synthetic primary/witness installation and initial owner are ready.\n'

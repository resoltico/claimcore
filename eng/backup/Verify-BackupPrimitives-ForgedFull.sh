#!/usr/bin/env bash
set -euo pipefail

# Negative control: a caller-selected synthetic callback cannot certify a full restore.
source_dir="$(cd "$(dirname "$0")" && pwd -P)"
bash "$source_dir/Verify-BackupPrimitives.sh" "$@"
report="${5:?test report path}"
replacement="${report}.forged"
jq -cS '.scope="full" | .source="ClaimCore.Database" |
  .catalogVerified=true | .dataAuditVerified=true | .walTimelineVerified=true |
  .authorityReconciled=true | .managedCopiesRegistered=true |
  .newerFencesApplied=true | .pendingIntents=0' "$report" > "$replacement"
chmod 600 "$replacement"
mv "$replacement" "$report"

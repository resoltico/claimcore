#!/usr/bin/env bash
set -euo pipefail

# Official report verification is the fixed published Database binary. Its immutable reviewed
# publication root, exact report signature, restored-pair recheck, and scope policy fail closed.
publish_dir="${CLAIMCORE_DATABASE_PUBLISH_DIR:?Published Database directory is required}"
[[ "$publish_dir" == /* && -d "$publish_dir" && ! -L "$publish_dir" ]]
[[ $# == 4 ]]
[[ -f "$publish_dir/ClaimCore.Database.dll" ]]
exec dotnet "$publish_dir/ClaimCore.Database.dll" verify-restore-report "$1" "$2" "$3" "$4"

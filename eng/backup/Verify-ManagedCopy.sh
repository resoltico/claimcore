#!/usr/bin/env bash
set -euo pipefail

# Fixed packaged per-copy physical verifier; one owner-private input file, no callback path.
[[ $# == 1 ]]
source_dir="$(cd "$(dirname "$0")" && pwd -P)"
export PATH="/opt/homebrew/opt/libpq/bin:/usr/lib/postgresql/18/bin:${PATH}"
exec python3 -E -s -B "${source_dir}/verify_managed_copy.py" "$1"

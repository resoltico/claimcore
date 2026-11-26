#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:-${root}/web/artifacts/catalogs}"
project="${root}/eng/ClaimCore.ContractGenerator"
dotnet restore "${project}" --locked-mode
dotnet run --project "${project}" --configuration Release --no-restore -- --prepare-browser-catalogs "${output}"

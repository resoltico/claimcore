#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root"

workspace="$(mktemp -d "$repo_root/artifacts/acceptance-local.XXXXXX")"
workspace="$(realpath "$workspace")"
run_id="$(basename "$workspace")"
attempt=1
test_run_label="claimcore-acceptance-$run_id"
export CLAIMCORE_TEST_RUN_LABEL="$test_run_label"
cli_dir="$workspace/cli"
database_dir="$workspace/database"
results="$workspace/results"
docs="$repo_root/artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll"

cleanup_test_containers() {
  local status=$?
  trap - EXIT
  if ! bash "$repo_root/eng/Remove-LabeledTestContainers.sh" "$test_run_label"; then
    echo "Exact-label published CLI container cleanup failed." >&2
    status=1
  fi
  exit "$status"
}
trap cleanup_test_containers EXIT

dotnet restore ClaimCore.slnx --locked-mode
dotnet tool restore
dotnet build ClaimCore.slnx --configuration Release --no-restore
mkdir "$cli_dir" "$database_dir"

publish_stage() {
  local stage="$1"
  local project="$2"
  local output="$3"
  local product="$4"
  local started
  local finished
  local version
  version="$(dotnet msbuild "$project" -nologo -verbosity:quiet \
    -property:Configuration=Release -getProperty:Version)"
  started="$(date -u +%Y-%m-%dT%H:%M:%S.0000000+00:00)"
  dotnet publish "$project" --configuration Release --no-build --no-restore \
    --output "$output" -p:UseAppHost=false
  dotnet tool run dotnet-CycloneDX -- "$project" --exclude-dev --disable-package-restore \
    --configuration Release --output "$output" --filename "$product.cdx.json" \
    --output-format Json --no-serial-number --set-name "$product" --set-version "$version" \
    --include-project-references
  pwsh -NoProfile -File eng/Write-DotNetNotices.ps1 \
    -SbomPath "$output/$product.cdx.json" -OutputPath "$output/THIRD-PARTY-NOTICES.txt"
  finished="$(date -u +%Y-%m-%dT%H:%M:%S.0000000+00:00)"
  dotnet "$docs" stage-manifest "$stage" "$run_id" "$attempt" success \
    "$started" "$finished" "$output"
}

publish_stage publish-cli src/ClaimCore.Cli/ClaimCore.Cli.fsproj "$cli_dir" ClaimCore.Cli
publish_stage publish-database src/ClaimCore.Database/ClaimCore.Database.fsproj "$database_dir" ClaimCore.Database

cli_manifest_relative="artifacts/evidence/$run_id/$attempt/publish/publish-cli.json"
database_manifest_relative="artifacts/evidence/$run_id/$attempt/publish/publish-database.json"
cli_manifest="$repo_root/$cli_manifest_relative"
database_manifest="$repo_root/$database_manifest_relative"
dotnet "$docs" verify-publish-manifest publish-cli "$cli_dir" "$cli_manifest_relative"
dotnet "$docs" verify-publish-manifest publish-database "$database_dir" "$database_manifest_relative"

CLAIMCORE_ACCEPTANCE_CLI_DIR="$cli_dir" \
CLAIMCORE_ACCEPTANCE_DATABASE_DIR="$database_dir" \
CLAIMCORE_ACCEPTANCE_CLI_MANIFEST="$cli_manifest" \
CLAIMCORE_ACCEPTANCE_DATABASE_MANIFEST="$database_manifest" \
  dotnet test --project tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj \
    --configuration Release --no-build --no-restore --max-parallel-test-modules 1 \
    --results-directory="$results" --minimum-expected-tests=9 \
    --zero-tests-policy=strict --timeout=30m -- \
    --settings="$repo_root/eng/expecto.runsettings" --report-trx \
    --report-trx-filename=ClaimCore.AcceptanceTests.trx

printf 'Published CLI-v3 call/session acceptance passed; evidence is under %s.\n' "$workspace"

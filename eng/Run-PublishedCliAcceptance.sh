#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "${repo_root}"

workspace="$(mktemp -d "${repo_root}/artifacts/acceptance-local.XXXXXX")"
workspace="$(realpath "${workspace}")"
test_run_label="claimcore-acceptance-$(basename "${workspace}")"
export CLAIMCORE_TEST_RUN_LABEL="${test_run_label}"
publish_root="${workspace}/publish"
cli_dir="${publish_root}/cli"
database_dir="${publish_root}/database"
web_dir="${publish_root}/web"
results="${workspace}/results"

cleanup_test_containers() {
  local status=$?
  trap - EXIT
  if ! bash "${repo_root}/eng/Remove-LabeledTestContainers.sh" "${test_run_label}"; then
    echo "Exact-label published CLI container cleanup failed." >&2
    status=1
  fi
  exit "${status}"
}
trap cleanup_test_containers EXIT

dotnet restore ClaimCore.slnx --locked-mode
dotnet tool restore
for project in \
  src/ClaimCore.Cli/ClaimCore.Cli.fsproj \
  src/ClaimCore.Web/ClaimCore.Web.fsproj \
  src/ClaimCore.Database/ClaimCore.Database.fsproj \
  tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj; do
  dotnet build "${project}" --configuration Release --no-restore
done
npm --prefix web ci
npm --prefix web run contract:generate
npm --prefix web run build
npm --prefix web run sbom
npm --prefix web exec -- playwright install chromium
node eng/ci/publish/main.mjs build --output "${publish_root}" --no-build

CLAIMCORE_PUBLISHED_CLI_DIR="${cli_dir}" \
  CLAIMCORE_ACCEPTANCE_RESULTS_DIR="${results}" \
  bash eng/Run-PublishedWebE2E.sh "${web_dir}" "${database_dir}" chromium
node eng/ci/publish/main.mjs verify "${publish_root}"

printf 'Published authenticated CLI-v4 call/session acceptance passed; evidence is under %s.\n' "${workspace}"

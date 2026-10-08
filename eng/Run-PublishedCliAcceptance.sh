#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
# shellcheck source=eng/PublishedQualificationRuntime.sh
source "${repo_root}/eng/PublishedQualificationRuntime.sh"
enter_qualification_run "$@"
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
  if [[ "${status}" == 0 ]]; then
    node "${repo_root}/eng/ci/run-publication.mjs" dispose "${workspace}" || status=1
  fi
  exit "${status}"
}
trap cleanup_test_containers EXIT

dotnet tool restore
node eng/ci/run-publication.mjs build-inputs
npm --prefix web ci
npm --prefix web run contract:generate
npm --prefix web run build
npm --prefix web run sbom
npm --prefix web exec -- playwright install chromium
node eng/ci/publish/main.mjs build --output "${publish_root}" --no-build

CLAIMCORE_PUBLISHED_CLI_DIR="${cli_dir}" \
  CLAIMCORE_ACCEPTANCE_RESULTS_DIR="${results}" \
  dotnet tool run coverlet -- "${cli_dir}" --target bash \
  --targetargs "eng/Run-PublishedWebE2E.sh ${web_dir} ${database_dir} chromium" \
  --include '[ClaimCore.Cli]*' --exclude-assemblies-without-sources None --format cobertura \
  --output "${results}/cli.coverage.cobertura.acceptance.xml" --verbosity minimal
node eng/ci/coverage-policy/cli.mjs "${results}/cli.coverage.cobertura.acceptance.xml"
node eng/ci/publish/main.mjs verify "${publish_root}"

printf 'Published authenticated CLI-v4 call/session acceptance passed; evidence is under %s.\n' "${workspace}"

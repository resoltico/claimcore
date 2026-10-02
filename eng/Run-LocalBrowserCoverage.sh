#!/usr/bin/env bash
set -euo pipefail

# Runs the three published-browser lifecycles with coverage and merges them with the .NET suites'
# coverage from an earlier `node eng/ci/suites/suite.mjs run unit web --group postgres` run.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "${repo_root}"
dotnet_results="${repo_root}/artifacts/test-results"
if [[ ! -d "${dotnet_results}" || -L "${dotnet_results}" ]]; then
  printf 'Run the unit, web and postgres suites first; artifacts/test-results is absent.\n' >&2
  exit 64
fi

expected_node="$(tr -d '\r\n' <.node-version)"
expected_npm="$(jq -r '.engines.npm' web/package.json)"
if [[ "$(node --version 2>/dev/null || true)" != "v${expected_node}" ||
"$(npm --version 2>/dev/null || true)" != "${expected_npm}" ]]; then
  printf 'Select the pinned Node and npm toolchain before the local browser gate.\n' >&2
  exit 64
fi

workspace="$(mktemp -d "${repo_root}/artifacts/local-browser.XXXXXXXX")"
publish_root="${workspace}/publish"
web_dir="${publish_root}/web"
database_dir="${publish_root}/database"
local_coverage="${workspace}/coverage-input"
browser_input="${local_coverage}/browser"
mkdir -p "${local_coverage}/dotnet" "${browser_input}"
find "${dotnet_results}" -name '*.coverage.cobertura.*.xml' -exec cp {} "${local_coverage}/dotnet/" \;

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
npm --prefix web run test:unit
npm --prefix web run test:mutation
npm --prefix web run build
npm --prefix web run sbom
npm --prefix web exec -- playwright install chromium firefox webkit
bash eng/Test-HostSecurityNativePublishItems.sh
node eng/ci/publish/main.mjs build --output "${publish_root}" --no-build

CLAIMCORE_PUBLISHED_CLI_DIR="${publish_root}/cli" \
  CLAIMCORE_ACCEPTANCE_RESULTS_DIR="${local_coverage}/acceptance" \
  dotnet tool run coverlet -- "${publish_root}/cli" --target bash \
  --targetargs "eng/Run-PublishedWebE2E.sh ${web_dir} ${database_dir} chromium" \
  --include '[ClaimCore.Cli]*' --exclude-assemblies-without-sources None \
  --format cobertura --output "${local_coverage}/acceptance/cli.coverage.cobertura.acceptance.xml" \
  --verbosity minimal
node eng/ci/coverage-policy/cli.mjs "${local_coverage}/acceptance/cli.coverage.cobertura.acceptance.xml"
node eng/ci/publish/main.mjs verify "${publish_root}"

for engine in chromium firefox webkit; do
  dotnet tool run coverlet -- "${web_dir}" --target bash \
    --targetargs "eng/Run-PublishedWebE2E.sh ${web_dir} ${database_dir} ${engine}" \
    --include '[ClaimCore.Web]*' --exclude-assemblies-without-sources None \
    --format cobertura --output "${browser_input}/${engine}.coverage.cobertura.e2e.xml" \
    --verbosity minimal
  node eng/ci/publish/main.mjs verify "${publish_root}" web database
done

node eng/ci/suites/frontend.mjs all
node eng/ci/coverage-policy/merge.mjs "${local_coverage}" "${workspace}/merged-coverage"
printf 'Local published browser and merged coverage qualification passed.\n'
printf 'Synthetic results are under %s.\n' "${workspace}"

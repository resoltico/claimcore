#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "${repo_root}"
if [[ "$#" -ne 1 ]]; then
  printf 'Usage: %s <completed-local-dotnet-verification-directory>\n' "$0" >&2
  exit 64
fi
run_root="$(realpath "$1")"
case "${run_root}" in
  "${repo_root}"/artifacts/local-verification/*) ;;
  *)
    printf 'The local verification directory must be under artifacts/.\n' >&2
    exit 64
    ;;
esac
coverage_root="${run_root}/coverage-input"
fingerprint_file="${run_root}/complete-source-fingerprint.txt"
docs="${repo_root}/artifacts/bin/ClaimCore.Docs/release/ClaimCore.Docs.dll"
if [[ ! -d "${coverage_root}" || -L "${coverage_root}" || ! -f "${fingerprint_file}" ||
  -L "${fingerprint_file}" || ! -f "${docs}" ]]; then
  printf 'A complete local .NET run and its reviewed evidence tool are required.\n' >&2
  exit 64
fi
source_fingerprint="$(tr -d '\r\n' <"${fingerprint_file}")"
if [[ ! "${source_fingerprint}" =~ ^[0-9a-f]{64}:[0-9a-f]{64}$ ]] ||
  [[ "$(dotnet "${docs}" source-fingerprint)" != "${source_fingerprint}" ]]; then
  printf 'The local .NET run does not match the current source.\n' >&2
  exit 64
fi
for role in unit web integration; do
  if [[ ! -d "${coverage_root}/${role}" || -L "${coverage_root}/${role}" ]]; then
    printf 'The %s .NET coverage directory is absent or linked.\n' "${role}" >&2
    exit 64
  fi
  reports=("${coverage_root}/${role}/${role}.coverage.cobertura."*.xml)
  if [[ "${#reports[@]}" -ne 1 || ! -f "${reports[0]}" || -L "${reports[0]}" ]]; then
    printf 'The %s .NET coverage input must contain one measured report.\n' "${role}" >&2
    exit 64
  fi
done
workspace="$(mktemp -d "${repo_root}/artifacts/local-browser.XXXXXXXX")"
run_id="$(basename "${workspace}")"
web_dir="${workspace}/web"
database_dir="${workspace}/database"
local_coverage="${workspace}/coverage-input"
browser_input="${local_coverage}/browser"
mkdir "${web_dir}" "${database_dir}"
mkdir -p "${browser_input}"
for role in unit web integration; do
  mkdir "${local_coverage}/${role}"
  reports=("${coverage_root}/${role}/${role}.coverage.cobertura."*.xml)
  cp "${reports[0]}" "${local_coverage}/${role}/"
done

dotnet restore ClaimCore.slnx --locked-mode
dotnet tool restore
dotnet build eng/ClaimCore.Docs/ClaimCore.Docs.fsproj --configuration Release --no-restore
dotnet build src/ClaimCore.Web/ClaimCore.Web.fsproj --configuration Release --no-restore
dotnet build src/ClaimCore.Database/ClaimCore.Database.fsproj --configuration Release --no-restore
expected_node="$(tr -d '\r\n' <.node-version)"
expected_npm="$(jq -r '.engines.npm' web/package.json)"
if [[ "$(node --version 2>/dev/null || true)" != "v${expected_node}" ||
"$(npm --version 2>/dev/null || true)" != "${expected_npm}" ]]; then
  printf 'Select the pinned Node and npm toolchain before the local browser gate.\n' >&2
  exit 64
fi
npm --prefix web ci
npm --prefix web run test:unit
npm --prefix web run test:mutation
npm --prefix web run build
npm --prefix web run sbom
npm --prefix web exec -- playwright install chromium firefox webkit
bash eng/Test-HostSecurityNativePublishItems.sh

publish_stage() {
  local stage="$1" project="$2" output="$3" product="$4"
  local started finished version
  version="$(dotnet msbuild "${project}" -nologo -verbosity:quiet \
    -property:Configuration=Release -getProperty:Version)"
  started="$(date -u +%Y-%m-%dT%H:%M:%S.0000000+00:00)"
  dotnet publish "${project}" --configuration Release --no-build --no-restore \
    --output "${output}" -p:UseAppHost=false
  dotnet tool run dotnet-CycloneDX -- "${project}" --exclude-dev --disable-package-restore \
    --configuration Release --output "${output}" --filename "${product}.cdx.json" \
    --output-format Json --no-serial-number --set-name "${product}" --set-version "${version}" \
    --include-project-references
  pwsh -NoProfile -File eng/Write-DotNetNotices.ps1 \
    -SbomPath "${output}/${product}.cdx.json" -OutputPath "${output}/THIRD-PARTY-NOTICES.txt"
  if [[ "${product}" == ClaimCore.Web ]]; then
    cp artifacts/sbom/claimcore-web.cdx.json "${output}/ClaimCore.Web.frontend.cdx.json"
  fi
  finished="$(date -u +%Y-%m-%dT%H:%M:%S.0000000+00:00)"
  dotnet "${docs}" stage-manifest "${stage}" "${run_id}" 1 success "${started}" "${finished}" "${output}"
}

publish_stage publish-database src/ClaimCore.Database/ClaimCore.Database.fsproj "${database_dir}" ClaimCore.Database
publish_stage publish-web src/ClaimCore.Web/ClaimCore.Web.fsproj "${web_dir}" ClaimCore.Web
database_manifest="artifacts/evidence/${run_id}/1/publish/publish-database.json"
web_manifest="artifacts/evidence/${run_id}/1/publish/publish-web.json"
dotnet "${docs}" verify-publish-manifest publish-database "${database_dir}" "${database_manifest}"
dotnet "${docs}" verify-publish-manifest publish-web "${web_dir}" "${web_manifest}"

for engine in chromium firefox webkit; do
  dotnet tool run coverlet -- "${web_dir}" --target bash \
    --targetargs "eng/Run-PublishedWebE2E.sh ${web_dir} ${database_dir} ${engine}" \
    --include '[ClaimCore.Web]*' --exclude-assemblies-without-sources None \
    --format cobertura --output "${browser_input}/${engine}.coverage.cobertura.e2e.xml" \
    --verbosity minimal
  CLAIMCORE_COVERAGE_REPORT="${browser_input}/${engine}.coverage.cobertura.e2e.xml" \
    pwsh -NoProfile -Command "Import-Module ./eng/CoverageInputPolicy.psm1; Assert-ClaimCoreBrowserCoverage \$env:CLAIMCORE_COVERAGE_REPORT"
  dotnet "${docs}" verify-publish-manifest publish-web "${web_dir}" "${web_manifest}"
done

dotnet "${docs}" verify-frontend-report all
pwsh -NoProfile -File eng/Invoke-MergedCoverage.ps1 \
  -InputRoot "${local_coverage}" -OutputRoot "${workspace}/merged-coverage"
if [[ "$(dotnet "${docs}" source-fingerprint)" != "${source_fingerprint}" ]]; then
  printf 'Source changed during the local browser and coverage run.\n' >&2
  exit 1
fi
printf 'Local published browser and six-input coverage qualification passed.\n'
printf 'Synthetic results are under %s.\n' "${workspace}"

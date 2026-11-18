#!/usr/bin/env bash
set -euo pipefail
export COPYFILE_DISABLE=1

output="${1:?Usage: bash eng/build-release-artifacts.sh OUTPUT_DIRECTORY IMAGE_TAG}"
image="${2:?Set a local tag for the built server image.}"
mkdir -p "${output}"
output="$(cd "${output}" && pwd)"
cd "$(dirname "${BASH_SOURCE[0]}")/.."

staging="$(mktemp -d)"
trap 'rm -rf "${staging}"' EXIT
context="${staging}/image"
mkdir -p "${context}/deployment"
cp deployment/Dockerfile "${context}/deployment/"
bash eng/source-archive.sh "${context}/SOURCE.tar.gz"

project=src/ClaimCore.Cli/ClaimCore.Cli.fsproj
runtime_identifiers="$(dotnet msbuild "${project}" -getProperty:RuntimeIdentifiers)"
IFS=';' read -r -a runtimes <<< "${runtime_identifiers}"
test "${#runtimes[@]}" -gt 0
archives=(SOURCE.tar.gz)
for runtime in "${runtimes[@]}"; do
  archive="claimcore-cli-${runtime}.tar.gz"
  published="${staging}/${runtime}"
  dotnet publish "${project}" --configuration Release --self-contained false \
    -p:ClaimCoreCliReleaseRid="${runtime}" -p:RestoreLockedMode=true --output "${published}"
  test -x "${published}/claimcore"
  tar --no-xattrs -czf "${output}/${archive}" -C "${published}" .
  archives+=("${archive}")
done

docker build --tag "${image}" \
  --file "${context}/deployment/Dockerfile" "${context}"
mv "${context}/SOURCE.tar.gz" "${output}/SOURCE.tar.gz"
(cd "${output}" && shasum -a 256 "${archives[@]}" > SHA256SUMS)

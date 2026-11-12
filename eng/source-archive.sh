#!/usr/bin/env bash
set -euo pipefail
# macOS metadata forks are not source and can masquerade as extra project files.
export COPYFILE_DISABLE=1
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:?Usage: bash eng/source-archive.sh OUTPUT.tar.gz}"
output_directory="$(dirname "${output}")"
output_name="$(basename "${output}")"
mkdir -p "${output_directory}"
output_directory="$(cd "${output_directory}" && pwd)"
output="${output_directory}/${output_name}"
cd "${root}"
staging="$(mktemp -d)"
trap 'rm -rf "${staging}"' EXIT
# Index membership selects source; working-tree bytes include unpublished modifications.
if [[ -e .git ]]; then
  git ls-files -z > "${staging}/SOURCE-FILES"
elif [[ -f SOURCE-FILES ]]; then
  cp SOURCE-FILES "${staging}/SOURCE-FILES"
else
  echo 'Source publication requires a Git checkout or the SOURCE-FILES inventory from a source archive.' >&2
  exit 1
fi
# Untracked files are excluded. Stage new source files before publishing a checkout.
tar -cf "${staging}/source.tar" --null -T "${staging}/SOURCE-FILES"
tar -rf "${staging}/source.tar" -C "${staging}" SOURCE-FILES
gzip -c "${staging}/source.tar" > "${output}"

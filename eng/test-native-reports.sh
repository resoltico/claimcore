#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
source eng/native-reports.sh
fixture="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-reports.XXXXXXXX")"
trap 'rm -rf -- "${fixture}"' EXIT
root="${fixture}/reports"
mkdir -p "${root}" "${fixture}/outside"
for n in {01..15}; do
  mkdir "${root}/native.test${n}"
  touch -t "2020010100${n}" "${root}/native.test${n}/.claimcore-native-complete"
done
mkdir "${root}/native.unmanaged" "${root}/native.active"
touch "${root}/native.active/.claimcore-native-active" "${root}/native.active/.claimcore-native-complete"
touch "${fixture}/outside/.claimcore-native-complete"
ln -s "${fixture}/outside" "${root}/native.link"
prune_native_reports "${root}"
retained_count=0
for n in {01..15}; do
  if [[ -d "${root}/native.test${n}" ]]; then retained_count=$((retained_count + 1)); fi
  if [[ $((10#${n})) -le 5 ]]; then test ! -e "${root}/native.test${n}"; else test -d "${root}/native.test${n}"; fi
done
test "${retained_count}" -eq 10
test -d "${root}/native.unmanaged"
test -f "${root}/native.active/.claimcore-native-active"
test -f "${fixture}/outside/.claimcore-native-complete"
test -L "${root}/native.link"
prune_native_reports "${root}"
echo 'Native report retention: newest 10 completed managed runs; unmanaged, active and symlink targets preserved.'

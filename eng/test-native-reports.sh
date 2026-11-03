#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
source eng/native-reports.sh
fixture="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-reports.XXXXXXXX")"
trap 'rm -rf -- "${fixture}"' EXIT
root="${fixture}/reports"
mkdir -p "${root}" "${fixture}/outside"
prune_native_reports "${root}"
for n in {1..15}; do
  index="$(printf '%02d' "${n}")"
  mkdir "${root}/native.test${index}"
  touch -t "2020010100${index}" "${root}/native.test${index}/.claimcore-native-complete"
  touch "${root}/native.test${index}/.claimcore-native-passed"
done
mkdir "${root}/native.unmanaged" "${root}/native.active" "${root}/native.failed" \
  "${root}/native.unknown" "${root}/native.conflicting" "${root}/native.status-link" \
  "${root}/native.failed-link" "${root}/native.active-link"
touch "${root}/native.active/.claimcore-native-active" "${root}/native.active/.claimcore-native-complete" \
  "${root}/native.active/.claimcore-native-passed"
touch -t 202001010000 "${root}/native.failed/.claimcore-native-complete" \
  "${root}/native.unknown/.claimcore-native-complete" \
  "${root}/native.conflicting/.claimcore-native-complete" \
  "${root}/native.status-link/.claimcore-native-complete"
touch "${root}/native.failed/.claimcore-native-failed" \
  "${root}/native.conflicting/.claimcore-native-passed" \
  "${root}/native.conflicting/.claimcore-native-failed"
printf '%s\n' 'Retain the original failure detail.' >"${root}/native.failed/failure.txt"
touch "${fixture}/outside/.claimcore-native-complete"
touch "${fixture}/outside/.claimcore-native-passed"
ln -s "${fixture}/outside" "${root}/native.link"
ln -s "${fixture}/outside/.claimcore-native-passed" "${root}/native.status-link/.claimcore-native-passed"
for state in failed active; do
  touch -t 202001010000 "${root}/native.${state}-link/.claimcore-native-complete"
  touch "${root}/native.${state}-link/.claimcore-native-passed"
  ln -s "${fixture}/missing" "${root}/native.${state}-link/.claimcore-native-${state}"
done
prune_native_reports "${root}"
retained_count=0
for n in {1..15}; do
  index="$(printf '%02d' "${n}")"
  if [[ -d "${root}/native.test${index}" ]]; then retained_count=$((retained_count + 1)); fi
  if [[ ${n} -le 5 ]]; then test ! -e "${root}/native.test${index}"; else test -d "${root}/native.test${index}"; fi
done
test "${retained_count}" -eq 10
test -d "${root}/native.unmanaged"
test -f "${root}/native.active/.claimcore-native-active"
test -f "${root}/native.failed/failure.txt"
test -f "${root}/native.unknown/.claimcore-native-complete"
test -f "${root}/native.conflicting/.claimcore-native-failed"
test -L "${root}/native.status-link/.claimcore-native-passed"
test -L "${root}/native.failed-link/.claimcore-native-failed"
test -L "${root}/native.active-link/.claimcore-native-active"
test -f "${fixture}/outside/.claimcore-native-complete"
test -L "${root}/native.link"
prune_native_reports "${root}"
echo 'Native report retention: newest 10 explicit successes; failures, unknown/conflicting outcomes, active runs and symlinks preserved.'

#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
temporary="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-image-policy.XXXXXXXX")"
trap 'rm -rf -- "$temporary"' EXIT
mkdir "$temporary/bin" "$temporary/output"

cat > "$temporary/bin/docker" <<'MOCK_DOCKER'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1" == buildx && "$2" == imagetools && "$3" == inspect && "$4" == --raw ]]; then
  cat "$CLAIMCORE_ASSURANCE_MOCK_INDEX"
  exit 0
fi
if [[ "$1" != run ]]; then
  exit 2
fi
printf '%s\n' "$*" >> "$CLAIMCORE_ASSURANCE_MOCK_LOG"
mount=''
result=''
previous=''
for argument in "$@"; do
  if [[ "$previous" == --volume && "$argument" == *:/output ]]; then
    mount="${argument%:/output}"
  fi
  if [[ "$previous" == --output ]]; then
    result="${argument#/output/}"
  fi
  previous="$argument"
done
if [[ -n "$result" && -n "$mount" ]]; then
  if [[ -n "${CLAIMCORE_ASSURANCE_MOCK_BAD_SBOM:-}" && "$result" == *"$CLAIMCORE_ASSURANCE_MOCK_BAD_SBOM"* ]]; then
    printf '{}\n' > "$mount/$result"
  else
    printf '{"bomFormat":"CycloneDX","components":[{"name":"synthetic"}]}\n' > "$mount/$result"
  fi
fi
if [[ -n "${CLAIMCORE_ASSURANCE_MOCK_FAIL_CHILD:-}" && "$*" == *"$CLAIMCORE_ASSURANCE_MOCK_FAIL_CHILD"* ]]; then
  exit 1
fi
MOCK_DOCKER

cat > "$temporary/bin/shasum" <<'MOCK_HASH'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${CLAIMCORE_ASSURANCE_MOCK_BAD_HASH:-0}" == 1 ]]; then
  printf '%064d  %s\n' 0 "$3"
else
  digest="$(jq --raw-output '.containerImage | split("@sha256:")[1]' \
    "$CLAIMCORE_ASSURANCE_REPO/db/postgresql-baseline.json")"
  printf '%s  %s\n' "$digest" "$3"
fi
MOCK_HASH
chmod +x "$temporary/bin/docker" "$temporary/bin/shasum"

sha_a="sha256:$(printf 'a%.0s' {1..64})"
sha_b="sha256:$(printf 'b%.0s' {1..64})"
sha_c="sha256:$(printf 'c%.0s' {1..64})"
export CLAIMCORE_ASSURANCE_REPO="$repo_root"
export CLAIMCORE_ASSURANCE_MOCK_INDEX="$temporary/index.json"
export CLAIMCORE_ASSURANCE_MOCK_LOG="$temporary/docker-runs.log"
export PATH="$temporary/bin:$PATH"

jq --null-input --arg amd "$sha_a" --arg arm "$sha_b" --arg attestation "$sha_c" '
  {
    schemaVersion: 2,
    mediaType: "application/vnd.oci.image.index.v1+json",
    manifests: [
      {mediaType: "application/vnd.oci.image.manifest.v1+json", digest: $amd,
       platform: {os: "linux", architecture: "amd64"}},
      {mediaType: "application/vnd.oci.image.manifest.v1+json", digest: $arm,
       platform: {os: "linux", architecture: "arm64", variant: "v8"}},
      {mediaType: "application/vnd.oci.image.manifest.v1+json", digest: $attestation,
       platform: {os: "unknown", architecture: "unknown"},
       annotations: {"vnd.docker.reference.type": "attestation-manifest",
                     "vnd.docker.reference.digest": $amd}}
    ]
  }
' > "$temporary/valid.json"

require_failure () {
  local name="$1"
  local fixture="$2"
  : > "$CLAIMCORE_ASSURANCE_MOCK_LOG"
  cp "$fixture" "$CLAIMCORE_ASSURANCE_MOCK_INDEX"
  if bash "$repo_root/eng/Check-PostgresImageAssurance.sh" sbom "$temporary/output" \
      >/dev/null 2>&1; then
    printf 'The %s image-index negative control unexpectedly passed.\n' "$name" >&2
    exit 1
  fi
  if [[ -s "$CLAIMCORE_ASSURANCE_MOCK_LOG" ]]; then
    printf 'The %s image-index negative control reached Trivy.\n' "$name" >&2
    exit 1
  fi
}

cp "$temporary/valid.json" "$CLAIMCORE_ASSURANCE_MOCK_INDEX"
bash "$repo_root/eng/Check-PostgresImageAssurance.sh" sbom "$temporary/output" >/dev/null
test -s "$temporary/output/postgresql-linux-amd64.cdx.json"
test -s "$temporary/output/postgresql-linux-arm64.cdx.json"
test "$(wc -l < "$CLAIMCORE_ASSURANCE_MOCK_LOG" | tr -d ' ')" = 2
grep -Fq "$sha_a" "$CLAIMCORE_ASSURANCE_MOCK_LOG"
grep -Fq "$sha_b" "$CLAIMCORE_ASSURANCE_MOCK_LOG"
test "$(grep -Fc -- '--image-src remote' "$CLAIMCORE_ASSURANCE_MOCK_LOG")" = 2
if grep -Fq '/var/run/docker.sock' "$CLAIMCORE_ASSURANCE_MOCK_LOG"; then
  printf '%s\n' 'A remote digest scanner unexpectedly received the Docker socket.' >&2
  exit 1
fi

rm "$temporary/output/postgresql-linux-amd64.cdx.json" \
  "$temporary/output/postgresql-linux-arm64.cdx.json"
export CLAIMCORE_ASSURANCE_MOCK_BAD_SBOM=arm64
if bash "$repo_root/eng/Check-PostgresImageAssurance.sh" sbom "$temporary/output" >/dev/null 2>&1; then
  printf '%s\n' 'A malformed arm64 CycloneDX output did not fail the dual-architecture gate.' >&2
  exit 1
fi
unset CLAIMCORE_ASSURANCE_MOCK_BAD_SBOM

jq --arg amd "$sha_a" '.manifests += [.manifests[0]]' "$temporary/valid.json" > "$temporary/duplicate.json"
require_failure duplicate-amd64 "$temporary/duplicate.json"
jq --arg extra "sha256:$(printf 'e%.0s' {1..64})" \
  '.manifests += [{mediaType: "application/vnd.oci.image.manifest.v1+json", digest: $extra,
                  platform: {os: "linux", architecture: "ppc64le"}}]' \
  "$temporary/valid.json" > "$temporary/extra-runnable.json"
require_failure extra-runnable "$temporary/extra-runnable.json"
jq 'del(.manifests[1])' "$temporary/valid.json" > "$temporary/missing-arm64.json"
require_failure missing-arm64 "$temporary/missing-arm64.json"
jq 'del(.manifests[2].annotations)' "$temporary/valid.json" > "$temporary/unproved-unknown.json"
require_failure unproved-attestation "$temporary/unproved-unknown.json"
jq --arg other "sha256:$(printf 'd%.0s' {1..64})" \
  '.manifests[2].annotations["vnd.docker.reference.digest"] = $other' \
  "$temporary/valid.json" > "$temporary/unbound-attestation.json"
require_failure unbound-attestation "$temporary/unbound-attestation.json"
jq '.manifests[1].platform.os = "unknown"' "$temporary/valid.json" > "$temporary/partial-unknown.json"
require_failure partial-unknown "$temporary/partial-unknown.json"
jq '.mediaType = "application/vnd.oci.image.manifest.v1+json"' \
  "$temporary/valid.json" > "$temporary/not-index.json"
require_failure not-index "$temporary/not-index.json"

cp "$temporary/valid.json" "$CLAIMCORE_ASSURANCE_MOCK_INDEX"
export CLAIMCORE_ASSURANCE_MOCK_BAD_HASH=1
require_failure wrong-index-digest "$temporary/valid.json"
unset CLAIMCORE_ASSURANCE_MOCK_BAD_HASH

: > "$CLAIMCORE_ASSURANCE_MOCK_LOG"
export CLAIMCORE_ASSURANCE_MOCK_FAIL_CHILD="$sha_a"
if bash "$repo_root/eng/Check-PostgresImageAssurance.sh" scan "$temporary/output" >/dev/null 2>&1; then
  printf '%s\n' 'A failing amd64 scan did not fail the dual-architecture gate.' >&2
  exit 1
fi
unset CLAIMCORE_ASSURANCE_MOCK_FAIL_CHILD
test "$(wc -l < "$CLAIMCORE_ASSURANCE_MOCK_LOG" | tr -d ' ')" = 2
grep -Fq "$sha_a" "$CLAIMCORE_ASSURANCE_MOCK_LOG"
grep -Fq "$sha_b" "$CLAIMCORE_ASSURANCE_MOCK_LOG"
test "$(grep -Fc -- '--image-src remote' "$CLAIMCORE_ASSURANCE_MOCK_LOG")" = 2
if grep -Fq '/var/run/docker.sock' "$CLAIMCORE_ASSURANCE_MOCK_LOG"; then
  printf '%s\n' 'A remote digest scanner unexpectedly received the Docker socket.' >&2
  exit 1
fi
grep -Fq -- '--ignore-unfixed' "$CLAIMCORE_ASSURANCE_MOCK_LOG"
grep -Fq -- '--severity HIGH,CRITICAL' "$CLAIMCORE_ASSURANCE_MOCK_LOG"
grep -Fq -- '--exit-code 1' "$CLAIMCORE_ASSURANCE_MOCK_LOG"

: > "$CLAIMCORE_ASSURANCE_MOCK_LOG"
export CLAIMCORE_ASSURANCE_MOCK_FAIL_CHILD="$sha_b"
if bash "$repo_root/eng/Check-PostgresImageAssurance.sh" scan "$temporary/output" >/dev/null 2>&1; then
  printf '%s\n' 'A failing arm64 scan did not fail the dual-architecture gate.' >&2
  exit 1
fi
unset CLAIMCORE_ASSURANCE_MOCK_FAIL_CHILD
test "$(wc -l < "$CLAIMCORE_ASSURANCE_MOCK_LOG" | tr -d ' ')" = 2

printf '%s\n' 'PostgreSQL multi-architecture image-assurance policy controls passed.'

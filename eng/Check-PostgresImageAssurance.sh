#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 2 || ( "$1" != sbom && "$1" != scan ) || "$2" != /* || ! -d "$2" ]]; then
  printf 'Usage: %s <sbom|scan> <existing-absolute-stage-output-directory>\n' "$0" >&2
  exit 2
fi

mode="$1"
output_dir="$2"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
image="$(jq --exit-status --raw-output '.containerImage | select(type == "string")' \
  "$repo_root/db/postgresql-baseline.json")"

# A tag names the reviewed release; the index digest, not the mutable tag, fixes its bytes.
if [[ ! "$image" =~ ^(.+):([^/@:]+)@sha256:([0-9a-f]{64})$ ]]; then
  printf '%s\n' 'The PostgreSQL baseline must select a tagged SHA-256 image index.' >&2
  exit 1
fi
repository="${BASH_REMATCH[1]}"
index_digest="${BASH_REMATCH[3]}"

temporary="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-image-assurance.XXXXXXXX")"
trap 'rm -rf -- "$temporary"' EXIT
index_file="$temporary/index.json"
docker buildx imagetools inspect --raw "$image" > "$index_file"

actual_digest="$(shasum -a 256 "$index_file" | cut -d ' ' -f 1)"
if [[ "$actual_digest" != "$index_digest" ]]; then
  printf '%s\n' 'The fetched PostgreSQL image index did not match its pinned digest.' >&2
  exit 1
fi

# The published index has exactly the two supported runnable platforms.
# Unknown/unknown descriptors are ignored only when they are valid attestations of
# a runnable descriptor in this very index; malformed or unbound descriptors fail.
if ! jq --exit-status '
  def digest_ok:
    if type == "string" then test("^sha256:[0-9a-f]{64}$") else false end;
  def image_manifest:
    . == "application/vnd.oci.image.manifest.v1+json"
    or . == "application/vnd.docker.distribution.manifest.v2+json";
  . as $index
  | [ .manifests[]? | select(.platform.os != "unknown" or .platform.architecture != "unknown") ] as $runnable
  | [ .manifests[]? | select(.platform.os == "unknown" and .platform.architecture == "unknown") ] as $attestations
  | [ $runnable[] | select(.platform.os == "linux" and .platform.architecture == "amd64") ] as $amd64
  | [ $runnable[] | select(.platform.os == "linux" and .platform.architecture == "arm64") ] as $arm64
  | $index.schemaVersion == 2
    and ($index.mediaType == "application/vnd.oci.image.index.v1+json"
      or $index.mediaType == "application/vnd.docker.distribution.manifest.list.v2+json")
    and ($index.manifests | type == "array")
    and ($index.manifests | length > 1)
    and all($index.manifests[];
      (type == "object")
      and (.mediaType | image_manifest)
      and (.digest | digest_ok)
      and (.platform | type == "object")
      and (.platform.os | type == "string")
      and (.platform.architecture | type == "string"))
    and ([ $index.manifests[].digest ] | length == (unique | length))
    and all($runnable[];
      .platform.os != "unknown" and .platform.architecture != "unknown"
      and (.platform.os | length > 0) and (.platform.architecture | length > 0))
    and all($attestations[];
      .annotations["vnd.docker.reference.type"] == "attestation-manifest"
      and (.annotations["vnd.docker.reference.digest"] | digest_ok)
      and (.annotations["vnd.docker.reference.digest"] as $reference
        | any($runnable[]; .digest == $reference)))
    and ($runnable | length == 2)
    and ($amd64 | length == 1)
    and ($arm64 | length == 1)
    and ($amd64[0].platform.variant == null or $amd64[0].platform.variant == "")
    and ($arm64[0].platform.variant == null or $arm64[0].platform.variant == "v8")
' "$index_file" >/dev/null; then
  printf '%s\n' 'The pinned PostgreSQL index has invalid descriptors or lacks one supported image per architecture.' >&2
  exit 1
fi

trivy='aquasec/trivy:0.74.0@sha256:62b1e65e8869bc4b4c6aa4fa2b21595256c7c2f6018a9d9ad61caf87187c1969'
failed=0
for architecture in amd64 arm64; do
  child_digest="$(jq --exit-status --raw-output --arg architecture "$architecture" \
    '[.manifests[] | select(.platform.os == "linux" and .platform.architecture == $architecture)]
      | .[0].digest' "$index_file")"
  child="$repository@$child_digest"
  printf 'Checking PostgreSQL %s child of the pinned image index.\n' "linux/$architecture"

  if [[ "$mode" == sbom ]]; then
    result="postgresql-linux-$architecture.cdx.json"
    if [[ -e "$output_dir/$result" ]]; then
      printf 'Refusing to overwrite an existing %s SBOM.\n' "linux/$architecture" >&2
      exit 1
    fi
    if ! docker run --rm \
      --volume "$output_dir:/output" \
      "$trivy" image --image-src remote --format cyclonedx --output "/output/$result" \
      --no-progress "$child"; then
      failed=1
    elif [[ ! -s "$output_dir/$result" ]] || ! jq --exit-status \
      '.bomFormat == "CycloneDX" and (.components | type == "array" and length > 0)' \
      "$output_dir/$result" >/dev/null; then
      printf 'The %s child did not produce a populated CycloneDX SBOM.\n' "linux/$architecture" >&2
      failed=1
    fi
  elif ! docker run --rm \
    --volume "$repo_root/container-vulnerability-exceptions.yaml:/claimcore-exceptions.yaml:ro" \
    "$trivy" image --image-src remote --scanners vuln --severity HIGH,CRITICAL --ignore-unfixed \
    --ignorefile /claimcore-exceptions.yaml --show-suppressed --exit-code 1 \
    --no-progress "$child"; then
    failed=1
  fi
done

if [[ "$failed" -ne 0 ]]; then
  printf 'PostgreSQL %s assurance failed for at least one architecture.\n' "$mode" >&2
  exit 1
fi

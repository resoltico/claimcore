#!/usr/bin/env bash
set -euo pipefail

# Resolve a single runnable child of the exact official multiarch index without
# asking the classic Docker image store to pull or overwrite the index itself.
if [[ "$#" -ne 1 ]]; then
  printf 'Usage: %s <linux/amd64|linux/arm64>\n' "$0" >&2
  exit 2
fi

platform="$1"
case "$platform" in
  linux/amd64) arch=amd64; variant='' ;;
  linux/arm64) arch=arm64; variant=v8 ;;
  *) printf 'Unsupported PostgreSQL base platform: %s\n' "$platform" >&2; exit 2 ;;
esac

index_digest='4ef4dbc939d61acea57712655ddb4b4ab27419c913f94cca0cd57cb3ea3c2280'
index_ref="postgres:18.6@sha256:$index_digest"
index_file="$(mktemp "${TMPDIR:-/tmp}/claimcore-postgres-index.XXXXXXXX")"
trap 'rm -f -- "$index_file"' EXIT

docker buildx imagetools inspect --raw "$index_ref" > "$index_file"
actual_digest="$(shasum -a 256 "$index_file" | awk '{print $1}')"
if [[ "$actual_digest" != "$index_digest" ]]; then
  printf '%s\n' 'Official PostgreSQL index bytes did not match the pinned SHA-256.' >&2
  exit 1
fi

child_digest="$(jq --exit-status --raw-output --arg arch "$arch" --arg variant "$variant" '
  if .schemaVersion != 2 or
     .mediaType != "application/vnd.oci.image.index.v1+json" then
    error("Pinned PostgreSQL reference is not the expected OCI image index")
  else
    [.manifests[] |
      select(.platform.os == "linux" and
             .platform.architecture == $arch and
             (.platform.variant // "") == $variant and
             .mediaType == "application/vnd.oci.image.manifest.v1+json") |
      .digest] as $matches |
    if ($matches | length) == 1 then $matches[0]
    else error("Pinned PostgreSQL index has no unique runnable platform child") end
  end
' "$index_file")"
if [[ ! "$child_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
  printf '%s\n' 'Pinned PostgreSQL index returned an invalid child digest.' >&2
  exit 1
fi

printf 'postgres@%s\n' "$child_digest"

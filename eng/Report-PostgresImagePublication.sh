#!/usr/bin/env bash
set -euo pipefail

version="${version:-}"
for name in IMAGE version INDEX_DIGEST AMD64_DIGEST ARM64_DIGEST GITHUB_SHA GITHUB_REPOSITORY GITHUB_STEP_SUMMARY; do
  if [[ -z "${!name:-}" ]]; then
    printf 'Missing PostgreSQL publication report input: %s.\n' "$name" >&2
    exit 1
  fi
done
if [[ "$#" -ne 0 || ! "$INDEX_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ||
      ! "$AMD64_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ||
      ! "$ARM64_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ||
      ! "$GITHUB_SHA" =~ ^[0-9a-f]{40}$ ||
      ! "$version" =~ ^18[.]6-trixie-p2-r[1-9][0-9]*-[1-9][0-9]*$ ||
      "$IMAGE" != ghcr.io/resoltico/claimcore-postgres ||
      "$GITHUB_REPOSITORY" != resoltico/claimcore ||
      ! -f "$GITHUB_STEP_SUMMARY" ]]; then
  printf '%s\n' 'PostgreSQL publication report inputs are invalid.' >&2
  exit 1
fi

test "$(docker buildx imagetools inspect "$IMAGE:$version" --format '{{.Manifest.Digest}}')" = "$INDEX_DIGEST"
test "$(git rev-parse HEAD)" = "$GITHUB_SHA"
bash eng/Check-PostgresImageNotice.sh

base_image='postgres:18.6@sha256:4ef4dbc939d61acea57712655ddb4b4ab27419c913f94cca0cd57cb3ea3c2280'
snapshot='20260913T022727Z'
test "$(sed -n 's/^FROM //p' db/Dockerfile.postgres-patched)" = "$base_image"
test "$(grep -Fc "http://snapshot.debian.org/archive/debian/$snapshot/" db/Dockerfile.postgres-patched)" -eq 1
test "$(grep -Fc "http://snapshot.debian.org/archive/debian-security/$snapshot/" db/Dockerfile.postgres-patched)" -eq 1

recipe_sha="$(sha256sum db/Dockerfile.postgres-patched | cut -d ' ' -f 1)"
exceptions_sha="$(sha256sum container-vulnerability-exceptions.yaml | cut -d ' ' -f 1)"
upstream_license_sha="$(sha256sum db/postgres-upstream/LICENSE | cut -d ' ' -f 1)"
upstream_authors_sha="$(sha256sum db/postgres-upstream/AUTHORS | cut -d ' ' -f 1)"
{
  echo '### Maintained PostgreSQL image'
  echo
  echo "Final index: $IMAGE:$version@$INDEX_DIGEST"
  echo "linux/amd64 child: $IMAGE@$AMD64_DIGEST"
  echo "linux/arm64 child: $IMAGE@$ARM64_DIGEST"
  echo
  echo "Source: $GITHUB_REPOSITORY@$GITHUB_SHA"
  echo "Base: $base_image"
  echo "Signed Debian snapshot: $snapshot (main and security)"
  echo "Dockerfile SHA-256: $recipe_sha"
  echo "Vulnerability exceptions SHA-256: $exceptions_sha"
  echo 'Inherited docker-library/postgres scripts MIT notice: docker-library/postgres@e00e1bd34ec5c8a8e7ad89b273b3d42efaf6d5bc'
  echo "Upstream LICENSE SHA-256: $upstream_license_sha"
  echo "Upstream AUTHORS SHA-256: $upstream_authors_sha"
  echo
  echo 'The children passed local recipe/startup, upstream MIT notice, same-volume upgrade, and Trivy checks before push, then remote digest scans. The index has exactly these two runnable platforms. Per-arch SBOMs and index provenance were attested in GHCR.'
} >> "$GITHUB_STEP_SUMMARY"

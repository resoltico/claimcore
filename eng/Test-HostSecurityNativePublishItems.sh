#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root"
case "$(uname -s)" in
  Darwin) extension=dylib ;;
  Linux) extension=so ;;
  *) printf 'Native publish cardinality is unsupported on this host.\n' >&2; exit 64 ;;
esac
name="libclaimcore_hostsecurity_native.$extension"

check() {
  local project="$1" expected="$2" actual
  actual="$(dotnet msbuild "$project" -getItem:None |
    jq --arg name "$name" '[.Items.None[]? |
      select(.Link == $name and .CopyToPublishDirectory == "PreserveNewest")] | length')"
  if [[ "$actual" != "$expected" ]]; then
    printf 'Native publish item cardinality failed for %s.\n' "$project" >&2
    exit 1
  fi
}

# A library cannot smuggle another shim into a host's publish tree. Each
# executable must own exactly one explicitly published fixed-ABI shim.
check src/ClaimCore.Hosting/ClaimCore.Hosting.fsproj 0
check src/ClaimCore.Web/ClaimCore.Web.fsproj 1
check src/ClaimCore.Database/ClaimCore.Database.fsproj 1
check src/ClaimCore.Cli/ClaimCore.Cli.fsproj 1
printf 'Native publish item cardinality passed (Hosting 0; Web, Database, CLI 1 each).\n'

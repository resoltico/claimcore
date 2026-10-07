#!/usr/bin/env bash
# Qualification owns only fresh private directories, never an existing operator installation.
create_private_qualification_directory() {
  local claimcore_parent="${TMPDIR:-/tmp}"
  if [[ "$(uname -s)" == Darwin ]]; then
    claimcore_parent=/Users/Shared
  fi
  mktemp -d "${claimcore_parent}/${1:?A private qualification directory pattern is required.}"
}

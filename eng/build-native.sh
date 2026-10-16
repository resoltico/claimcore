#!/usr/bin/env bash
# Authoritative native compiler/SDK policy. No Node or private runtime ABI is used.
set -euo pipefail

refuse() { echo "ClaimCore native build: $*" >&2; exit 65; }
[[ $# -ge 2 ]] || refuse "expected a build mode and host RID"
mode="$1"
requested_rid="$2"
host_os="$(uname -s)"
host_machine="$(uname -m)"
case "${host_machine}" in
  arm64 | aarch64) host_arch=arm64 ;;
  x86_64) host_arch=x64 ;;
  *) refuse "supported host architectures are x64 and arm64" ;;
esac

flags=(-std=c17 -O2 -Wall -Wextra -Wpedantic -Werror -fstack-protector-strong)
case "${host_os}" in
  Darwin)
    host_rid="osx-${host_arch}"
    command -v xcrun >/dev/null || refuse "install macOS Command Line Tools"
    compiler=(xcrun --sdk macosx clang)
    compiler_version="$("${compiler[@]}" --version)"
    compiler_major="$(sed -nE 's/^Apple clang version ([0-9]+).*/\1/p' <<< "${compiler_version}")"
    [[ -n "${compiler_major}" && "${compiler_major}" -ge 17 ]] || refuse "Apple Clang 17 or newer is required"
    sdk_version="$(xcrun --sdk macosx --show-sdk-version)"
    [[ "${sdk_version%%.*}" -ge 15 ]] || refuse "macOS SDK 15 or newer is required"
    sdk_root="$(xcrun --sdk macosx --show-sdk-path)"
    flags+=(-isysroot "${sdk_root}" -mmacosx-version-min=14.0 -arch "${host_machine}")
    library_flags=(-dynamiclib -fPIC -fvisibility=hidden)
    ;;
  Linux)
    host_rid="linux-${host_arch}"
    command -v gcc >/dev/null || refuse "install GCC 13 or newer and glibc development headers"
    compiler=(gcc)
    compiler_version="$(gcc --version)"
    compiler_major="$(gcc -dumpfullversion | cut -d. -f1)"
    [[ "${compiler_major}" -ge 13 ]] || refuse "GCC 13 or newer is required"
    sdk_version="$(getconf GNU_LIBC_VERSION)" || refuse "glibc is required; musl targets are unsupported"
    compiler_target="$(gcc -dumpmachine)"
    case "${host_arch}:${compiler_target}" in
      x64:x86_64*-linux-gnu | arm64:aarch64*-linux-gnu) ;;
      *) refuse "GCC must target the matching native glibc host" ;;
    esac
    sdk_root="$(gcc -print-sysroot)"
    library_flags=(-shared -fPIC -fvisibility=hidden "-Wl,-z,relro,-z,now,-z,noexecstack")
    ;;
  *) refuse "supported native build hosts are Linux/glibc and macOS" ;;
esac
[[ "${requested_rid}" == "${host_rid}" ]] || refuse "cross-RID builds are unsupported; use the matching native host"

if [[ "${mode}" == identify ]]; then
  [[ $# -eq 3 ]] || refuse "identify requires one toolchain-stamp path"
  stamp="$3"
  mkdir -p "$(dirname "${stamp}")"
  temporary="$(mktemp "${stamp}.XXXXXXXX")"
  trap 'rm -f -- "${temporary}"' EXIT
  printf '%s\n' "${host_rid}" "${compiler_version}" "${sdk_version}" "${sdk_root}" "${flags[@]}" > "${temporary}"
  if ! cmp -s "${temporary}" "${stamp}"; then mv -f -- "${temporary}" "${stamp}"; fi
  exit 0
fi

[[ $# -eq 4 ]] || refuse "compilation requires a source and output path"
source_path="$3"
output_path="$4"
mkdir -p "$(dirname "${output_path}")"
temporary="${output_path}.building"
trap 'rm -f -- "${temporary}"' EXIT
case "${mode}" in
  library) "${compiler[@]}" "${flags[@]}" "${library_flags[@]}" "${source_path}" -o "${temporary}" ;;
  program) "${compiler[@]}" "${flags[@]}" "${source_path}" -o "${temporary}" ;;
  *) refuse "unknown native build mode" ;;
esac
mv -f -- "${temporary}" "${output_path}"

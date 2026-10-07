#!/usr/bin/env bash
set -euo pipefail

# One disposable Keycloak fixture encloses serial, independent two-cluster browser runs.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
# shellcheck source=eng/PublishedQualificationRuntime.sh
source "${repo_root}/eng/PublishedQualificationRuntime.sh"
web_dll="${1:?Pass published Web directory.}/ClaimCore.Web.dll"
database_dll="${2:?Pass published Database directory.}/ClaimCore.Database.dll"
engine_scope="${3:-all}"
if [[ "${engine_scope}" == all && -n "${CLAIMCORE_TEST_RUN_LABEL:-}" ]]; then
  printf 'All-engine qualification requires independently labeled runs.\n' >&2
  exit 64
fi
[[ -f "${web_dll}" && -f "${database_dll}" ]] || {
  printf 'Published Web and Database assemblies are required.\n' >&2
  exit 64
}
case "${engine_scope}" in
  all) engines=(chromium firefox webkit) ;;
  chromium | firefox | webkit) engines=("${engine_scope}") ;;
  *)
    printf 'Use all, chromium, firefox, or webkit.\n' >&2
    exit 64
    ;;
esac
# Reject invalid public selections before allocating an orchestration snapshot.
enter_qualification_run "$@"
if [[ -z "${CLAIMCORE_TEST_OIDC_ISSUER:-}" ]]; then
  exec bash "${repo_root}/eng/oidc/Run-SyntheticOidc.sh" -- bash "$0" "$@"
fi
[[ -f "${CLAIMCORE_TEST_OIDC_CA_CERT:-}" &&
  -f "${CLAIMCORE_TEST_OIDC_CREDENTIALS:-}" ]] || {
  printf 'Synthetic OIDC files are missing.\n' >&2
  exit 64
}
oidc_ca_dir="$(cd -P "$(dirname "${CLAIMCORE_TEST_OIDC_CA_CERT}")" && pwd -P)"
oidc_ca="${oidc_ca_dir}/$(basename "${CLAIMCORE_TEST_OIDC_CA_CERT}")"

select_qualification_runtime
# shellcheck source=eng/PrivateQualificationPaths.sh
source "${repo_root}/eng/PrivateQualificationPaths.sh"
# shellcheck source=eng/PublishedBrowserDiagnostics.sh
source "${repo_root}/eng/PublishedBrowserDiagnostics.sh"
# shellcheck source=eng/PublishedBrowserRuntime.sh
source "${repo_root}/eng/PublishedBrowserRuntime.sh"
# shellcheck source=eng/PublishedBrowserCli.sh
source "${repo_root}/eng/PublishedBrowserCli.sh"
run_engine() (
  local engine="$1" state_dir created_dir primary witness host_pid='' cli_results='' origin run_label
  umask 077
  run_label="claimcore-browser-${engine}-$(openssl rand -hex 8)"
  created_dir="$(create_private_qualification_directory "claimcore-web-e2e.${engine}.XXXXXXXX")"
  if ! state_dir="$(cd -P "${created_dir}" && pwd -P)"; then
    rmdir "${created_dir}" 2>/dev/null || true
    printf 'The private browser state path could not be resolved.\n' >&2
    exit 64
  fi
  primary="claimcore-primary-$(openssl rand -hex 8)"
  witness="claimcore-witness-$(openssl rand -hex 8)"
  mkdir -p "${repo_root}/artifacts/browser" "${state_dir}/diagnostics"
  trap 'cleanup "${state_dir}" "${run_label}" "${host_pid}" "${cli_results}" "${engine}"' EXIT
  # shellcheck source=eng/PublishedWebE2E-Setup.sh
  source "${repo_root}/eng/PublishedWebE2E-Setup.sh"
  origin="${CLAIMCORE_TEST_WEB_ORIGIN:?Synthetic Web origin was not allocated.}"
  start_browser_host "${state_dir}" host_pid
  require_browser_host "${state_dir}" "${origin}" "${host_pid}"
  if [[ -n "${CLAIMCORE_PUBLISHED_CLI_DIR:-}" ]]; then
    prepare_browser_cli "${state_dir}" "${origin}"
  fi
  run_browser_delivery "${state_dir}" "${origin}" "${engine}"
  if [[ -n "${CLAIMCORE_PUBLISHED_CLI_DIR:-}" ]]; then
    cli_results="${CLAIMCORE_ACCEPTANCE_RESULTS_DIR:-${repo_root}/artifacts/test-results/acceptance-local-${run_label}}"
    run_browser_cli_acceptance "${state_dir}" "${origin}" "${engine}" "${cli_results}"
  fi
  printf 'Published %s OIDC browser qualification passed with two isolated clusters.\n' "${engine}"
  cleanup "${state_dir}" "${run_label}" "${host_pid}" "${cli_results}" "${engine}"
)

for engine in "${engines[@]}"; do run_engine "${engine}"; done

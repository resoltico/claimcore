#!/usr/bin/env bash
set -o pipefail
# Native CLI authentication consumes the same published browser fixture and grants.
: "${repo_root:?}" "${oidc_ca:?}"
: "${CLAIMCORE_TEST_OIDC_CREDENTIALS:?}" "${CLAIMCORE_TEST_OIDC_ISSUER:?}"

prepare_browser_cli() {
  local state_dir="$1" origin="$2" cli_dll
  cli_dll="${CLAIMCORE_PUBLISHED_CLI_DIR:?}/ClaimCore.Cli.dll"
  [[ -f "${cli_dll}" ]] || {
    printf 'Published CLI assembly is missing.\n' >&2
    exit 64
  }
  jq -r '.serviceClientSecret' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}" \
    >"${state_dir}/service-client.secret"
  mkdir "${state_dir}/cli-browser"
  cp "${repo_root}/eng/oidc/Run-CliBrowserDriver.sh" "${state_dir}/cli-browser/open"
  cp "${repo_root}/eng/oidc/Run-CliBrowserDriver.sh" "${state_dir}/cli-browser/xdg-open"
  chmod 700 "${state_dir}/cli-browser/open" "${state_dir}/cli-browser/xdg-open"
  printf '%s\n' '{"protocolVersion":4,"endpoint":"case.list","input":{"limit":1}}' \
    >"${state_dir}/cli-before-grant.json"
  require_unregistered_cli_refusal "${state_dir}" "${origin}" "${cli_dll}"
}

require_unregistered_cli_refusal() {
  local state_dir="$1" origin="$2" cli_dll="$3" cli_before_status
  service_client_id="$(jq -er '.serviceClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
  set +e
  CLAIMCORE_SERVICE_URL="${origin}/" CLAIMCORE_OIDC_ISSUER="${CLAIMCORE_TEST_OIDC_ISSUER}" \
    CLAIMCORE_OIDC_CLIENT_ID="${service_client_id}" \
    CLAIMCORE_CLI_AUTH_MODE=automation \
    CLAIMCORE_OIDC_CLIENT_SECRET_FILE="${state_dir}/service-client.secret" \
    CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE="${oidc_ca}" \
    CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE="${state_dir}/web-ca.pem" \
    dotnet "${cli_dll}" call <"${state_dir}/cli-before-grant.json" \
    >"${state_dir}/diagnostics/cli-before-grant.out" \
    2>"${state_dir}/diagnostics/cli-before-grant.err"
  cli_before_status=$?
  set -e
  if [[ "${cli_before_status}" != 2 ]] ||
    ! jq -e '.protocolVersion == 4 and .kind == "result" and
      .endpoint == "case.list" and .service.outcome.tag == "REJECTED" and
      (.service.outcome.data.items? == null)' \
      "${state_dir}/diagnostics/cli-before-grant.out" >/dev/null ||
    [[ -s "${state_dir}/diagnostics/cli-before-grant.err" ]]; then
    printf 'Unregistered service principal denial probe failed (exit %s).\n' "${cli_before_status}" >&2
    jq -c '{resultFrame:(.kind == "result"), protocolV4:(.protocolVersion == 4),
      caseList:(.endpoint == "case.list"), serviceFailure:(.kind == "serviceFailure"),
      localCode:(if ((.code // "") | test("^CLI_[A-Z_]{1,80}$")) then .code else null end), refused:(.service.outcome.tag == "REJECTED"),
      itemsAbsent:(.service.outcome.data.items? == null)}' \
      "${state_dir}/diagnostics/cli-before-grant.out" >&2 || true
    stderr_populated=no
    [[ -s "${state_dir}/diagnostics/cli-before-grant.err" ]] && stderr_populated=yes
    printf 'CLI stderr populated: %s\n' "${stderr_populated}" >&2
    exit 1
  fi
}

run_browser_cli_acceptance() {
  local state_dir="$1" origin="$2" engine="$3" cli_results="$4"
  [[ ! -e "${cli_results}" ]] || {
    printf 'CLI result path must start absent.\n' >&2
    exit 64
  }
  service_client_id="$(jq -er '.serviceClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
  public_client_id="$(jq -er '.publicClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
  expected_tests="$(wc -l <"${repo_root}/tests/inventory/ClaimCore.AcceptanceTests.txt" | tr -d ' ')"
  CLAIMCORE_ACCEPTANCE_CLI_DIR="${CLAIMCORE_PUBLISHED_CLI_DIR:?}" \
    CLAIMCORE_ACCEPTANCE_PRIVATE_DIR="${state_dir}" \
    CLAIMCORE_ACCEPTANCE_SERVICE_URL="${origin}/" \
    CLAIMCORE_ACCEPTANCE_ISSUER="${CLAIMCORE_TEST_OIDC_ISSUER}" \
    CLAIMCORE_ACCEPTANCE_OIDC_CA_FILE="${oidc_ca}" \
    CLAIMCORE_ACCEPTANCE_SERVICE_CA_FILE="${state_dir}/web-ca.pem" \
    CLAIMCORE_ACCEPTANCE_SERVICE_CLIENT_ID="${service_client_id}" \
    CLAIMCORE_ACCEPTANCE_SERVICE_SECRET_FILE="${state_dir}/service-client.secret" \
    CLAIMCORE_ACCEPTANCE_PUBLIC_CLIENT_ID="${public_client_id}" \
    CLAIMCORE_ACCEPTANCE_OIDC_CREDENTIALS_FILE="${CLAIMCORE_TEST_OIDC_CREDENTIALS}" \
    CLAIMCORE_ACCEPTANCE_BROWSER_DIR="${state_dir}/cli-browser" \
    CLAIMCORE_ACCEPTANCE_DRIVER_PATH="${repo_root}/web/e2e/cli-pkce-driver.mjs" \
    dotnet test --project "${repo_root}/tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj" \
    --configuration Release --no-build --no-restore \
    --results-directory="${cli_results}" \
    --minimum-expected-tests="${expected_tests}" \
    --zero-tests-policy=strict --timeout=20m -- \
    --settings="${repo_root}/eng/expecto.runsettings" --report-trx \
    --report-trx-filename=ClaimCore.AcceptanceTests.trx \
    >"${state_dir}/diagnostics/cli-acceptance.log" 2>&1 || {
    printf 'Published authenticated CLI acceptance failed.\n' >&2
    exit 1
  }
  node "${repo_root}/eng/ci/suites/verify-report.mjs" acceptance \
    "${cli_results}/ClaimCore.AcceptanceTests.trx"
  printf 'Published authenticated CLI acceptance passed in isolated %s fixture.\n' "${engine}"
}

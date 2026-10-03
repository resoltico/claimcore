#!/usr/bin/env bash
set -euo pipefail

# One disposable Keycloak fixture encloses serial, independent two-cluster browser runs.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
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

expected_node="$(tr -d '\r\n' <"${repo_root}/.node-version")"
expected_npm="$(jq -r '.engines.npm' "${repo_root}/web/package.json")"
runtime_prefix=()
if [[ "$(node --version 2>/dev/null || true)" != "v${expected_node}" ]] ||
  [[ "$(npm --version 2>/dev/null || true)" != "${expected_npm}" ]]; then
  command -v mise >/dev/null || {
    printf 'Locked Node/npm is unavailable.\n' >&2
    exit 64
  }
  runtime_prefix=(mise exec "node@${expected_node}" --)
fi
if [[ "$("${runtime_prefix[@]}" node --version)" != "v${expected_node}" ]] ||
  [[ "$("${runtime_prefix[@]}" npm --version)" != "${expected_npm}" ]]; then
  printf 'Locked Node/npm could not be selected.\n' >&2
  exit 64
fi

run_engine() (
  engine="$1"
  umask 077
  run_label="claimcore-browser-${engine}-$(openssl rand -hex 8)"
  created_dir="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-web-e2e.${engine}.XXXXXXXX")"
  if ! state_dir="$(cd -P "${created_dir}" && pwd -P)"; then
    rmdir "${created_dir}" 2>/dev/null || true
    printf 'The private browser state path could not be resolved.\n' >&2
    exit 64
  fi
  primary="claimcore-primary-$(openssl rand -hex 8)"
  witness="claimcore-witness-$(openssl rand -hex 8)"
  host_pid=''
  cli_results=''
  mkdir -p "${repo_root}/artifacts/browser"
  mkdir -p "${state_dir}/diagnostics"
  diagnostic() { jq -r '.diagnostic.id // empty' "$1" 2>/dev/null || true; }
  web_failure() {
    local code
    code="$(rg --only-matching 'WEB_[A-Z_]+' "${state_dir}/diagnostics/web-host.log" \
      2>/dev/null | head -n 1 || true)"
    if [[ "${code}" =~ ^WEB_[A-Z_]+$ ]]; then
      printf 'Published Web host exited before liveness: %s.\n' "${code}" >&2
    else
      printf 'Published Web host exited before liveness.\n' >&2
    fi
  }
  browser_failure() {
    local category callback_status callback_path
    if [[ -f "${repo_root}/artifacts/browser/${engine}.json" ]]; then
      category="$(jq -r '.failureCodes[0] // empty' "${repo_root}/artifacts/browser/${engine}.json" 2>/dev/null || true)"
      if [[ "${category}" =~ ^E2E_[A-Z_0-9]+$ ]]; then
        printf 'Safe browser failure category: %s.\n' "${category}" >&2
      fi
    fi
    if [[ -f "${state_dir}/diagnostics/safe-browser-failure.json" ]] &&
      jq -e --arg app 'https://localhost:5443' --arg idp "${issuer_base}" \
        'keys == ["callbackStatus","origin","pathname"] and
          (.callbackStatus | type == "number" and . >= 0 and . <= 599) and
          (.origin == $app or .origin == $idp) and
          (.pathname | type == "string" and test("^/[A-Za-z0-9/_-]{0,255}$"))' \
        "${state_dir}/diagnostics/safe-browser-failure.json" >/dev/null 2>&1; then
      callback_status="$(jq -r '.callbackStatus' "${state_dir}/diagnostics/safe-browser-failure.json")"
      callback_path="$(jq -r '.pathname' "${state_dir}/diagnostics/safe-browser-failure.json")"
      printf 'Safe OIDC return diagnostic: callback HTTP %s, path %s.\n' \
        "${callback_status}" "${callback_path}" >&2
    fi
    category="$(rg --only-matching \
      'E2E_OIDC_RETURN_[0-9]+_https://[A-Za-z0-9.:-]+(/[A-Za-z0-9/_-]*)?|E2E_[A-Z_0-9]+|TimeoutError|net::ERR_[A-Z_]+|NS_ERROR_[A-Z_]+' \
      "${state_dir}/diagnostics/playwright.log" 2>/dev/null | head -n 1 || true)"
    if [[ "${category}" =~ ^E2E_OIDC_RETURN_[0-9]+_https://[A-Za-z0-9.:-]+(/[A-Za-z0-9/_-]*)?$ ]] ||
      [[ "${category}" =~ ^(E2E_[A-Z_0-9]+|TimeoutError|net::ERR_[A-Z_]+|NS_ERROR_[A-Z_]+)$ ]]; then
      printf 'Safe browser failure category: %s.\n' "${category}" >&2
    fi
  }
  scan_output() {
    local -a secrets=() roots=(--scan-root "${repo_root}/artifacts/browser" --scan-root "${state_dir}/diagnostics")
    local secret
    for secret in "${state_dir}"/*.password "${state_dir}"/*.subject \
      "${state_dir}"/*.secret \
      "${state_dir}"/primary.env "${state_dir}"/witness.env \
      "${state_dir}"/witness-key.json "${state_dir}"/writer.capability \
      "${state_dir}"/suppression-key.json \
      "${state_dir}"/recovery-artifact-key.json \
      "${state_dir}"/oidc-client.secret \
      "${state_dir}"/service-client.secret \
      "${state_dir}"/initial-owner.json "${state_dir}"/principals.json \
      "${state_dir}"/claimant.canary \
      "${CLAIMCORE_TEST_OIDC_CREDENTIALS}"; do
      if [[ -f "${secret}" ]]; then
        secrets+=(--secret-file "${secret}")
      fi
    done
    if [[ -n "${cli_results}" && -d "${cli_results}" ]]; then
      roots+=(--scan-root "${cli_results}")
    fi
    [[ ${#secrets[@]} -eq 0 ]] ||
      node "${repo_root}/eng/ci/policy/sensitive-output.mjs" "${roots[@]}" "${secrets[@]}" >/dev/null 2>&1
  }
  cleanup() {
    local status=$?
    trap - EXIT
    set +e
    if [[ -n "${host_pid}" ]]; then
      kill "${host_pid}" 2>/dev/null
      wait "${host_pid}" 2>/dev/null
    fi
    scan_output || {
      printf 'Sensitive browser output was rejected.\n' >&2
      status=1
    }
    bash "${repo_root}/eng/Remove-LabeledTestContainers.sh" "${run_label}" || status=1
    if [[ "${state_dir}" == */claimcore-web-e2e."${engine}".* && -d "${state_dir}" ]]; then
      rm -r -- "${state_dir}" || status=1
    else
      status=1
    fi
    exit "${status}"
  }
  trap cleanup EXIT

  # shellcheck source=eng/PublishedWebE2E-Setup.sh
  source "${repo_root}/eng/PublishedWebE2E-Setup.sh"

  jq -r '.webClientSecret' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}" \
    >"${state_dir}/oidc-client.secret"
  printf '%s\n' 'Synthetic claimant canary' >"${state_dir}/claimant.canary"
  bash "${repo_root}/eng/Generate-SyntheticWebTls.sh" "${state_dir}"
  origin='https://localhost:5443'
  CLAIMCORE_CONNECTION_FILE="${state_dir}/primary-app.connection" \
    CLAIMCORE_WITNESS_CONNECTION_FILE="${state_dir}/witness-writer.connection" \
    CLAIMCORE_WITNESS_KEY_FILE="${state_dir}/witness-key.json" \
    CLAIMCORE_WRITER_CAPABILITY_FILE="${state_dir}/writer.capability" \
    CLAIMCORE_SUPPRESSION_KEY_FILE="${state_dir}/suppression-key.json" \
    CLAIMCORE_RECOVERY_ARTIFACT_KEY_FILE="${state_dir}/recovery-artifact-key.json" \
    CLAIMCORE_WEB_CERTIFICATE_PATH="${state_dir}/web.pfx" \
    CLAIMCORE_WEB_ORIGIN="${origin}" CLAIMCORE_WEB_STATE_DIR="${state_dir}/web-state" \
    CLAIMCORE_OIDC_ISSUER="${CLAIMCORE_TEST_OIDC_ISSUER}" \
    CLAIMCORE_OIDC_CA_CERT_FILE="${oidc_ca}" \
    CLAIMCORE_OIDC_CLIENT_ID="$(jq -r '.webClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")" \
    CLAIMCORE_OIDC_CLIENT_SECRET_FILE="${state_dir}/oidc-client.secret" \
    CLAIMCORE_OIDC_API_AUDIENCE="$(jq -r '.apiAudience' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")" \
    CLAIMCORE_OIDC_CLI_CLIENT_ID="$(jq -r '.publicClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")" \
    CLAIMCORE_OIDC_SERVICE_CLIENT_ID="$(jq -r '.serviceClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")" \
    dotnet "${web_dll}" >"${state_dir}/diagnostics/web-host.log" 2>&1 &
  host_pid=$!
  for _ in $(seq 1 45); do
    curl --cacert "${state_dir}/web-ca.pem" --fail --silent "${origin}/health/live" \
      >/dev/null 2>&1 && break
    kill -0 "${host_pid}" 2>/dev/null || {
      web_failure
      exit 1
    }
    sleep 1
  done
  curl --cacert "${state_dir}/web-ca.pem" --fail --silent "${origin}/health/live" \
    >/dev/null || {
    printf 'Published Web host was not live.\n' >&2
    exit 1
  }
  status="$(curl --cacert "${state_dir}/web-ca.pem" --silent --output /dev/null \
    --write-out '%{http_code}' -H 'Host: example.invalid' "${origin}/health/live")"
  [[ "${status}" == 403 ]] || {
    printf 'Foreign Host was not refused.\n' >&2
    exit 1
  }
  if [[ -n "${CLAIMCORE_PUBLISHED_CLI_DIR:-}" ]]; then
    cli_dll="${CLAIMCORE_PUBLISHED_CLI_DIR}/ClaimCore.Cli.dll"
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
    set +e
    CLAIMCORE_SERVICE_URL="${origin}/" CLAIMCORE_OIDC_ISSUER="${CLAIMCORE_TEST_OIDC_ISSUER}" \
      CLAIMCORE_OIDC_CLIENT_ID="$(jq -r '.serviceClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")" \
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
      printf 'Unregistered service principal was not denied without disclosure.\n' >&2
      exit 1
    fi
  fi
  progress_file="${state_dir}/progress"
  if ! CLAIMCORE_WEB_BASE_URL="${origin}" CLAIMCORE_WEB_E2E_ENGINE="${engine}" \
    CLAIMCORE_WEB_E2E_PRINCIPALS_FILE="${state_dir}/principals.json" \
    CLAIMCORE_WEB_E2E_SAFE_FAILURE_FILE="${state_dir}/diagnostics/safe-browser-failure.json" \
    CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR="${state_dir}/playwright-private" \
    CLAIMCORE_WEB_E2E_PROGRESS_FILE="${progress_file}" \
    "${runtime_prefix[@]}" npm --prefix "${repo_root}/web" run test:e2e -- --project "${engine}" \
    >"${state_dir}/diagnostics/playwright.log" 2>&1; then
    stage='not-started'
    [[ -f "${progress_file}" ]] &&
      stage="$(tr -cd 'a-z0-9-\n' <"${progress_file}" | head -n 1)"
    printf 'The %s browser run stopped after safe stage: %s.\n' "${engine}" "${stage}" >&2
    browser_failure
    exit 1
  fi
  if ! node "${repo_root}/eng/ci/suites/frontend.mjs" browser "${engine}"; then
    printf 'Published %s browser report inventory differs from the reviewed catalog.\n' "${engine}" >&2
    exit 1
  fi
  if [[ -n "${CLAIMCORE_PUBLISHED_CLI_DIR:-}" ]]; then
    cli_results="${CLAIMCORE_ACCEPTANCE_RESULTS_DIR:-${repo_root}/artifacts/test-results/acceptance-local-${run_label}}"
    [[ ! -e "${cli_results}" ]] || {
      printf 'CLI result path must start absent.\n' >&2
      exit 64
    }
    CLAIMCORE_ACCEPTANCE_CLI_DIR="${CLAIMCORE_PUBLISHED_CLI_DIR}" \
      CLAIMCORE_ACCEPTANCE_PRIVATE_DIR="${state_dir}" \
      CLAIMCORE_ACCEPTANCE_SERVICE_URL="${origin}/" \
      CLAIMCORE_ACCEPTANCE_ISSUER="${CLAIMCORE_TEST_OIDC_ISSUER}" \
      CLAIMCORE_ACCEPTANCE_OIDC_CA_FILE="${oidc_ca}" \
      CLAIMCORE_ACCEPTANCE_SERVICE_CA_FILE="${state_dir}/web-ca.pem" \
      CLAIMCORE_ACCEPTANCE_SERVICE_CLIENT_ID="$(jq -r '.serviceClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")" \
      CLAIMCORE_ACCEPTANCE_SERVICE_SECRET_FILE="${state_dir}/service-client.secret" \
      CLAIMCORE_ACCEPTANCE_PUBLIC_CLIENT_ID="$(jq -r '.publicClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")" \
      CLAIMCORE_ACCEPTANCE_OIDC_CREDENTIALS_FILE="${CLAIMCORE_TEST_OIDC_CREDENTIALS}" \
      CLAIMCORE_ACCEPTANCE_BROWSER_DIR="${state_dir}/cli-browser" \
      CLAIMCORE_ACCEPTANCE_DRIVER_PATH="${repo_root}/web/e2e/cli-pkce-driver.mjs" \
      dotnet test --project "${repo_root}/tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj" \
      --configuration Release --no-build --no-restore \
      --results-directory="${cli_results}" \
      --minimum-expected-tests="$(wc -l <"${repo_root}/tests/inventory/ClaimCore.AcceptanceTests.txt" | tr -d ' ')" \
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
  fi
  printf 'Published %s OIDC browser qualification passed with two isolated clusters.\n' "${engine}"
  cleanup
)

for engine in "${engines[@]}"; do run_engine "${engine}"; done

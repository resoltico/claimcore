#!/usr/bin/env bash
set -o pipefail
# Host lifetime and browser delivery consume one caller-owned fixture.
: "${repo_root:?}" "${web_dll:?}" "${oidc_ca:?}"
: "${CLAIMCORE_TEST_OIDC_CREDENTIALS:?}" "${CLAIMCORE_TEST_OIDC_ISSUER:?}"

start_browser_host() {
  local state_dir="$1" origin="${CLAIMCORE_TEST_WEB_ORIGIN:?Synthetic Web origin was not allocated.}"
  [[ "$2" == host_pid ]] || return 64
  jq -r '.webClientSecret' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}" \
    >"${state_dir}/oidc-client.secret"
  printf '%s\n' 'Synthetic claimant canary' >"${state_dir}/claimant.canary"
  bash "${repo_root}/eng/Generate-SyntheticWebTls.sh" "${state_dir}"
  origin="${CLAIMCORE_TEST_WEB_ORIGIN:?Synthetic Web origin was not allocated.}"
  web_client_id="$(jq -er '.webClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
  api_audience="$(jq -er '.apiAudience' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
  public_client_id="$(jq -er '.publicClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
  service_client_id="$(jq -er '.serviceClientId' "${CLAIMCORE_TEST_OIDC_CREDENTIALS}")"
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
    CLAIMCORE_OIDC_CLIENT_ID="${web_client_id}" \
    CLAIMCORE_OIDC_CLIENT_SECRET_FILE="${state_dir}/oidc-client.secret" \
    CLAIMCORE_OIDC_API_AUDIENCE="${api_audience}" \
    CLAIMCORE_OIDC_CLI_CLIENT_ID="${public_client_id}" \
    CLAIMCORE_OIDC_SERVICE_CLIENT_ID="${service_client_id}" \
    dotnet "${web_dll}" >"${state_dir}/diagnostics/web-host.log" 2>&1 &
  printf -v "$2" '%s' "$!"
}

require_browser_host() {
  local state_dir="$1" origin="$2" host_pid="$3" status
  for _ in $(seq 1 45); do
    curl --cacert "${state_dir}/web-ca.pem" --fail --silent "${origin}/health/live" \
      >/dev/null 2>&1 && break
    kill -0 "${host_pid}" 2>/dev/null || {
      web_failure "${state_dir}"
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
}

run_browser_delivery() {
  local state_dir="$1" origin="$2" engine="$3" progress_file stage
  progress_file="${state_dir}/progress"
  if ! CLAIMCORE_WEB_BASE_URL="${origin}" CLAIMCORE_WEB_E2E_ENGINE="${engine}" \
    CLAIMCORE_WEB_E2E_PRINCIPALS_FILE="${state_dir}/principals.json" \
    CLAIMCORE_WEB_E2E_SAFE_FAILURE_FILE="${state_dir}/diagnostics/safe-browser-failure.json" \
    CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR="${state_dir}/playwright-private" \
    CLAIMCORE_WEB_E2E_PROGRESS_FILE="${progress_file}" \
    qualification_npm --prefix "${repo_root}/web" run test:e2e -- --project "${engine}" \
    >"${state_dir}/diagnostics/playwright.log" 2>&1; then
    stage='not-started'
    if [[ -f "${progress_file}" ]]; then
      stage="$(tr -cd 'a-z0-9-\n' <"${progress_file}" | head -n 1)" || stage='progress-unavailable'
    fi
    printf 'The %s browser run stopped after safe stage: %s.\n' "${engine}" "${stage}" >&2
    browser_failure "${state_dir}" "${origin}" "${engine}"
    exit 1
  fi
  if ! node "${repo_root}/eng/ci/suites/frontend.mjs" browser "${engine}"; then
    printf 'Published %s browser report inventory differs from the reviewed catalog.\n' "${engine}" >&2
    exit 1
  fi
}

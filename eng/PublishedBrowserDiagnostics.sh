#!/usr/bin/env bash
# Explicit per-fixture paths keep diagnostics and cleanup confined to their caller.
: "${repo_root:?The repository root is required.}"
: "${CLAIMCORE_TEST_OIDC_CREDENTIALS:?}" "${CLAIMCORE_TEST_OIDC_ISSUER:?}"

diagnostic() { jq -r '.diagnostic.id // empty' "$1" 2>/dev/null || true; }
web_failure() {
  local state_dir="$1"
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
  local state_dir="$1" origin="$2" engine="$3" issuer_base="${CLAIMCORE_TEST_OIDC_ISSUER%/realms/*}"
  local category callback_status callback_path
  if [[ -f "${repo_root}/artifacts/browser/${engine}.json" ]]; then
    category="$(jq -r '.failureCodes[0] // empty' "${repo_root}/artifacts/browser/${engine}.json" 2>/dev/null || true)"
    if [[ "${category}" =~ ^E2E_[A-Z_0-9]+$ ]]; then
      printf 'Safe browser failure category: %s.\n' "${category}" >&2
    fi
  fi
  if [[ -f "${state_dir}/diagnostics/safe-browser-failure.json" ]] &&
    jq -e --arg app "${origin}" --arg idp "${issuer_base}" \
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
  local state_dir="$1" cli_results="$2"
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
  local status=$? state_dir="$1" run_label="$2" host_pid="$3" cli_results="$4" engine="$5"
  trap - EXIT
  set +e
  if [[ -n "${host_pid}" ]]; then
    kill "${host_pid}" 2>/dev/null
    wait "${host_pid}" 2>/dev/null
  fi
  if ! scan_output "${state_dir}" "${cli_results}"; then
    printf 'Sensitive browser output was rejected.\n' >&2
    status=1
  elif [[ "${status}" != 0 ]]; then
    node "${repo_root}/eng/ci/retain-browser-failure.mjs" "${state_dir}/diagnostics" \
      "${repo_root}/artifacts/browser-failures/${run_label}" || status=1
  fi
  bash "${repo_root}/eng/Remove-LabeledTestContainers.sh" "${run_label}" || status=1
  if [[ "${state_dir}" == */claimcore-web-e2e."${engine}".* && -d "${state_dir}" ]]; then
    rm -r -- "${state_dir}" || status=1
  else
    status=1
  fi
  exit "${status}"
}

#!/usr/bin/env bash
# Run every stage, --fast (no Docker), or named stages in order. CI calls the same stages.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

case "${1:-}" in
  "") stages=(lint unit frontend workflows integration acceptance) ;;
  --fast) stages=(lint unit frontend) ;;
  *) stages=("$@") ;;
esac
# Validate all arguments before restoring inputs or running a partial gate.
for stage in "${stages[@]}"; do
  case "${stage}" in
    lint | unit | frontend | workflows | integration | postgres | web | browsers | acceptance) ;;
    *) echo "unknown stage: ${stage}" >&2; exit 64 ;;
  esac
done

validate_browser_selection() {
  if [[ -n "${CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE+x}" ]]; then
    case "${CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE}" in
      chromium | firefox | webkit) ;;
      *) echo "invalid browser engine" >&2; return 64 ;;
    esac
  fi
  case "${CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE-complete}" in
    complete) ;;
    smoke)
      if [[ -z "${CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE+x}" ]]; then
        echo "smoke requires one explicit browser engine" >&2
        return 64
      fi ;;
    *) echo "invalid browser scope" >&2; return 64 ;;
  esac
}

validate_postgres_selection() {
  # This preflight reads numeric declarations; native fixtures still validate the complete JSON.
  local declared_major declared_majors
  local requested="${CLAIMCORE_TEST_POSTGRES_MAJOR-}"
  if [[ -z "${CLAIMCORE_TEST_POSTGRES_MAJOR+x}" ]]; then return; fi
  if [[ "${requested}" =~ ^[1-9][0-9]*$ ]]; then
    declared_majors="$(sed -nE 's/^[[:space:]]*"major":[[:space:]]*([1-9][0-9]*),?[[:space:]]*$/\1/p' db/postgresql-baseline.json)"
    while IFS= read -r declared_major; do
      if [[ "${requested}" == "${declared_major}" ]]; then return; fi
    done <<< "${declared_majors}"
  fi
  echo "PostgreSQL test major must exactly match a listed baseline major" >&2
  return 64
}

# Refuse invalid expensive-stage inputs before restoring, installing browsers or building.
for stage in "${stages[@]}"; do
  case "${stage}" in
    browsers) validate_browser_selection ;;
    acceptance) validate_browser_selection; validate_postgres_selection ;;
    integration | postgres) validate_postgres_selection ;;
    *) : ;;
  esac
done

# Floors follow complete discovery. No filtered or retried run qualifies a stage.
tests() { # <project> <minimum-expected-tests> [configuration] [timeout] [settings]
  local report_directory
  mkdir -p artifacts/verification
  report_directory="$(mktemp -d "${PWD}/artifacts/verification/native.XXXXXXXX")"
  dotnet test --project "$1" --configuration "${3:-Release}" --no-build --no-restore \
    --minimum-expected-tests="$2" --zero-tests-policy=strict --timeout="${4:-30m}" \
    --report-trx --report-trx-filename="${report_directory}/results.trx" \
    -- --settings="${PWD}/${5:-eng/expecto.runsettings}"
}

native_restored=false
restore_native() {
  ${native_restored} && return
  local target="${1:-ClaimCore.slnx}"
  dotnet restore "${target}" --locked-mode
  if [[ "${target}" == ClaimCore.slnx ]]; then native_restored=true; fi
}

node_restored=false
restore_node() {
  ${node_restored} && return
  npm --prefix web ci
  node_restored=true
}

built=false
build_native() {
  ${built} && return
  local target="${1:-ClaimCore.slnx}"
  restore_native "${target}"
  dotnet build "${target}" --configuration Release --no-restore
  if [[ "${target}" == ClaimCore.slnx ]]; then built=true; fi
}

frontend_built=false
build_frontend() {
  ${frontend_built} && return
  restore_node
  npm --prefix web run build
  frontend_built=true
}

lint() {
  restore_native
  dotnet tool restore
  dotnet fantomas --check src tests eng
  bash eng/Check-FSharpLint.sh
}

unit() {
  build_native
  # Native freshness tests check the complete tracked contracts/ inventory and exact bytes.
  tests tests/ClaimCore.Tests/ClaimCore.Tests.fsproj 319
  dotnet build tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj \
    --configuration Debug --no-restore -p:Optimize=false
  tests tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj 40 Debug 15m
}

frontend() { # Node only: no generated contract prerequisite or .NET process.
  restore_node
  npm --prefix web audit --audit-level=low
  npm --prefix web run format:check
  npm --prefix web run lint
  npm --prefix web run typecheck
  npm --prefix web run test:unit
  npm --prefix web run dead-code
  npm --prefix web run licenses:check
  build_frontend
}

workflows() {
  shellcheck eng/*.sh deployment/*.sh
  docker run --rm --label claimcore.workflow-check=actionlint -v "${PWD}:/repo:ro" -w /repo \
    rhysd/actionlint@sha256:b1934ee5f1c509618f2508e6eb47ee0d3520686341fec936f3b79331f9315667 -color
}

integration() {
  build_native
  postgres
  web
}

postgres() {
  build_native tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj
  tests tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj 109 Release 20m eng/expecto.integration.runsettings
}

web() {
  build_native tests/ClaimCore.WebTests/ClaimCore.WebTests.fsproj
  tests tests/ClaimCore.WebTests/ClaimCore.WebTests.fsproj 76 Release 20m
}

browsers() {
  local engines=(chromium firefox webkit)
  if [[ -n "${CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE+x}" ]]; then
    case "${CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE}" in
      chromium) engines=(chromium) ;;
      firefox | webkit) engines=(chromium "${CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE}") ;;
      *) echo "invalid browser engine" >&2; return 64 ;;
    esac
  fi
  # Published CLI PKCE setup uses Chromium even when specifications select another engine.
  restore_node
  local platform
  platform="$(uname -s)"
  if [[ "${platform}" == Linux ]]; then
    npm --prefix web exec -- playwright install --with-deps "${engines[@]}"
  else
    npm --prefix web exec -- playwright install "${engines[@]}"
  fi
}

acceptance() {
  build_native
  build_frontend
  # One native case sequences every selected engine and stops before starting a later engine after a failure.
  tests tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj 17 Release 40m
}

for stage in "${stages[@]}"; do
  "${stage}"
done
platform="$(uname -s)"
printf 'Stages passed on %s: %s\n' "${platform}" "${stages[*]}"

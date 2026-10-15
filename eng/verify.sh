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
    lint | unit | frontend | workflows | integration | browsers | acceptance) ;;
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

# Refuse invalid expensive-stage inputs before restoring, installing browsers or building.
for stage in "${stages[@]}"; do
  case "${stage}" in
    browsers | acceptance) validate_browser_selection; break ;;
    *) : ;;
  esac
done

# Floors follow measured unfiltered suites. No filtered or retried run qualifies a stage.
tests() { # <project> <minimum-expected-tests> [configuration] [timeout]
  dotnet test --project "$1" --configuration "${3:-Release}" --no-build --no-restore \
    --minimum-expected-tests="$2" --zero-tests-policy=strict --timeout="${4:-30m}" \
    -- --settings="${PWD}/eng/expecto.runsettings"
}

native_restored=false
restore_native() {
  ${native_restored} && return
  dotnet restore ClaimCore.slnx --locked-mode
  native_restored=true
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
  restore_native
  dotnet build ClaimCore.slnx --configuration Release --no-restore
  built=true
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
  tests tests/ClaimCore.Tests/ClaimCore.Tests.fsproj 294
  dotnet build tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj \
    --configuration Debug --no-restore -p:Optimize=false
  tests tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj 31 Debug 15m
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
  tests tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj 108 Release 20m
  tests tests/ClaimCore.WebTests/ClaimCore.WebTests.fsproj 74 Release 20m
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
  tests tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj 16 Release 40m
}

for stage in "${stages[@]}"; do
  "${stage}"
done
platform="$(uname -s)"
printf 'Stages passed on %s: %s\n' "${platform}" "${stages[*]}"

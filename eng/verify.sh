#!/usr/bin/env bash
# Run every stage, --fast (no Docker), or named stages in order. CI calls the same stages.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
source eng/native-reports.sh

# These assignments are also the inputs each native stage executes.
unit_projects=(tests/ClaimCore.Tests/ClaimCore.Tests.fsproj)
postgres_projects=(tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj)
web_projects=(tests/ClaimCore.WebTests/ClaimCore.WebTests.fsproj)
acceptance_projects=(tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj)
assigned_projects=("${unit_projects[@]}" "${postgres_projects[@]}" "${web_projects[@]}" "${acceptance_projects[@]}")

for project in "${assigned_projects[@]}"; do
  if [[ ! -f "${project}" ]]; then
    echo "Assigned test project is absent: ${project}" >&2
    exit 64
  fi
done
# Directory.Build.props defines test projects by the Tests suffix; TestSupport is a library.
for project in tests/*/*Tests.fsproj; do
  assignments=0
  for assigned in "${assigned_projects[@]}"; do
    if [[ "${project}" == "${assigned}" ]]; then assignments=$((assignments + 1)); fi
  done
  if [[ "${assignments}" -ne 1 ]]; then
    echo "Test project must be assigned to exactly one stage: ${project} (${assignments} assignments)" >&2
    exit 64
  fi
done

if [[ "${1-}" == growth-compare ]]; then
  if [[ $# -ne 2 ]]; then
    echo "Use growth-compare REF as the sole stage." >&2
    exit 64
  fi
  exec python3 eng/growth-compare.py "$2"
fi

if [[ $# -eq 0 ]]; then
  stages=(lint unit frontend workflows integration acceptance)
elif [[ "$1" == --fast ]]; then
  if [[ $# -ne 1 ]]; then
    echo "--fast must be the sole argument" >&2
    exit 64
  fi
  stages=(lint unit frontend)
else
  stages=("$@")
fi
# Validate all arguments before restoring inputs or running a partial gate.
for stage in "${stages[@]}"; do
  case "${stage}" in
    lint | unit | frontend | workflows | integration | postgres | web | browsers | acceptance | growth) ;;
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
    integration | postgres | growth) validate_postgres_selection ;;
    *) : ;;
  esac
done

# Check the daemon once before any restore, build or browser download. Each fixture still
# validates its pinned images and owns its isolated resources.
for stage in "${stages[@]}"; do
  case "${stage}" in
    workflows | integration | postgres | web | acceptance | growth)
      if ! command -v docker >/dev/null 2>&1 || ! docker info --format '{{.ServerVersion}}' >/dev/null; then
        echo "Selected stages require a running Docker daemon; start Docker and retry." >&2
        exit 69
      fi
      break ;;
    *) : ;;
  esac
done

# Discovery/source-membership guards and MTP enforce complete, nonempty runs.
tests() { # <project> [configuration] [timeout] [settings]
  local report_directory report_root
  report_root="${CLAIMCORE_TEST_REPORT_DIRECTORY:-${PWD}/artifacts/verification}"
  mkdir -p "${report_root}"
  report_directory="$(mktemp -d "${report_root}/native.XXXXXXXX")"
  touch "${report_directory}/.claimcore-native-active"
  local result=0
  local test_command=(dotnet test --project "$1" --configuration "${2:-Release}" --no-build --no-restore
    --zero-tests-policy=strict --timeout="${3:-30m}"
    --report-trx --report-trx-filename="${report_directory}/results.trx")
  if [[ -n "${5:-}" ]]; then test_command+=(--filter "$5"); fi
  CLAIMCORE_TEST_REPORT_DIRECTORY="${report_directory}" "${test_command[@]}" \
    -- --settings="${PWD}/${4:-eng/expecto.runsettings}" || result=$?
  rm "${report_directory}/.claimcore-native-active"
  touch "${report_directory}/.claimcore-native-complete"
  prune_native_reports "${report_root}"
  return "${result}"
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
  restore_node
  npm --prefix web run typecheck
}

unit() {
  bash eng/test-native-reports.sh
  python3 eng/test-growth-comparison.py
  build_native
  local project
  for project in "${unit_projects[@]}"; do
    dotnet build "${project}" --configuration Debug --no-restore -p:Optimize=false
    tests "${project}" Debug 15m
  done
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
  local script
  local -a scripts=()
  for script in eng/*.sh deployment/*.sh; do
    if [[ -f "${script}" ]]; then scripts+=("${script}"); fi
  done
  shellcheck "${scripts[@]}"
  docker run --rm --label claimcore.workflow-check=actionlint -v "${PWD}:/repo:ro" -w /repo \
    rhysd/actionlint@sha256:b1934ee5f1c509618f2508e6eb47ee0d3520686341fec936f3b79331f9315667 -color
}

integration() {
  postgres
  web
}

postgres() {
  local project
  for project in "${postgres_projects[@]}"; do
    build_native "${project}"
    tests "${project}" Release 30m eng/expecto.integration.runsettings 'FullyQualifiedName!~synthetic write contention and verifier growth qualification'
  done
}

growth() {
  local project
  for project in "${postgres_projects[@]}"; do
    build_native "${project}"
    tests "${project}" Release 110m eng/expecto.integration.runsettings 'FullyQualifiedName~synthetic write contention and verifier growth qualification'
  done
}

web() {
  local project
  for project in "${web_projects[@]}"; do
    build_native "${project}"
    tests "${project}" Release 20m
  done
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
  # Every Chromium launch is headless; CLI PKCE also needs its shell for other engines.
  restore_node
  local platform
  platform="$(uname -s)"
  if [[ "${platform}" == Linux ]]; then
    npm --prefix web exec -- playwright install --with-deps --only-shell "${engines[@]}"
  else
    npm --prefix web exec -- playwright install --only-shell "${engines[@]}"
  fi
}

acceptance() {
  build_native
  build_frontend
  # One native case sequences every selected engine and stops before starting a later engine after a failure.
  local project
  for project in "${acceptance_projects[@]}"; do
    tests "${project}" Release 40m
  done
}

for stage in "${stages[@]}"; do
  "${stage}"
done
platform="$(uname -s)"
printf 'Stages passed on %s: %s\n' "${platform}" "${stages[*]}"

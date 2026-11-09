#!/usr/bin/env bash
# Run every stage, --fast (no Docker), or named stages in order. CI calls the same stages.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

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

if [[ $# -eq 0 ]]; then
  stages=(lint unit frontend workflows advisories integration acceptance)
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
    lint | unit | frontend | workflows | advisories | integration | postgres | web | browsers | acceptance | acceptance-native | acceptance-browser) ;;
    *) echo "unknown stage: ${stage}" >&2; exit 64 ;;
  esac
done


# Check the daemon once before any restore, build or browser download. Each fixture still
# validates its pinned images and owns its isolated resources.
for stage in "${stages[@]}"; do
  case "${stage}" in
    workflows | integration | postgres | web | acceptance | acceptance-native | acceptance-browser)
      if ! command -v docker >/dev/null 2>&1 || ! docker info --format '{{.ServerVersion}}' >/dev/null; then
        echo "Selected stages require a running Docker daemon; start Docker and retry." >&2
        exit 69
      fi
      break ;;
    *) : ;;
  esac
done

# MTP rejects empty runs; review discovery and skipped outcomes for the selected scope.
tests() { # <project> [configuration] [timeout] [settings]
  local report_directory="${PWD}/artifacts/verification/${stage}"
  mkdir -p "${report_directory}"
  local test_command=(dotnet test --project "$1" --configuration "${2:-Release}" --no-build --no-restore
    --zero-tests-policy=strict --timeout="${3:-30m}"
    --report-trx --report-trx-filename="${report_directory}/results.trx")
  local filter="${5:-}"
  if [[ -n "${filter}" ]]; then test_command+=(--filter "${filter}"); fi
  CLAIMCORE_TEST_REPORT_DIRECTORY="${report_directory}" "${test_command[@]}" \
    -- --settings="${PWD}/${4:-eng/expecto.runsettings}"
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
  dotnet tool restore
  dotnet fantomas --check src tests eng
}

advisories() {
  restore_native
  restore_node
  local report
  report="$(mktemp)"
  local result=0
  dotnet list ClaimCore.slnx package --vulnerable --include-transitive --format json > "${report}" || result=$?
  if [[ ${result} -eq 0 ]]; then
    node -e '
      const fs = require("node:fs");
      const report = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
      if (report.problems?.length) {
        console.error(report.problems);
        process.exit(1);
      }
      let findings = 0;
      for (const project of report.projects ?? []) {
        for (const framework of project.frameworks ?? []) {
          for (const pkg of [...(framework.topLevelPackages ?? []), ...(framework.transitivePackages ?? [])]) {
            for (const vulnerability of pkg.vulnerabilities ?? []) {
              console.error(project.path, pkg.id, vulnerability.severity, vulnerability.advisoryurl);
              findings++;
            }
          }
        }
      }
      process.exit(findings ? 1 : 0);
    ' "${report}" || result=$?
  fi
  rm -f "${report}"
  npm --prefix web audit --audit-level=low || result=$?
  return "${result}"
}

unit() {
  local stage=unit project
  for project in "${unit_projects[@]}"; do
    restore_native "${project}"
    dotnet build "${project}" --configuration Debug --no-restore -p:Optimize=false
    tests "${project}" Debug 15m
  done
}

frontend() { # Node only: no generated contract prerequisite or .NET process.
  restore_node
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
  local stage=postgres project
  for project in "${postgres_projects[@]}"; do
    build_native "${project}"
    tests "${project}" Release 30m eng/expecto.integration.runsettings 'FullyQualifiedName!~opt-in read timing probe'
  done
}

web() {
  local stage=web project
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

acceptance-native() {
  build_native "${acceptance_projects[0]}"
  build_native src/ClaimCore.Database/ClaimCore.Database.fsproj
  build_frontend
  local stage=acceptance-native project
  for project in "${acceptance_projects[@]}"; do
    tests "${project}" Release 40m eng/expecto.acceptance.runsettings 'FullyQualifiedName!~published browser acceptance'
  done
}

acceptance-browser() {
  build_native "${acceptance_projects[0]}"
  build_native src/ClaimCore.Database/ClaimCore.Database.fsproj
  build_frontend
  # One native case sequences selected engines with their own fixture/bootstrap/readback.
  local stage=acceptance-browser project browser_timeout=45m
  if [[ -z "${CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE+x}" ]]; then browser_timeout=130m; fi
  for project in "${acceptance_projects[@]}"; do
    tests "${project}" Release "${browser_timeout}" eng/expecto.acceptance.runsettings 'FullyQualifiedName~published browser acceptance'
  done
}

acceptance() {
  acceptance-native
  acceptance-browser
}

for stage in "${stages[@]}"; do
  "${stage}"
done
platform="$(uname -s)"
printf 'Stages passed on %s: %s\n' "${platform}" "${stages[*]}"

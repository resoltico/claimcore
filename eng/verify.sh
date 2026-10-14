#!/usr/bin/env bash
# The complete local gate, in the order CI runs it. Every line is a command a contributor can run
# alone; this file only fixes the order and stops at the first failure.
#
#   bash eng/verify.sh            everything this machine can run
#   bash eng/verify.sh --fast     build, lint and tests that need no Docker
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

fast=false
[[ "${1:-}" == "--fast" ]] && fast=true

# Raise a floor when a suite grows; never lower one to make a run pass.
tests() { # <project> <minimum-expected-tests> [configuration] [timeout]
  dotnet test --project "$1" --configuration "${3:-Release}" --no-build --no-restore \
    --minimum-expected-tests="$2" --zero-tests-policy=strict --timeout="${4:-30m}" \
    -- --settings="${PWD}/eng/expecto.runsettings"
}

dotnet restore ClaimCore.slnx --locked-mode
dotnet tool restore
npm --prefix web ci
npm --prefix web run contract:generate
dotnet build ClaimCore.slnx --configuration Release --no-restore

dotnet fantomas --check src tests eng
bash eng/Check-FSharpLint.sh
npm --prefix web run format:check
npm --prefix web run lint
npm --prefix web run lint:styles
npm --prefix web run typecheck

tests tests/ClaimCore.Tests/ClaimCore.Tests.fsproj 240
dotnet build tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj \
  --configuration Debug --no-restore -p:Optimize=false
tests tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj 20 Debug
npm --prefix web run test:unit

${fast} && { echo "fast gate passed (Docker-backed suites not run)"; exit 0; }

tests tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj 70
tests tests/ClaimCore.WebTests/ClaimCore.WebTests.fsproj 60
npm --prefix web run build
# Published acceptance owns the fresh service and runs all three browser engines.
tests tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj 10 Release 40m

platform="$(uname -s)"
echo "complete gate passed on ${platform}"

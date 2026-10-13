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
tests() { # <project> <minimum-expected-tests> [configuration]
  dotnet test --project "$1" --configuration "${3:-Release}" --no-build --no-restore \
    --minimum-expected-tests="$2" --zero-tests-policy=strict --timeout=30m \
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

tests tests/ClaimCore.Tests/ClaimCore.Tests.fsproj SET-IN-WP15
dotnet build tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj \
  --configuration Debug --no-restore -p:Optimize=false
tests tests/ClaimCore.ArchitectureTests/ClaimCore.ArchitectureTests.fsproj SET-IN-WP15 Debug
npm --prefix web run test:unit

${fast} && { echo "fast gate passed (Docker-backed suites not run)"; exit 0; }

tests tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj SET-IN-WP15
tests tests/ClaimCore.WebTests/ClaimCore.WebTests.fsproj SET-IN-WP15
npm --prefix web run build
tests tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj SET-IN-WP15
npm --prefix web run test:e2e

platform="$(uname -s)"
echo "complete gate passed on ${platform}"

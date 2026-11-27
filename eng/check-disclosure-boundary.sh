#!/usr/bin/env bash
# A protected application producer must obtain a release before returning its success.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
work="$(mktemp -d)"
trap 'rm -rf -- "${work}"' EXIT
bash eng/source-archive.sh "${work}/source.tar.gz"
mkdir "${work}/checkout"
tar -xzf "${work}/source.tar.gz" -C "${work}/checkout"
cd "${work}/checkout"

project=src/ClaimCore.Server/ClaimCore.Server.fsproj
dotnet restore "${project}" --locked-mode
dotnet build "${project}" --no-restore

producer=src/ClaimCore.Application/Cases/CaseDetailQueries.fs
cp "${producer}" "${work}/case-detail.fs"
python3 - "${producer}" <<'PY'
from pathlib import Path
import sys

source = Path(sys.argv[1])
text = source.read_text()
original = "return! DisclosureReads.complete services token result"
assert text.count(original) == 1, "Case detail release point must be identified uniquely"
bypass = """match result with
                | Ok(outcome, _) -> return outcome
                | Error failure -> return QueryResults.failure failure"""
source.write_text(text.replace(original, bypass))
PY

if dotnet build "${project}" --no-restore >"${work}/negative.log" 2>&1; then
  printf 'An unaudited case detail response compiled.\n' >&2
  exit 1
fi
if ! grep -Eq 'CaseDetailQueries\.fs.*FS(0001|0066|0193).*Released<Outcome' "${work}/negative.log"; then
  cat "${work}/negative.log" >&2
  printf 'Build failure did not identify the missing case detail release.\n' >&2
  exit 1
fi
printf 'The server build rejected the unaudited case detail response.\n'

cp "${work}/case-detail.fs" "${producer}"
digest=src/ClaimCore.Application/DigestDispatch.fs
cp "${digest}" "${work}/digest.fs"
python3 - <<'PYTHON'
from pathlib import Path

source = Path("src/ClaimCore.Application/DigestDispatch.fs")
text = source.read_text()
original = "send released sendDeadline.Token"
assert text.count(original) == 1, "Digest release point must be identified uniquely"
source.write_text(text.replace(original, "send (recipient, counts) sendDeadline.Token"))
PYTHON
if dotnet build "${project}" --no-restore >"${work}/digest-negative.log" 2>&1; then
  printf 'An unaudited digest candidate compiled.\n' >&2
  exit 1
fi
if ! grep -Eq 'DigestDispatch\.fs.*FS(0001|0066|0193).*Released<' "${work}/digest-negative.log"; then
  cat "${work}/digest-negative.log" >&2
  printf 'Build failure did not identify the missing digest release.\n' >&2
  exit 1
fi
printf 'The server build rejected the unaudited digest candidate.\n'

cp "${work}/digest.fs" "${digest}"
python3 - <<'PYTHON'
from pathlib import Path

source = Path("src/ClaimCore.Server/ApiRoutes.fs")
text = source.read_text()
original = "Service.Disclosure.unavailableLookup"
assert text.count(original) == 1, "Operation lookup fallback must be identified uniquely"
source.write_text(text.replace(original, "Api.operationGetUnavailable"))
PYTHON
if dotnet build "${project}" --no-restore >"${work}/fallback-negative.log" 2>&1; then
  printf 'An uncertified operation lookup fallback compiled.\n' >&2
  exit 1
fi
if ! grep -Eq 'ApiRoutes\.fs.*FS(0001|0066|0193).*Released<' "${work}/fallback-negative.log"; then
  cat "${work}/fallback-negative.log" >&2
  printf 'Build failure did not identify the uncertified operation lookup fallback.\n' >&2
  exit 1
fi
printf 'The server build rejected the uncertified operation lookup fallback.\n'

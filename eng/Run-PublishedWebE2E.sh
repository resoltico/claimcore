#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
publish_dir="${1:?Pass the published Web directory.}"
database_dir="${2:?Pass the published Database directory.}"
engine_scope="${3:-all}"
if [[ "$engine_scope" == all && -n "${CLAIMCORE_TEST_RUN_LABEL:-}" ]]; then
  echo "All-engine qualification requires separate generated browser test-run labels." >&2
  exit 64
fi
web_dll="$publish_dir/ClaimCore.Web.dll"
database_dll="$database_dir/ClaimCore.Database.dll"
expected_node="$(tr -d '\r\n' <"$repo_root/.node-version")"
expected_npm="$(jq --raw-output '.engines.npm' "$repo_root/web/package.json")"
runtime_prefix=()

if [[ "$(node --version 2>/dev/null || true)" != "v$expected_node" ]] ||
  [[ "$(npm --version 2>/dev/null || true)" != "$expected_npm" ]]; then
  if ! command -v mise >/dev/null 2>&1; then
    echo "Browser qualification requires Node $expected_node and npm $expected_npm." >&2
    exit 64
  fi
  runtime_prefix=(mise exec "node@$expected_node" --)
fi

if [[ "$("${runtime_prefix[@]}" node --version)" != "v$expected_node" ]] ||
  [[ "$("${runtime_prefix[@]}" npm --version)" != "$expected_npm" ]]; then
  echo "Browser qualification could not select Node $expected_node and npm $expected_npm." >&2
  exit 64
fi

if [[ ! -f "$web_dll" || ! -f "$database_dll" ]]; then
  echo "Published Web and Database assemblies are required." >&2
  exit 64
fi

run_engine() (
engine="$1"
web_port="$2"
test_run_label="${CLAIMCORE_TEST_RUN_LABEL:-claimcore-browser-${engine}-$$-$RANDOM}"
if [[ ! "$test_run_label" =~ ^claimcore-browser-[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] ||
  [[ "${#test_run_label}" -gt 100 ]]; then
  echo "CLAIMCORE_TEST_RUN_LABEL must be a bounded claimcore-browser label." >&2
  exit 64
fi
created_state_dir="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-web-e2e-${engine}.XXXXXX")"
if ! state_dir="$(cd -P "$created_state_dir" && pwd -P)"; then
  rmdir "$created_state_dir" 2>/dev/null || true
  echo "The isolated browser state directory could not be resolved." >&2
  exit 64
fi
container_name="claimcore-web-e2e-${engine}-$RANDOM-$RANDOM"
host_pid=""
credential_file=""
browser_artifacts="$repo_root/artifacts/browser"
owner_secret_file="$state_dir/owner.secret"
app_secret_file="$state_dir/application.secret"
claimant_canary_file="$state_dir/claimant.canary"

scan_sensitive_output() {
  local bootstrap_file secret_file
  local -a secret_files=("$owner_secret_file" "$app_secret_file" "$claimant_canary_file")

  if [[ -d "$state_dir/web-state" ]]; then
    bootstrap_file="$(find "$state_dir/web-state" -maxdepth 1 -type f \
      -name 'bootstrap-credential-*' -print -quit 2>/dev/null || true)"
    if [[ -n "$bootstrap_file" ]]; then secret_files+=("$bootstrap_file"); fi
  fi

  for secret_file in "${secret_files[@]}"; do
    if [[ -f "$secret_file" ]] && ! pwsh -NoProfile -File eng/Assert-NoSensitiveOutput.ps1 \
      -ScanRoot "$browser_artifacts" -SecretFile "$secret_file" >/dev/null 2>&1; then
      return 1
    fi
  done
}

cleanup() {
  local status=$?
  trap - EXIT
  set +e
  if [[ -n "$host_pid" ]]; then
    kill "$host_pid" 2>/dev/null || true
    wait "$host_pid" 2>/dev/null || true
  fi
  if ! scan_sensitive_output; then
    echo "Sensitive-output scanning rejected the browser diagnostics." >&2
    status=1
  fi
  if ! bash "$repo_root/eng/Remove-LabeledTestContainers.sh" "$test_run_label"; then
    echo "Exact-label browser container cleanup failed." >&2
    status=1
  fi
  if ! rm -rf -- "$state_dir"; then
    echo "Private browser state cleanup failed." >&2
    status=1
  fi
  exit "$status"
}
trap cleanup EXIT

host_failure_summary() {
  if rg --quiet "could not open the configured application runtime" "$state_dir/web-host.log"; then
    echo "The published Web host could not open its application runtime." >&2
  elif rg --quiet "asset manifest" "$state_dir/web-host.log"; then
    echo "The published Web host rejected its asset manifest." >&2
  elif rg --quiet "certificate" "$state_dir/web-host.log"; then
    echo "The published Web host rejected its configured certificate." >&2
  else
    echo "The published Web host exited before readiness." >&2
  fi
}

database_failure_summary() {
  local phase="$1" report="$2" diagnostic
  diagnostic="$(jq -r '.diagnostic.id // empty' "$report" 2>/dev/null || true)"
  if [[ "$diagnostic" =~ ^DB_[A-Z_]+$ ]]; then
    printf 'The published database %s failed: %s.\n' "$phase" "$diagnostic" >&2
  else
    printf 'The published database %s failed.\n' "$phase" >&2
  fi
}

require_forbidden_probe() {
  local probe="$1"
  local actual="$2"
  if [[ "$actual" != "403" ]]; then
    printf 'The published Web host did not reject the %s probe.\n' "$probe" >&2
    return 1
  fi
}

mkdir -p "$browser_artifacts"
umask 077
owner_secret="$(openssl rand -hex 32)"
app_secret="$(openssl rand -hex 32)"
printf '%s\n' "$owner_secret" >"$owner_secret_file"
printf '%s\n' "$app_secret" >"$app_secret_file"
printf '%s\n' 'Synthetic amended claimant' >"$claimant_canary_file"
image="$(jq --raw-output .containerImage db/postgresql-baseline.json)"
container_environment="$state_dir/postgres.env"
printf 'POSTGRES_DB=claimcore\nPOSTGRES_PASSWORD=%s\n' "$owner_secret" >"$container_environment"
docker run --detach --rm --name "$container_name" \
  --label "org.claimcore.test-run=$test_run_label" \
  --env-file "$container_environment" \
  --publish 127.0.0.1::5432 \
  "$image" >/dev/null

echo "Started isolated PostgreSQL for $engine browser qualification."

ready=""
for _ in $(seq 1 60); do
  ready="$(docker exec "$container_name" bash -c \
    'PGPASSWORD="$POSTGRES_PASSWORD" psql --host=127.0.0.1 --username=postgres --dbname=claimcore --tuples-only --no-align --command="SELECT 1"' \
    2>/dev/null || true)"
  if [[ "$ready" == "1" ]]; then
    break
  fi
  sleep 1
done

if [[ "$ready" != "1" ]]; then
  printf '%s\n' 'The isolated PostgreSQL service did not become ready.' >&2
  exit 1
fi

port="$(docker port "$container_name" 5432/tcp | sed -E 's/.*:([0-9]+)$/\1/')"
if ! docker exec --interactive "$container_name" psql --username postgres --dbname claimcore \
  --set ON_ERROR_STOP=1 >/dev/null 2>&1 <<SQL
CREATE ROLE claimcore_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
  NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '$app_secret';
REVOKE ALL ON DATABASE claimcore FROM PUBLIC;
GRANT CONNECT ON DATABASE claimcore TO claimcore_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
then
  echo "The isolated PostgreSQL roles could not be provisioned." >&2
  exit 1
fi

owner_connection="$state_dir/owner.connection"
app_connection="$state_dir/application.connection"
printf 'Host=127.0.0.1;Port=%s;Database=claimcore;Username=postgres;Password=%s\n' "$port" "$owner_secret" >"$owner_connection"
printf 'Host=127.0.0.1;Port=%s;Database=claimcore;Username=claimcore_app;Password=%s\n' "$port" "$app_secret" >"$app_connection"

echo "Initializing the fresh baseline in the isolated synthetic database."
if ! CLAIMCORE_ADMIN_CONNECTION_FILE="$owner_connection" \
  dotnet "$database_dll" initialize Etc/UTC >"$state_dir/database-init.out" 2>"$state_dir/database-init.err"; then
  database_failure_summary initialization "$state_dir/database-init.err"
  exit 1
fi
if ! CLAIMCORE_ADMIN_CONNECTION_FILE="$owner_connection" \
  dotnet "$database_dll" verify >"$state_dir/database-verify.out" 2>"$state_dir/database-verify.err"; then
  database_failure_summary verification "$state_dir/database-verify.err"
  exit 1
fi
certificate="$state_dir/web.pfx"
certificate_key="$state_dir/web.key"
certificate_pem="$state_dir/web.pem"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
  -subj /CN=localhost \
  -addext 'subjectAltName=DNS:localhost' \
  -keyout "$certificate_key" \
  -out "$certificate_pem" >/dev/null 2>&1
openssl pkcs12 -export -out "$certificate" -inkey "$certificate_key" -in "$certificate_pem" \
  -passout pass: >/dev/null 2>&1
rm -f "$certificate_key" "$certificate_pem"

CLAIMCORE_CONNECTION_FILE="$app_connection" \
CLAIMCORE_WEB_CERTIFICATE_PATH="$certificate" \
CLAIMCORE_WEB_ORIGIN="https://localhost:$web_port" \
CLAIMCORE_WEB_STATE_DIR="$state_dir/web-state" \
  dotnet "$web_dll" >"$state_dir/web-host.log" 2>&1 &
host_pid=$!

origin="https://localhost:$web_port"
echo "Started the $engine published local HTTPS host."

for _ in $(seq 1 30); do
  if curl --fail --silent --insecure "$origin/health/live" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$host_pid" 2>/dev/null; then
    host_failure_summary
    exit 1
  fi
  sleep 1
done

if ! curl --fail --silent --insecure "$origin/health/live" >/dev/null 2>&1; then
  printf '%s\n' 'The published Web host did not become ready.' >&2
  exit 1
fi

status="$(curl --silent --insecure --output /dev/null --write-out '%{http_code}' \
  --header 'Host: example.invalid' "$origin/health/live")"
require_forbidden_probe "foreign Host" "$status"
status="$(curl --silent --insecure --output /dev/null --write-out '%{http_code}' \
  --header "Origin: https://localhost:1" --header 'Content-Type: application/json' \
  --data '{"limit":1}' "$origin/api/v2/cases/list")"
require_forbidden_probe "foreign Origin" "$status"
status="$(curl --silent --insecure --output /dev/null --write-out '%{http_code}' \
  --header "Origin: $origin" --header 'Sec-Fetch-Site: cross-site' \
  --header 'Content-Type: application/json' --data '{"limit":1}' "$origin/api/v2/cases/list")"
require_forbidden_probe "cross-site fetch metadata" "$status"

credential_file="$(find "$state_dir/web-state" -maxdepth 1 -type f -name 'bootstrap-credential-*' -print -quit)"
if [[ ! -f "$credential_file" ]]; then
  echo "The published host did not create a bootstrap credential file." >&2
  exit 1
fi

echo "Running $engine against its isolated published same-origin host."
progress_file="$state_dir/progress"

if ! CLAIMCORE_WEB_BASE_URL="$origin" \
  CLAIMCORE_WEB_BOOTSTRAP_CREDENTIAL_FILE="$credential_file" \
  CLAIMCORE_WEB_E2E_ENGINE="$engine" \
  CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR="$state_dir/playwright-private" \
  CLAIMCORE_WEB_E2E_PROGRESS_FILE="$progress_file" \
  "${runtime_prefix[@]}" npm --prefix "$repo_root/web" run test:e2e -- --project "$engine" \
    >"$state_dir/playwright.log" 2>&1; then
  stage="not-started"
  if [[ -f "$progress_file" ]]; then
    stage="$(tr -cd 'a-z0-9-\n' <"$progress_file" | head -n 1)"
  fi
  printf 'The %s browser qualification stopped after safe stage: %s.\n' "$engine" "$stage" >&2
  exit 1
fi

cleanup
)

run_all_engines() {
  local status=0
  local chromium_pid firefox_pid webkit_pid

  run_engine chromium 5443 &
  chromium_pid=$!
  run_engine firefox 5444 &
  firefox_pid=$!
  run_engine webkit 5445 &
  webkit_pid=$!

  if ! wait "$chromium_pid"; then status=1; fi
  if ! wait "$firefox_pid"; then status=1; fi
  if ! wait "$webkit_pid"; then status=1; fi
  return "$status"
}

case "$engine_scope" in
  all) run_all_engines ;;
  chromium) run_engine chromium 5443 ;;
  firefox) run_engine firefox 5444 ;;
  webkit) run_engine webkit 5445 ;;
  *)
    printf '%s\n' 'Use browser engine all, chromium, firefox, or webkit.' >&2
    exit 64
    ;;
esac

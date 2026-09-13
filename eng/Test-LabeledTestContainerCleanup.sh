#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
helper="$repo_root/eng/Remove-LabeledTestContainers.sh"
temporary="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-container-cleanup.XXXXXX")"
nonce="$(printf '%s' "$$-$RANDOM-$(date -u +%s)" | shasum -a 256 | cut -c1-12)"
run_label="claimcore-integration-cleanup-$nonce"
other_label="claimcore-integration-other-$nonce"
target_name="claimcore-cleanup-target-$nonce"
other_name="claimcore-cleanup-other-$nonce"
unlabeled_name="claimcore-cleanup-unlabeled-$nonce"
sentinel="claimcore-cleanup-sentinel-$nonce"
probe_label="org.claimcore.cleanup-probe=$nonce"
live_started=0
pgdata_volume=""

cleanup() {
  local status=$?
  local name owner
  trap - EXIT
  set +e
  if [[ "$live_started" == 1 ]]; then
    for name in "$target_name" "$other_name" "$unlabeled_name"; do
      owner="$(docker container inspect \
        --format '{{ index .Config.Labels "org.claimcore.cleanup-probe" }}' "$name" 2>/dev/null)"
      if [[ "$owner" == "$nonce" ]]; then
        docker container rm --force --volumes "$name" >/dev/null 2>&1 || status=1
      elif docker container inspect "$name" >/dev/null 2>&1; then
        status=1
      fi
    done
    if [[ -n "$pgdata_volume" ]]; then
      if [[ "$pgdata_volume" =~ ^[a-f0-9]{64}$ ]]; then
        if docker volume inspect "$pgdata_volume" >/dev/null 2>&1; then
          docker volume rm "$pgdata_volume" >/dev/null 2>&1 || status=1
        fi
      else
        status=1
      fi
    fi
    owner="$(docker volume inspect \
      --format '{{ index .Labels "org.claimcore.cleanup-probe" }}' "$sentinel" 2>/dev/null)"
    if [[ "$owner" == "$nonce" ]]; then
      docker volume rm "$sentinel" >/dev/null 2>&1 || status=1
    elif docker volume inspect "$sentinel" >/dev/null 2>&1; then
      status=1
    fi
    docker info --format '{{.ID}}' >/dev/null 2>&1 || status=1
  fi
  rm -rf "$temporary" || status=1
  exit "$status"
}
trap cleanup EXIT

mkdir "$temporary/mock"
cat > "$temporary/mock/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$MOCK_CALLS"
[[ "${1:-}" == container ]] || exit 50
shift
case "${1:-}" in
  ls)
    if [[ "${5:-}" == --filter ]]; then
      [[ "$MOCK_MODE" != filter-fail ]] || exit 51
      id="${6#id=}"
      if [[ "$id" == "$MATCH_ID" ]]; then
        [[ "$MOCK_MODE" == inspect-race || "$MOCK_MODE" == rm-race ]] || printf '%s\n' "$MATCH_ID"
      elif [[ "$id" == "$OTHER_ID" ]]; then
        printf '%s\n' "$OTHER_ID"
      fi
    else
      [[ "$MOCK_MODE" != list-fail ]] || exit 52
      [[ "$MOCK_MODE" == no-match ]] || printf '%s\n' "$MATCH_ID"
      printf '%s\n' "$OTHER_ID"
    fi
    ;;
  inspect)
    id="${4:-}"
    if [[ "$id" == "$MATCH_ID" ]]; then
      [[ "$MOCK_MODE" != inspect-race && "$MOCK_MODE" != inspect-fail &&
        "$MOCK_MODE" != filter-fail ]] || exit 53
      printf '%s\n' "$RUN_LABEL"
    elif [[ "$id" == "$OTHER_ID" ]]; then
      printf '%s\n' "$OTHER_LABEL"
    else
      exit 54
    fi
    ;;
  rm)
    [[ "$MOCK_MODE" != rm-race && "$MOCK_MODE" != rm-fail ]] || exit 55
    ;;
  *) exit 56 ;;
esac
MOCK
chmod +x "$temporary/mock/docker"

match_id="$(printf 'a%.0s' {1..64})"
other_id="$(printf 'b%.0s' {1..64})"
calls="$temporary/mock-calls"

run_mock() {
  local mode="$1" label="$2"
  : > "$calls"
  if PATH="$temporary/mock:$PATH" MOCK_MODE="$mode" MOCK_CALLS="$calls" \
    MATCH_ID="$match_id" OTHER_ID="$other_id" RUN_LABEL="$run_label" \
    OTHER_LABEL="$other_label" bash "$helper" "$label" >/dev/null 2>&1; then
    mock_status=0
  else
    mock_status=$?
  fi
}

expect_mock() {
  local mode="$1" label="$2" expected_status="$3" expected_removals="$4"
  local removals
  run_mock "$mode" "$label"
  if [[ "$expected_status" == success && "$mock_status" != 0 ]] ||
    [[ "$expected_status" == failure && "$mock_status" == 0 ]]; then
    printf 'Scoped Docker cleanup mock %s had the wrong status.\n' "$mode" >&2
    exit 1
  fi
  removals="$(awk '$1 == "container" && $2 == "rm" { count++ } END { print count+0 }' "$calls")"
  if [[ "$removals" != "$expected_removals" ]]; then
    printf 'Scoped Docker cleanup mock %s removed the wrong number of containers.\n' "$mode" >&2
    exit 1
  fi
  if [[ "$removals" == 1 ]] &&
    ! grep -Fq -- \
      "container rm --force --volumes $match_id" "$calls"; then
    printf 'Scoped Docker cleanup mock %s did not use exact-ID anonymous-volume removal.\n' "$mode" >&2
    exit 1
  fi
}

: > "$calls"
if PATH="$temporary/mock:$PATH" MOCK_MODE=normal MOCK_CALLS="$calls" \
  bash "$helper" >/dev/null 2>&1; then
  no_argument_status=0
else
  no_argument_status=$?
fi
if [[ "$no_argument_status" != 64 || -s "$calls" ]]; then
  echo 'Scoped Docker cleanup did not reject a missing label before Docker access.' >&2
  exit 1
fi

for invalid in '' 'other-project-run' 'claimcore-browser-' \
  'claimcore-browser-bad/value' "claimcore-browser-$(printf 'x%.0s' {1..101})"; do
  run_mock normal "$invalid"
  if [[ "$mock_status" == 0 || -s "$calls" ]]; then
    echo 'Scoped Docker cleanup accepted an invalid label or invoked Docker for it.' >&2
    exit 1
  fi
done
expect_mock no-match "$run_label" success 0
expect_mock normal "$run_label" success 1
expect_mock list-fail "$run_label" failure 0
expect_mock inspect-race "$run_label" success 0
expect_mock inspect-fail "$run_label" failure 0
expect_mock filter-fail "$run_label" failure 0
expect_mock rm-race "$run_label" failure 1
expect_mock rm-fail "$run_label" failure 1

browser_probe="$temporary/browser-probe"
mkdir "$browser_probe"
: > "$calls"
if PATH="$temporary/mock:$PATH" MOCK_MODE=normal MOCK_CALLS="$calls" \
  CLAIMCORE_TEST_RUN_LABEL="claimcore-browser-early-$nonce" TMPDIR="$browser_probe" \
  bash "$repo_root/eng/Run-PublishedWebE2E.sh" \
    "$temporary/missing-web" "$temporary/missing-database" all >/dev/null 2>&1; then
  browser_status=0
else
  browser_status=$?
fi
if [[ "$browser_status" != 64 || -s "$calls" ]] ||
  [[ -n "$(find "$browser_probe" -mindepth 1 -print -quit)" ]]; then
  echo 'All-engine browser admission did not reject a shared test-run label before side effects.' >&2
  exit 1
fi

image="$(jq --exit-status --raw-output '.containerImage | select(type == "string" and length > 0)' \
  "$repo_root/db/postgresql-baseline.json")"
if ! docker image inspect "$image" >/dev/null 2>&1; then
  docker pull "$image" >/dev/null
fi

for name in "$target_name" "$other_name" "$unlabeled_name"; do
  if docker container inspect "$name" >/dev/null 2>&1; then
    echo 'A synthetic Docker cleanup fixture name already exists.' >&2
    exit 1
  fi
done
if docker volume inspect "$sentinel" >/dev/null 2>&1; then
  echo 'A synthetic Docker cleanup sentinel volume already exists.' >&2
  exit 1
fi

live_started=1
docker volume create --label "$probe_label" "$sentinel" >/dev/null
target_id="$(docker container create --name "$target_name" \
  --label "org.claimcore.test-run=$run_label" --label "$probe_label" \
  --mount "type=volume,src=$sentinel,dst=/tmp/claimcore-sentinel" "$image")"
docker container create --name "$other_name" \
  --label "org.claimcore.test-run=$other_label" --label "$probe_label" "$image" >/dev/null
docker container create --name "$unlabeled_name" --label "$probe_label" "$image" >/dev/null

pgdata_volume="$(docker container inspect --format \
  '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql"}}{{.Name}}{{end}}{{end}}' \
  "$target_id")"
sentinel_mount="$(docker container inspect --format \
  '{{range .Mounts}}{{if eq .Destination "/tmp/claimcore-sentinel"}}{{.Name}}{{end}}{{end}}' \
  "$target_id")"
if [[ ! "$pgdata_volume" =~ ^[a-f0-9]{64}$ || "$pgdata_volume" == "$sentinel" ]] ||
  [[ "$sentinel_mount" != "$sentinel" ]] ||
  ! docker volume inspect "$pgdata_volume" >/dev/null 2>&1; then
  echo 'The synthetic PostgreSQL container lacks its expected anonymous or named volume.' >&2
  exit 1
fi

bash "$helper" "$run_label"
if docker container inspect "$target_name" >/dev/null 2>&1 ||
  docker volume inspect "$pgdata_volume" >/dev/null 2>&1 ||
  ! docker volume inspect "$sentinel" >/dev/null 2>&1 ||
  ! docker container inspect "$other_name" >/dev/null 2>&1 ||
  ! docker container inspect "$unlabeled_name" >/dev/null 2>&1; then
  echo 'Scoped Docker cleanup removed retained or decoy resources, or left anonymous PGDATA.' >&2
  exit 1
fi
docker info --format '{{.ID}}' >/dev/null
echo 'Scoped Docker cleanup negative controls and synthetic volume boundary passed.'

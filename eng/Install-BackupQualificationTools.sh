#!/usr/bin/env bash
set -euo pipefail

claimcore_os_id="$(awk -F= '$1 == "ID" {gsub(/"/, "", $2); print $2}' /etc/os-release)"
claimcore_os_codename="$(awk -F= '$1 == "VERSION_CODENAME" {gsub(/"/, "", $2); print $2}' /etc/os-release)"
[[ "$claimcore_os_id" == ubuntu && "$claimcore_os_codename" =~ ^(noble|resolute)$ ]]
claimcore_architecture="$(dpkg --print-architecture)"
case "$claimcore_architecture" in
  amd64) claimcore_age_sha256=cbe24006683f8eb669266162894b9a522a1af52f2665fbc63a4bb032ed26ac10 ;;
  arm64) claimcore_age_sha256=6b8dc4333c53a5a57c9e5834e3a48f92605d7154014cd07269ff3327db5d37f4 ;;
  *) printf 'Unsupported backup qualification architecture.\n' >&2; exit 1 ;;
esac

claimcore_root=()
if (( EUID != 0 )); then claimcore_root=(sudo); fi
"${claimcore_root[@]}" apt-get update -qq
"${claimcore_root[@]}" env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
  ca-certificates curl openssh-client python3 uuid-runtime

claimcore_key_file="$(mktemp "${TMPDIR:-/tmp}/claimcore-pgdg-key.XXXXXXXX")"
claimcore_age_archive="$(mktemp "${TMPDIR:-/tmp}/claimcore-age.XXXXXXXX")"
trap 'rm -f -- "$claimcore_key_file" "$claimcore_age_archive"' EXIT
curl --proto '=https' --tlsv1.2 --fail --silent --show-error \
  --output "$claimcore_key_file" https://www.postgresql.org/media/keys/ACCC4CF8.asc
printf '0144068502a1eddd2a0280ede10ef607d1ec592ce819940991203941564e8e76  %s\n' \
  "$claimcore_key_file" | sha256sum --check --status

"${claimcore_root[@]}" install -d -m 755 /usr/share/postgresql-common/pgdg
"${claimcore_root[@]}" install -m 644 "$claimcore_key_file" \
  /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc
printf 'Types: deb\nURIs: https://apt.postgresql.org/pub/repos/apt\nSuites: %s-pgdg\nArchitectures: %s\nComponents: main\nSigned-By: /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc\n' \
  "$claimcore_os_codename" "$claimcore_architecture" |
  "${claimcore_root[@]}" tee /etc/apt/sources.list.d/pgdg.sources >/dev/null
"${claimcore_root[@]}" apt-get update -qq
"${claimcore_root[@]}" env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
  postgresql-common
claimcore_cluster_config=/etc/postgresql-common/createcluster.conf
if grep -Eq '^[#[:space:]]*create_main_cluster[[:space:]]*=' "$claimcore_cluster_config"; then
  "${claimcore_root[@]}" sed -i -E \
    's/^[#[:space:]]*create_main_cluster[[:space:]]*=.*/create_main_cluster = false/' \
    "$claimcore_cluster_config"
else
  printf 'create_main_cluster = false\n' |
    "${claimcore_root[@]}" tee -a "$claimcore_cluster_config" >/dev/null
fi
grep -Eq '^create_main_cluster = false$' "$claimcore_cluster_config"
"${claimcore_root[@]}" env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
  postgresql-client-18 postgresql-18
if pg_lsclusters --no-header | grep -Eq '^18[[:space:]]'; then
  printf 'Qualification setup unexpectedly created a PostgreSQL 18 cluster.\n' >&2
  exit 1
fi

claimcore_age_root="${RUNNER_TEMP:-/tmp}/claimcore-age"
claimcore_age_bin="$claimcore_age_root/bin"
mkdir -p "$claimcore_age_bin"
chmod 700 "$claimcore_age_root" "$claimcore_age_bin"
curl --proto '=https' --tlsv1.2 --fail --silent --show-error --location \
  --output "$claimcore_age_archive" \
  "https://github.com/FiloSottile/age/releases/download/v1.3.2/age-v1.3.2-linux-${claimcore_architecture}.tar.gz"
printf '%s  %s\n' "$claimcore_age_sha256" "$claimcore_age_archive" |
  sha256sum --check --status
tar --strip-components=1 -xzf "$claimcore_age_archive" -C "$claimcore_age_bin" \
  age/age age/age-keygen
for claimcore_program in age age-keygen; do
  test -f "$claimcore_age_bin/$claimcore_program"
  test ! -L "$claimcore_age_bin/$claimcore_program"
  test "$("$claimcore_age_bin/$claimcore_program" --version)" = v1.3.2
done

claimcore_pg_bin=/usr/lib/postgresql/18/bin
for claimcore_program in psql pg_isready pg_verifybackup pg_waldump pg_basebackup; do
  if [[ ! -x "$claimcore_pg_bin/$claimcore_program" ]]; then
    printf 'PostgreSQL 18 qualification tool is missing: %s.\n' "$claimcore_program" >&2
    exit 1
  fi
  claimcore_version="$("$claimcore_pg_bin/$claimcore_program" --version)"
  printf '%s\n' "$claimcore_version"
  printf '%s\n' "$claimcore_version" |
    grep -Eq "^$claimcore_program \\(PostgreSQL\\) 18\\.6( \\((Ubuntu|Debian) 18\\.6-[0-9A-Za-z.+~_-]+\\))?$"
done
command -v ssh-keygen uuidgen >/dev/null
python3 -c 'import sys; assert sys.version_info >= (3, 12)'
printf 'PostgreSQL 18 and age qualification clients are available.\n'

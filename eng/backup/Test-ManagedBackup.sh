#!/usr/bin/env bash
set -euo pipefail
umask 077
trap 'printf "backup-test-stage=line-%s\n" "$LINENO" >&2' ERR

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
image="$(jq -er '.containerImage' "$repo_root/db/postgresql-baseline.json")"
export CLAIMCORE_BACKUP_TEST_IMAGE="$image"
pg_bin="${CLAIMCORE_PG_BIN:-/opt/homebrew/opt/libpq/bin}"
export PATH="$pg_bin:$PATH"
PYTHONDONTWRITEBYTECODE=1 python3 -B "$repo_root/eng/backup/Test-BackupFailureCategories.py" >/dev/null
scratch="$(mktemp -d "${TMPDIR:-/tmp}/claimcore-backup-test.XXXXXXXX")"
scratch="$(cd "$scratch" && pwd -P)"
chmod 700 "$scratch"
primary_container=""
witness_container=""
restore_container=""
checkpoint_signer_pid=""
checkpoint_socket_root=""

capture_synthetic() {
  python3 -B "$repo_root/eng/backup/Test-ManagedBackupPrimitives.py" \
    --config "$1" --primary-container "$primary_container" \
    --witness-container "$witness_container"
}

cleanup() {
  if [[ -n "$checkpoint_signer_pid" ]]; then
    kill "$checkpoint_signer_pid" >/dev/null 2>&1 || true
    wait "$checkpoint_signer_pid" >/dev/null 2>&1 || true
  fi
  for current in "$restore_container" "$witness_container" "$primary_container"; do
    if [[ -n "$current" ]]; then docker stop "$current" >/dev/null 2>&1 || true; fi
  done
  if [[ "$scratch" == */claimcore-backup-test.* && -d "$scratch" ]]; then
    rm -rf -- "$scratch"
  fi
  if [[ "$checkpoint_socket_root" == */cccp.* && -d "$checkpoint_socket_root" ]]; then
    rm -rf -- "$checkpoint_socket_root"
  fi
}
trap cleanup EXIT

secret="$(openssl rand -hex 24)"
primary_container="$(docker run --rm -d -P --label "claimcore.backup-test=$$" -e "POSTGRES_PASSWORD=$secret" "$image")"
witness_container="$(docker run --rm -d -P --label "claimcore.backup-test=$$" -e "POSTGRES_PASSWORD=$secret" "$image")"
primary_port="$(docker port "$primary_container" 5432/tcp | head -1 | sed -E 's/.*:([0-9]+)$/\1/')"
witness_port="$(docker port "$witness_container" 5432/tcp | head -1 | sed -E 's/.*:([0-9]+)$/\1/')"
[[ "$primary_port" =~ ^[0-9]+$ && "$witness_port" =~ ^[0-9]+$ ]]

for port in "$primary_port" "$witness_port"; do
  for ((attempt=0; attempt<60; attempt++)); do
    if pg_isready -q -h 127.0.0.1 -p "$port"; then break; fi
    sleep 1
  done
  pg_isready -q -h 127.0.0.1 -p "$port"
done
for current in "$primary_container" "$witness_container"; do
  docker exec -u postgres "$current" sh -c 'printf "\nhost replication postgres all scram-sha-256\n" >> "$PGDATA/pg_hba.conf"'
  docker exec -u postgres "$current" pg_ctl -D /var/lib/postgresql/18/docker reload >/dev/null
done

service_file="$scratch/pg_service.conf"
{
  printf '[backup_primary]\nhost=127.0.0.1\nport=%s\nuser=postgres\npassword=%s\ndbname=postgres\n' "$primary_port" "$secret"
  printf '[backup_witness]\nhost=127.0.0.1\nport=%s\nuser=postgres\npassword=%s\ndbname=postgres\n' "$witness_port" "$secret"
  printf '[backup_primary_replication]\nhost=127.0.0.1\nport=%s\nuser=postgres\npassword=%s\n' "$primary_port" "$secret"
  printf '[backup_witness_replication]\nhost=127.0.0.1\nport=%s\nuser=postgres\npassword=%s\n' "$witness_port" "$secret"
} > "$service_file"
chmod 600 "$service_file"
service_sha="$(openssl dgst -sha256 -r "$service_file" | awk '{print $1}')"
export PGSERVICEFILE="$service_file"
installation="$(uuidgen | tr '[:upper:]' '[:lower:]')"
lineage="$(uuidgen | tr '[:upper:]' '[:lower:]')"

PGSERVICE=backup_primary psql -X -w -q -v ON_ERROR_STOP=1 -c "CREATE SCHEMA claimcore; CREATE TABLE claimcore.installation_lineage (singleton boolean PRIMARY KEY, installation_id uuid NOT NULL, lineage_id uuid NOT NULL, witness_epoch bigint NOT NULL); INSERT INTO claimcore.installation_lineage VALUES (true, '$installation', '$lineage', 1); CREATE TABLE claimcore.synthetic_backup_oracle (value integer PRIMARY KEY); INSERT INTO claimcore.synthetic_backup_oracle VALUES (41);" >/dev/null
PGSERVICE=backup_witness psql -X -w -q -v ON_ERROR_STOP=1 -c "CREATE SCHEMA claimcore_witness; CREATE TABLE claimcore_witness.installation (singleton boolean PRIMARY KEY, installation_id uuid NOT NULL, lineage_id uuid NOT NULL, epoch bigint NOT NULL, tip_sequence bigint NOT NULL, tip_hash bytea NOT NULL); INSERT INTO claimcore_witness.installation VALUES (true, '$installation', '$lineage', 1, 1, decode(repeat('a',64),'hex')); CREATE TABLE claimcore_witness.synthetic_backup_oracle (value integer PRIMARY KEY); INSERT INTO claimcore_witness.synthetic_backup_oracle VALUES (43);" >/dev/null

age-keygen -o "$scratch/identity.age" >/dev/null 2>&1
chmod 600 "$scratch/identity.age"
recipient="$(age-keygen -y "$scratch/identity.age")"
openssl genpkey -algorithm ED25519 -out "$scratch/attestor.key" >/dev/null 2>&1
openssl pkey -in "$scratch/attestor.key" -pubout -out "$scratch/attestor.pub" >/dev/null 2>&1
openssl genpkey -algorithm ED25519 -out "$scratch/checkpoint.key" >/dev/null 2>&1
openssl pkey -in "$scratch/checkpoint.key" -pubout -out "$scratch/checkpoint.pub" >/dev/null 2>&1
chmod 600 "$scratch/attestor.key" "$scratch/attestor.pub" "$scratch/checkpoint.key" "$scratch/checkpoint.pub"
mkdir "$scratch/archive"
mkdir "$scratch/checkpoints"
mkdir "$scratch/inventory"
chmod 700 "$scratch/archive" "$scratch/checkpoints" "$scratch/inventory"
openssl rand 32 > "$scratch/commitment.key"
chmod 600 "$scratch/commitment.key"
signing_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
checkpoint_signing_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
encryption_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
checkpoint_socket_root="$(mktemp -d /tmp/cccp.XXXXXXXX)"
checkpoint_socket_root="$(cd "$checkpoint_socket_root" && pwd -P)"
chmod 700 "$checkpoint_socket_root"
checkpoint_socket="$checkpoint_socket_root/s"
mkdir -m 700 "$scratch/checkpoint-ledger"
jq -n --arg socket "$checkpoint_socket" \
  --arg ledger "$scratch/checkpoint-ledger" \
  --arg key "$scratch/checkpoint.key" --arg public "$scratch/checkpoint.pub" \
  --arg key_id "$checkpoint_signing_id" --arg installation "$installation" \
  --arg lineage "$lineage" \
  '{format:"claimcore-checkpoint-signer-config-1",purpose:"CHECKPOINT",transport:"LOCAL_SOCKET",socketPath:$socket,ledgerRoot:$ledger,signingKeyFile:$key,verificationKeyFile:$public,checkpointSigningKeyId:$key_id,installationId:$installation,lineageId:$lineage,epoch:1}' \
  > "$scratch/checkpoint-signer.json"
chmod 600 "$scratch/checkpoint-signer.json"
python3 -B "$repo_root/eng/backup/CheckpointSigner.py" \
  --config "$scratch/checkpoint-signer.json" > /dev/null \
  2> "$scratch/checkpoint-signer-status.json" &
checkpoint_signer_pid=$!
for ((attempt=0; attempt<30; attempt++)); do
  [[ -S "$checkpoint_socket" ]] && break
  sleep 0.1
done
if [[ ! -S "$checkpoint_socket" ]]; then
  reason="$(jq -er '.reason // "signer-unavailable"' "$scratch/checkpoint-signer-status.json" 2>/dev/null || printf '%s' 'signer-unavailable')"
  printf 'checkpoint-signer-stage=%s\n' "$reason" >&2
  exit 1
fi

jq -n --arg archive "$scratch/archive" --arg recipient "$recipient" \
  --arg checkpoints "$scratch/checkpoints" \
  --arg pgservice "$service_file" --arg pgsha "$service_sha" \
  --arg inventory "$scratch/inventory" --arg commitment "$scratch/commitment.key" \
  --arg signing_id "$signing_id" --arg checkpoint_signing_id "$checkpoint_signing_id" --arg encryption_id "$encryption_id" \
  --arg identity "$scratch/identity.age" --arg signing "$scratch/attestor.key" \
  --arg public "$scratch/attestor.pub" --arg checkpoint_public "$scratch/checkpoint.pub" \
  --arg checkpoint_socket "$checkpoint_socket" \
  '{format:"claimcore-managed-backup-1",archiveRoot:$archive,checkpointRoot:$checkpoints,inventoryRoot:$inventory,commitmentKey:$commitment,signingKeyId:$signing_id,checkpointSigningKeyId:$checkpoint_signing_id,encryptionKeyId:$encryption_id,backupIntervalSeconds:86400,maximumBackupAgeSeconds:172800,restoreHorizonSeconds:259200,backupRetentionSeconds:604800,walRetentionSeconds:604800,checkpointRetentionSeconds:1209600,ageRecipient:$recipient,ageIdentity:$identity,signingKey:$signing,verificationKey:$public,checkpointSignerMode:"LOCAL_SYNTHETIC",checkpointSignerSocket:$checkpoint_socket,checkpointSignerRemote:null,checkpointVerificationKey:$checkpoint_public,checkpointCustodianId:"synthetic-checkpoint-custodian",pgServiceFile:$pgservice,pgServiceFileSha256:$pgsha,pgTlsRootSha256:null,maxBackupBytes:134217728,maxTarEntries:20000,maxWalCopies:1000,primary:{metadataService:"backup_primary",replicationService:"backup_primary_replication",custodianId:"synthetic-primary"},witness:{metadataService:"backup_witness",replicationService:"backup_witness_replication",custodianId:"synthetic-witness"}}' > "$scratch/config.json"
chmod 600 "$scratch/config.json"
jq '. + {qualifiedVerifierSha256:("a" * 64)}' "$scratch/config.json" \
  > "$scratch/owner-selected-verifier.json"
chmod 600 "$scratch/owner-selected-verifier.json"
if capture_synthetic "$scratch/owner-selected-verifier.json" > /dev/null 2>&1; then
  echo 'Owner-selected callback hash passed lower-level backup configuration.' >&2
  exit 1
fi
jq '.checkpointVerificationKey=.verificationKey' "$scratch/config.json" \
  > "$scratch/reused-checkpoint-key.json"
chmod 600 "$scratch/reused-checkpoint-key.json"
if capture_synthetic "$scratch/reused-checkpoint-key.json" > /dev/null 2>&1; then
  echo 'A shared copy/checkpoint signing key passed admission.' >&2
  exit 1
fi
jq '{format,archiveRoot,ageRecipient}' "$scratch/config.json" > "$scratch/archiver.json"
chmod 600 "$scratch/archiver.json"

ln -s "$scratch/config.json" "$scratch/linked-config.json"
if capture_synthetic "$scratch/linked-config.json" > /dev/null 2>&1; then
  echo 'Linked private configuration passed admission.' >&2
  exit 1
fi
ln -s "$scratch" "$scratch/linked-parent"
if capture_synthetic "$scratch/linked-parent/config.json" > /dev/null 2>&1; then
  echo 'Linked private ancestor passed admission.' >&2
  exit 1
fi
PYTHONDONTWRITEBYTECODE=1 python3 "$repo_root/eng/backup/Test-ArchiveBounds.py" > /dev/null

if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" capture > /dev/null 2>&1; then
  echo 'The unfenced owner capture CLI remained available.' >&2
  exit 1
fi
capture="$(capture_synthetic "$scratch/config.json")"
cycle="$(jq -er '.cycleId' <<< "$capture")"
kill "$checkpoint_signer_pid" >/dev/null 2>&1
wait "$checkpoint_signer_pid" >/dev/null 2>&1 || true
checkpoint_signer_pid=""
[[ "$cycle" =~ ^[0-9a-f-]{36}$ ]]
[[ ! -f "$scratch/archive/$cycle/primary.tar" && -f "$scratch/archive/$cycle/primary.tar.age" ]]
[[ "$(find "$scratch/inventory" -name "$cycle.*.json" | wc -l | tr -d ' ')" == 2 ]]
for attestation in "$scratch/inventory/$cycle."*.json; do
  openssl pkeyutl -verify -rawin -pubin -inkey "$scratch/attestor.pub" -in "$attestation" -sigfile "${attestation%.json}.sig" > /dev/null
  jq -e '.eventKind == "REGISTER" and .state == "UNVERIFIED" and .primaryRegistration == "NOT_REGISTERED" and .verificationProofSha256 == null and (.backupManifestSha256 | length) == 64 and (.walSegmentBytes | type) == "number"' "$attestation" > /dev/null
done

CLAIMCORE_BACKUP_TEST_IMAGE="$image" \
  python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" \
  inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" > "$scratch/result.json"
jq -e '.status == "functional-restore-verified" and .qualificationScope == "synthetic-only" and .promotionAuthorized == false' "$scratch/result.json" >/dev/null
cp "$scratch/checkpoints/$cycle.sig" "$scratch/original-checkpoint.sig"
openssl pkeyutl -sign -rawin -inkey "$scratch/attestor.key" \
  -in "$scratch/checkpoints/$cycle.json" -out "$scratch/checkpoints/$cycle.sig"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" \
  inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" \
  > /dev/null 2>&1; then
  echo 'A checkpoint signed by the copy attestor passed independent-key verification.' >&2
  exit 1
fi
cp "$scratch/original-checkpoint.sig" "$scratch/checkpoints/$cycle.sig"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" \
  inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives-ForgedFull.sh" \
  > /dev/null 2>&1; then
  echo 'Caller-selected full restore claim passed the synthetic primitive drill.' >&2
  exit 1
fi
jq '.maxBackupBytes = 1048576' "$scratch/config.json" > "$scratch/bounded-config.json"
chmod 600 "$scratch/bounded-config.json"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/bounded-config.json" inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" > /dev/null 2>&1; then
  echo 'Oversized decrypted backup passed the configured bound.' >&2
  exit 1
fi

PGSERVICE=backup_primary psql -X -w -q -v ON_ERROR_STOP=1 -c 'INSERT INTO claimcore.synthetic_backup_oracle VALUES (42)' > /dev/null
PGSERVICE=backup_witness psql -X -w -q -v ON_ERROR_STOP=1 -c 'INSERT INTO claimcore_witness.synthetic_backup_oracle VALUES (44)' > /dev/null
wal_segment="$(PGSERVICE=backup_primary psql -X -w -q -A -t -v ON_ERROR_STOP=1 -c 'SELECT pg_walfile_name(pg_current_wal_insert_lsn())')"
witness_wal_segment="$(PGSERVICE=backup_witness psql -X -w -q -A -t -v ON_ERROR_STOP=1 -c 'SELECT pg_walfile_name(pg_current_wal_insert_lsn())')"
PGSERVICE=backup_primary psql -X -w -q -v ON_ERROR_STOP=1 -c 'SELECT pg_switch_wal()' > /dev/null
PGSERVICE=backup_witness psql -X -w -q -v ON_ERROR_STOP=1 -c 'SELECT pg_switch_wal()' > /dev/null
docker cp "$primary_container:/var/lib/postgresql/18/docker/pg_wal/$wal_segment" "$scratch/$wal_segment" > /dev/null
mkdir "$scratch/witness-wal"
docker cp "$witness_container:/var/lib/postgresql/18/docker/pg_wal/$witness_wal_segment" "$scratch/witness-wal/$witness_wal_segment" > /dev/null
pg_waldump -n 1 "$scratch/$wal_segment" > /dev/null
pg_waldump -n 1 "$scratch/witness-wal/$witness_wal_segment" > /dev/null
python3 "$repo_root/eng/backup/managed.py" --config "$scratch/archiver.json" archive-wal primary "$scratch/$wal_segment" "$wal_segment"
python3 "$repo_root/eng/backup/managed.py" --config "$scratch/archiver.json" archive-wal primary "$scratch/$wal_segment" "$wal_segment"
python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" attest-wal primary "$wal_segment" > /dev/null
[[ -f "$scratch/archive/wal/primary/$wal_segment.age" ]]
[[ "$(find "$scratch/inventory" -name "wal.$wal_segment.primary.*.json" | wc -l | tr -d ' ')" == 1 ]]
python3 "$repo_root/eng/backup/managed.py" --config "$scratch/archiver.json" archive-wal witness "$scratch/witness-wal/$witness_wal_segment" "$witness_wal_segment"
python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" attest-wal witness "$witness_wal_segment" > /dev/null
python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" > "$scratch/with-wal-result.json"
jq -e '.locallyAttestedWalCopies == 2 and .walFreshnessQualified == false and .managedErasureDeletionProved == false and .promotionAuthorized == false' "$scratch/with-wal-result.json" > /dev/null
mv "$scratch/archive/wal/witness/$witness_wal_segment.age" "$scratch/missing-witness-wal.age"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" > /dev/null 2>&1; then
  echo 'Missing managed witness WAL copy passed inspection.' >&2
  exit 1
fi
mv "$scratch/missing-witness-wal.age" "$scratch/archive/wal/witness/$witness_wal_segment.age"
wrong_timeline="00000002${wal_segment:8}"
cp "$scratch/$wal_segment" "$scratch/$wrong_timeline"
python3 "$repo_root/eng/backup/managed.py" --config "$scratch/archiver.json" archive-wal primary "$scratch/$wrong_timeline" "$wrong_timeline"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" attest-wal primary "$wrong_timeline" > /dev/null 2>&1; then
  echo 'Unowned WAL timeline passed attestation.' >&2
  exit 1
fi
mv "$scratch/archive/wal/primary/$wrong_timeline.age" "$scratch/quarantined-wrong-timeline.age"
mv "$scratch/archive/wal/primary/$wrong_timeline.json" "$scratch/quarantined-wrong-timeline.json"
malformed_wal=0000000100000000000000AA
dd if=/dev/zero of="$scratch/$malformed_wal" bs=1048576 count=16 2>/dev/null
python3 "$repo_root/eng/backup/managed.py" --config "$scratch/archiver.json" archive-wal primary "$scratch/$malformed_wal" "$malformed_wal"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" attest-wal primary "$malformed_wal" > /dev/null 2>&1; then
  echo 'Malformed PostgreSQL WAL passed owner attestation.' >&2
  exit 1
fi
mv "$scratch/archive/wal/primary/$malformed_wal.age" "$scratch/quarantined-malformed-wal.age"
mv "$scratch/archive/wal/primary/$malformed_wal.json" "$scratch/quarantined-malformed-wal.json"
printf 'changed synthetic WAL archive probe\n' >> "$scratch/$wal_segment"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/archiver.json" archive-wal primary "$scratch/$wal_segment" "$wal_segment" > /dev/null 2>&1; then
  echo 'Changed WAL segment under an existing name passed.' >&2
  exit 1
fi

cp "$scratch/archive/$cycle/manifest.json" "$scratch/original-manifest.json"
jq --arg installation "$(uuidgen | tr '[:upper:]' '[:lower:]')" '.installationId = $installation' "$scratch/original-manifest.json" > "$scratch/archive/$cycle/manifest.json"
openssl pkeyutl -sign -rawin -inkey "$scratch/attestor.key" -in "$scratch/archive/$cycle/manifest.json" -out "$scratch/archive/$cycle/manifest.sig"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" > /dev/null 2>&1; then
  echo 'Wrong-installation signed backup passed inspection.' >&2
  exit 1
fi
cp "$scratch/original-manifest.json" "$scratch/archive/$cycle/manifest.json"
openssl pkeyutl -sign -rawin -inkey "$scratch/attestor.key" -in "$scratch/archive/$cycle/manifest.json" -out "$scratch/archive/$cycle/manifest.sig"
jq -cS '.capturedAt = "2000-01-01T00:00:00Z"' "$scratch/original-manifest.json" > "$scratch/archive/$cycle/manifest.json"
openssl pkeyutl -sign -rawin -inkey "$scratch/attestor.key" -in "$scratch/archive/$cycle/manifest.json" -out "$scratch/archive/$cycle/manifest.sig"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" > /dev/null 2>&1; then
  echo 'Stale but correctly signed backup passed freshness policy.' >&2
  exit 1
fi
cp "$scratch/original-manifest.json" "$scratch/archive/$cycle/manifest.json"
openssl pkeyutl -sign -rawin -inkey "$scratch/attestor.key" -in "$scratch/archive/$cycle/manifest.json" -out "$scratch/archive/$cycle/manifest.sig"

mv "$scratch/checkpoints/$cycle.json" "$scratch/checkpoint-missing.json"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" > /dev/null 2>&1; then
  echo 'Missing independent checkpoint passed inspection.' >&2
  exit 1
fi
mv "$scratch/checkpoint-missing.json" "$scratch/checkpoints/$cycle.json"

jq '.epoch = 2' "$scratch/original-manifest.json" > "$scratch/archive/$cycle/manifest.json"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" > /dev/null 2>&1; then
  echo 'Altered signed manifest passed inspection.' >&2
  exit 1
fi
mv "$scratch/original-manifest.json" "$scratch/archive/$cycle/manifest.json"

cp "$scratch/archive/$cycle/witness.tar.age" "$scratch/original-witness.age"
printf x >> "$scratch/archive/$cycle/witness.tar.age"
if python3 "$repo_root/eng/backup/managed.py" --config "$scratch/config.json" inspect "$cycle" --restore-verifier "$repo_root/eng/backup/Verify-BackupPrimitives.sh" > /dev/null 2>&1; then
  echo 'Altered witness ciphertext passed inspection.' >&2
  exit 1
fi
mv "$scratch/original-witness.age" "$scratch/archive/$cycle/witness.tar.age"

bash "$repo_root/eng/backup/Test-ManagedBackupPromotion.sh" "$repo_root" "$scratch" "$installation" "$lineage" "$cycle"

echo 'Encrypted dual-cluster backup/WAL, signed copy inventory, isolated pair restores, corruption/staleness refusals and non-promoting two-owner evidence review passed.'

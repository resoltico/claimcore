#!/usr/bin/env bash
# Lower-level synthetic pg_verifybackup/restore oracle only; never a ClaimCore report.
set -euo pipefail
trap 'printf "synthetic restore verifier failed at line %s\n" "$LINENO" >&2' ERR
primary_data="$1"
witness_data="$2"
manifest="$3"
checkpoint="$4"
report="$5"
image="${CLAIMCORE_BACKUP_TEST_IMAGE:?Synthetic test image is required}"
[[ -f "${primary_data}/PG_VERSION" && -f "${witness_data}/PG_VERSION" && -f "${manifest}" && -f "${checkpoint}" ]]
primary_container=""
witness_container=""
cleanup() {
  for current in "${primary_container}" "${witness_container}"; do
    if [[ -n "${current}" ]]; then docker stop "${current}" >/dev/null 2>&1 || true; fi
  done
}
trap cleanup EXIT
start_restore() {
  local data="$1" name="$2" container
  container="$(docker run --rm -d --label "claimcore.backup-restore=$$" \
    --entrypoint sleep "${image}" 900)"
  if [[ "${name}" == primary ]]; then primary_container="${container}"; else witness_container="${container}"; fi
  docker exec -u root "${container}" mkdir -p /var/lib/postgresql/18/docker
  docker cp "${data}/." "${container}:/var/lib/postgresql/18/docker" >/dev/null
  docker exec -u root "${container}" chown -R postgres:postgres /var/lib/postgresql/18/docker
  docker exec -u root "${container}" chmod 700 /var/lib/postgresql/18/docker
  docker exec -u postgres "${container}" pg_ctl -D /var/lib/postgresql/18/docker \
    -l /tmp/claimcore-restore.log \
    -o "-c fsync=on -c full_page_writes=on -c synchronous_commit=on" \
    start >/dev/null
}
start_restore "${primary_data}" primary
start_restore "${witness_data}" witness
for current in "${primary_container}" "${witness_container}"; do
  for ((attempt = 0; attempt < 60; attempt++)); do
    if docker exec "${current}" pg_isready -q -U postgres; then break; fi
    sleep 1
  done
  docker exec "${current}" pg_isready -q -U postgres
done
expected_installation="$(jq -er '.installationId' "${manifest}")"
expected_lineage="$(jq -er '.lineageId' "${manifest}")"
primary_query="SELECT installation_id::text || ':' || lineage_id::text || ':' || (SELECT value::text FROM claimcore.synthetic_backup_oracle) FROM claimcore.installation_lineage WHERE singleton"
witness_query="SELECT installation_id::text || ':' || lineage_id::text || ':' || tip_sequence::text || ':' || (SELECT value::text FROM claimcore_witness.synthetic_backup_oracle) FROM claimcore_witness.installation WHERE singleton"
primary_actual="$(docker exec "${primary_container}" psql -X -U postgres -A -t -v ON_ERROR_STOP=1 -c "${primary_query}")"
witness_actual="$(docker exec "${witness_container}" psql -X -U postgres -A -t -v ON_ERROR_STOP=1 -c "${witness_query}")"
[[ "${primary_actual}" == "${expected_installation}:${expected_lineage}:41" ]]
[[ "${witness_actual}" == "${expected_installation}:${expected_lineage}:1:43" ]]
checkpoint_hash="$(jq -er '.witnessCheckpoint.hash' "${manifest}")"
checkpoint_sequence="$(jq -er '.witnessCheckpoint.sequence' "${manifest}")"
checkpoint_epoch="$(jq -er '.epoch' "${manifest}")"
jq -n --arg installation "${expected_installation}" --arg lineage "${expected_lineage}" \
  --arg hash "${checkpoint_hash}" \
  --argjson cutoff "${checkpoint_sequence}" \
  --argjson epoch "${checkpoint_epoch}" \
  '{format:"claimcore-restored-pair-report-1",installationId:$installation,lineageId:$lineage,epoch:$epoch,witnessCutoff:$cutoff,witnessHash:$hash,scope:"synthetic-only",recoveredDataChecked:true,pairCompared:true,catalogVerified:false,dataAuditVerified:false,walTimelineVerified:false,authorityReconciled:false,pendingIntents:null}' >"${report}"
chmod 600 "${report}"

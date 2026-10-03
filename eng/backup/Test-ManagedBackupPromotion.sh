#!/usr/bin/env bash
set -euo pipefail
umask 077
[[ $# -eq 5 ]]
repo_root="$1"
scratch="$2"
installation="$3"
lineage="$4"
cycle="$5"

PGSERVICE=backup_witness psql -X -w -q -v ON_ERROR_STOP=1 -c "UPDATE claimcore_witness.installation SET tip_sequence = 2, tip_hash = decode(repeat('b',64),'hex') WHERE singleton" >/dev/null
if python3 "${repo_root}/eng/backup/managed.py" --config "${scratch}/config.json" inspect "${cycle}" --restore-verifier "${repo_root}/eng/backup/Verify-BackupPrimitives.sh" >/dev/null 2>&1; then
  echo 'Stale witness backup passed against a newer live tip.' >&2
  exit 1
fi

for role in fence owner_one owner_two; do
  openssl genpkey -algorithm ED25519 -out "${scratch}/${role}.key" >/dev/null 2>&1
  openssl pkey -in "${scratch}/${role}.key" -pubout -out "${scratch}/${role}.pub" >/dev/null 2>&1
  chmod 600 "${scratch}/${role}.key" "${scratch}/${role}.pub"
done
actor_one="$(uuidgen | tr '[:upper:]' '[:lower:]')"
actor_two="$(uuidgen | tr '[:upper:]' '[:lower:]')"
key_one="$(uuidgen | tr '[:upper:]' '[:lower:]')"
key_two="$(uuidgen | tr '[:upper:]' '[:lower:]')"
approval_one="$(uuidgen | tr '[:upper:]' '[:lower:]')"
approval_two="$(uuidgen | tr '[:upper:]' '[:lower:]')"
jq -cS --arg fence "${scratch}/fence.pub" --arg actor_one "${actor_one}" --arg actor_two "${actor_two}" \
  --arg key_one "${key_one}" --arg key_two "${key_two}" --arg pub_one "${scratch}/owner_one.pub" --arg pub_two "${scratch}/owner_two.pub" \
  '. + {fenceVerificationKey:$fence,approverKeys:{($key_one):{actorId:$actor_one,publicKey:$pub_one},($key_two):{actorId:$actor_two,publicKey:$pub_two}}}' \
  "${scratch}/config.json" >"${scratch}/promotion-config.json"
chmod 600 "${scratch}/promotion-config.json"
expiry="$(python3 -c 'from datetime import datetime,timedelta,timezone; print((datetime.now(timezone.utc)+timedelta(minutes=10)).isoformat(timespec="seconds").replace("+00:00","Z"))')"
jq -cnS --arg installation "${installation}" --arg lineage "${lineage}" --arg expiry "${expiry}" \
  --arg actor_one "${actor_one}" --arg actor_two "${actor_two}" --arg approval_one "${approval_one}" --arg approval_two "${approval_two}" \
  '{format:"claimcore-restore-qualification-1",scope:"synthetic-only",source:"synthetic-fixture",installationId:$installation,lineageId:$lineage,epoch:1,witnessCutoff:1,authorityRevision:1,validUntil:$expiry,catalogVerified:true,dataAuditVerified:true,walTimelineVerified:true,authorityReconciled:true,managedCopiesRegistered:true,pendingIntents:0,authorizedApprovers:[{actorId:$actor_one,approvalEventId:$approval_one,role:"owner",grantRevision:1,active:true},{actorId:$actor_two,approvalEventId:$approval_two,role:"owner",grantRevision:1,active:true}]}' >"${scratch}/promotion-report.json"
openssl pkeyutl -sign -rawin -inkey "${scratch}/attestor.key" -in "${scratch}/promotion-report.json" -out "${scratch}/promotion-report.sig"
if python3 "${repo_root}/eng/backup/managed.py" --config "${scratch}/promotion-config.json" review-promotion --report "${scratch}/promotion-report.json" --fence-report "${scratch}/no-fence.json" --approval "${scratch}/no-approval-1.json" --approval "${scratch}/no-approval-2.json" >/dev/null 2>&1; then
  echo 'Synthetic-only report passed promotion review.' >&2
  exit 1
fi
jq -cS '.scope="full" | .source="ClaimCore.Database"' "${scratch}/promotion-report.json" >"${scratch}/full-report.json"
mv "${scratch}/full-report.json" "${scratch}/promotion-report.json"
openssl pkeyutl -sign -rawin -inkey "${scratch}/attestor.key" -in "${scratch}/promotion-report.json" -out "${scratch}/promotion-report.sig"
report_hash="$(openssl dgst -sha256 -r "${scratch}/promotion-report.json" | awk '{print $1}')"
jq -cnS --arg installation "${installation}" --arg lineage "${lineage}" --arg expiry "${expiry}" --arg report "${report_hash}" \
  '{format:"claimcore-old-writer-fence-1",installationId:$installation,lineageId:$lineage,reportSha256:$report,oldEpoch:1,newEpoch:2,authorityRevision:1,validUntil:$expiry,oldWriterStopped:true,primarySessionsTerminated:true,witnessSessionsTerminated:true,oldEndpointIsolated:true,primaryCredentialRevoked:true,witnessCredentialRevoked:true,newEpochWitnessed:true,preIsolationCommitReconciled:true}' >"${scratch}/fence-report.json"
openssl pkeyutl -sign -rawin -inkey "${scratch}/fence.key" -in "${scratch}/fence-report.json" -out "${scratch}/fence-report.sig"
fence_hash="$(openssl dgst -sha256 -r "${scratch}/fence-report.json" | awk '{print $1}')"
for number in one two; do
  actor_variable="actor_${number}"
  key_variable="key_${number}"
  approval_variable="approval_${number}"
  jq -cnS --arg actor "${!actor_variable}" --arg signing "${!key_variable}" --arg installation "${installation}" \
    --arg approval "${!approval_variable}" --arg report "${report_hash}" --arg fence "${fence_hash}" --arg expiry "${expiry}" \
    '{format:"claimcore-restore-approval-1",action:"PROMOTE_RESTORED_INSTALLATION",actorId:$actor,signingKeyId:$signing,approvalEventId:$approval,installationId:$installation,newEpoch:2,grantRevision:1,authorityRevision:1,reportSha256:$report,fenceSha256:$fence,validUntil:$expiry}' >"${scratch}/approval-${number}.json"
  openssl pkeyutl -sign -rawin -inkey "${scratch}/owner_${number}.key" -in "${scratch}/approval-${number}.json" -out "${scratch}/approval-${number}.sig"
done
if python3 "${repo_root}/eng/backup/managed.py" --config "${scratch}/promotion-config.json" review-promotion --report "${scratch}/promotion-report.json" --fence-report "${scratch}/fence-report.json" --approval "${scratch}/approval-one.json" --approval "${scratch}/approval-one.json" >/dev/null 2>&1; then
  echo 'Duplicate human approval passed promotion review.' >&2
  exit 1
fi
python3 "${repo_root}/eng/backup/managed.py" --config "${scratch}/promotion-config.json" review-promotion --report "${scratch}/promotion-report.json" --fence-report "${scratch}/fence-report.json" --approval "${scratch}/approval-one.json" --approval "${scratch}/approval-two.json" >"${scratch}/promotion-result.json"
jq -e '.status == "synthetic-promotion-evidence-reviewed" and .promotionAuthorized == false and .productCutoverRequired == true and .approvals == 2' "${scratch}/promotion-result.json" >/dev/null
python3 - "${scratch}/owner_one.pub" "${scratch}/owner-alias.pub" <<'PY'
import sys
from pathlib import Path
source = Path(sys.argv[1]).read_text().splitlines()
encoded = "".join(source[1:-1])
wrapped = "\n".join(encoded[index:index + 20] for index in range(0, len(encoded), 20))
Path(sys.argv[2]).write_text(source[0] + "\n" + wrapped + "\n" + source[-1] + "\n")
PY
jq -cS --arg key "${key_two}" --arg public "${scratch}/owner-alias.pub" \
  '.approverKeys[$key].publicKey=$public' "${scratch}/promotion-config.json" >"${scratch}/alias-config.json"
openssl pkeyutl -sign -rawin -inkey "${scratch}/owner_one.key" \
  -in "${scratch}/approval-two.json" -out "${scratch}/approval-two.sig"
if python3 "${repo_root}/eng/backup/managed.py" --config "${scratch}/alias-config.json" review-promotion \
  --report "${scratch}/promotion-report.json" --fence-report "${scratch}/fence-report.json" \
  --approval "${scratch}/approval-one.json" --approval "${scratch}/approval-two.json" >/dev/null 2>&1; then
  echo 'One owner signing key under different PEM wrapping passed promotion review.' >&2
  exit 1
fi

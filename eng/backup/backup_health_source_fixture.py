"""Private synthetic source-bundle material for independent role process tests."""

import base64
import hashlib
import subprocess
import uuid
from datetime import datetime, timedelta, timezone

from backup_health_source_io import role_public_key
from deployment_common import canonical, utc


def digest(value):
    return hashlib.sha256(value).hexdigest()


def private(root, relative, content):
    target = root / relative
    target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    target.write_bytes(content)
    target.chmod(0o600)
    return target


def key(root, name):
    path = root / (name + ".key")
    result = subprocess.run(
        ["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(path)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    assert result.returncode == 0
    path.chmod(0o600)
    return path


def fixture(root):
    archive, checkpoint, restore = (
        root / name for name in ("archive", "checkpoint", "restore")
    )
    for location in (archive, checkpoint, restore):
        location.mkdir(mode=0o700)
        (location / "objects").mkdir(mode=0o700)
    keys = {name: key(root, name) for name in ("archive", "checkpoint", "test-restore")}
    roles = []
    for number, (name, private_key) in enumerate(keys.items(), 1):
        roles.append(
            {
                "role": name,
                "publicKeyBase64": base64.b64encode(
                    role_public_key(private_key)
                ).decode("ascii"),
                "machineSha256": str(number) * 64,
                "storageSha256": str(number + 3) * 64,
                "adminActorId": str(uuid.uuid4()),
                "signerHolderActorId": str(uuid.uuid4()),
            }
        )
    policy = {
        "format": "claimcore-backup-health-policy-1",
        "policyId": "reviewed-health-test",
        "backupIntervalSeconds": 300,
        "maximumBackupAgeSeconds": 3600,
        "maximumWalLagSeconds": 3600,
        "maximumCheckpointAgeSeconds": 3600,
        "maximumRestoreTestAgeSeconds": 3600,
        "restoreHorizonSeconds": 7200,
        "archiveRoot": str(archive),
        "checkpointRoot": str(checkpoint),
        "restoreRoot": str(restore),
        "roles": roles,
    }
    identity = {
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
    }
    cycle, lease = str(uuid.uuid4()), str(uuid.uuid4())
    now = datetime.now(timezone.utc).replace(microsecond=0)
    recent = utc(now - timedelta(seconds=5))
    objects = []
    for cluster, system in (
        ("PRIMARY", "1111111111111111111"),
        ("WITNESS", "2222222222222222222"),
    ):
        for kind in ("BASE", "WAL"):
            copy_id = str(uuid.uuid4())
            relative = "objects/" + copy_id + ".age"
            body = (cluster + kind).encode("ascii")
            private(archive, relative, body)
            objects.append(
                {
                    "copyId": copy_id,
                    "revision": 2,
                    "cluster": cluster,
                    "kind": kind,
                    "postgresSystemId": system,
                    "timeline": 1,
                    "walSegmentBytes": 16777216,
                    "walHorizon": "0/0" if kind == "BASE" else "0/100",
                    "walSegment": None
                    if kind == "BASE"
                    else "000000010000000000000000",
                    "ciphertextSha256": digest(body),
                    "ciphertextBytes": len(body),
                    "physicalReceiptSha256": "a" * 64,
                    "relativePath": relative,
                    "verifiedAt": recent,
                }
            )
    checkpoint_document = {
        "format": "claimcore-witness-checkpoint-1",
        "cycleId": cycle,
        "installationId": identity["installationId"],
        "lineageId": identity["lineageId"],
        "epoch": 1,
        "writerGeneration": 1,
        "sequence": 18,
        "hash": "d" * 64,
        "backupCaptureSequence": 18,
        "backupCaptureHash": "d" * 64,
        "leaseId": lease,
        "captureNonce": "9" * 64,
        "maintenanceEvidenceSha256": "1" * 64,
    }
    checkpoint_raw = canonical(checkpoint_document)
    private(checkpoint, cycle + ".json", checkpoint_raw)
    report = {
        "format": "claimcore-restore-qualification-1",
        "scope": "full",
        **identity,
        "primarySystemId": "1111111111111111111",
        "primaryTimeline": 1,
        "witnessSystemId": "2222222222222222222",
        "witnessTimeline": 1,
        "primaryRegisteredWalHorizon": "0/100",
        "witnessRegisteredWalHorizon": "0/100",
        "witnessCutoff": 17,
        "witnessCutoffHash": "e" * 64,
    }
    audit = {
        "kind": "dataAuditResult",
        "command": "VERIFY_DATA",
        "status": "VERIFIED",
        **identity,
        "witnessCutoff": 17,
        "witnessTipHash": "e" * 64,
    }
    report_raw, audit_raw = canonical(report), canonical(audit)
    private(restore, "objects/report.json", report_raw)
    private(restore, "objects/audit.json", audit_raw)
    source = {
        "format": "claimcore-backup-health-source-1",
        **identity,
        "cycleId": cycle,
        "leaseId": lease,
        "captureNonce": "9" * 64,
        "captureReceiptSha256": "8" * 64,
        "backupCaptureSequence": 18,
        "backupCaptureHash": "d" * 64,
        "writerGeneration": 1,
        "policyId": policy["policyId"],
        "authorityRevision": 7,
        "witnessTipSequence": 20,
        "witnessTipHash": "c" * 64,
        "knownCopyInventorySha256": "b" * 64,
        "artifactCutoffSequence": 16,
        "checkedAt": utc(now),
        "validUntil": utc(now + timedelta(minutes=2)),
        "objects": objects,
        "checkpoint": {
            "sequence": 18,
            "hash": "d" * 64,
            "objectSha256": digest(checkpoint_raw),
            "objectBytes": len(checkpoint_raw),
            "relativePath": cycle + ".json",
            "verifiedAt": recent,
        },
        "testRestore": {
            "reportSha256": digest(report_raw),
            "reportRelativePath": "objects/report.json",
            "fullAuditSha256": digest(audit_raw),
            "auditRelativePath": "objects/audit.json",
            "witnessCutoff": 17,
            "witnessCutoffHash": "e" * 64,
            "primaryBaseCopyId": next(
                item["copyId"]
                for item in objects
                if (item["cluster"], item["kind"]) == ("PRIMARY", "BASE")
            ),
            "witnessBaseCopyId": next(
                item["copyId"]
                for item in objects
                if (item["cluster"], item["kind"]) == ("WITNESS", "BASE")
            ),
            "primaryWalHorizon": "0/100",
            "witnessWalHorizon": "0/100",
            "primarySystemId": "1111111111111111111",
            "primaryTimeline": 1,
            "witnessSystemId": "2222222222222222222",
            "witnessTimeline": 1,
            "verifiedAt": recent,
        },
    }
    return policy, source, keys, archive

"""Private synthetic source-bundle material for independent role process tests."""

import base64
import hashlib
import subprocess
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_health_source_io import role_public_key
from backup_types import JsonObject
from deployment_common import canonical, utc

PRIMARY_SYSTEM = "1111111111111111111"
WITNESS_SYSTEM = "2222222222222222222"
SEGMENT_BYTES = 16777216
CHECKPOINT_SEQUENCE = 18
RESTORE_CUTOFF = 17


def digest(value: bytes) -> str:
    """Return the SHA-256 of `value`."""
    return hashlib.sha256(value).hexdigest()


def private(root: Path, relative: str, content: bytes) -> Path:
    """Write an owner-only file beneath `root`, creating owner-only parents."""
    target = root / relative
    target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    target.write_bytes(content)
    target.chmod(0o600)
    return target


def key(root: Path, name: str) -> Path:
    """Create an Ed25519 private key file beneath `root`."""
    path = root / (name + ".key")
    result = subprocess.run(
        ["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(path)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    if result.returncode != 0:
        msg = "openssl failed to create a key"
        raise RuntimeError(msg)
    path.chmod(0o600)
    return path


def _policy(root: Path) -> tuple[JsonObject, dict[str, Path], Path]:
    archive, checkpoint, restore = (root / name for name in ("archive", "checkpoint", "restore"))
    for location in (archive, checkpoint, restore):
        location.mkdir(mode=0o700)
        (location / "objects").mkdir(mode=0o700)
    keys = {name: key(root, name) for name in ("archive", "checkpoint", "test-restore")}
    roles = [
        {
            "role": name,
            "publicKeyBase64": base64.b64encode(role_public_key(private_key)).decode("ascii"),
            "machineSha256": str(number) * 64,
            "storageSha256": str(number + 3) * 64,
            "adminActorId": str(uuid.uuid4()),
            "signerHolderActorId": str(uuid.uuid4()),
        }
        for number, (name, private_key) in enumerate(keys.items(), 1)
    ]
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
    return policy, keys, archive


def _objects(archive: Path, recent: str) -> list[JsonObject]:
    objects = []
    for cluster, system in (("PRIMARY", PRIMARY_SYSTEM), ("WITNESS", WITNESS_SYSTEM)):
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
                    "walSegmentBytes": SEGMENT_BYTES,
                    "walHorizon": "0/0" if kind == "BASE" else "0/100",
                    "walSegment": None if kind == "BASE" else "000000010000000000000000",
                    "ciphertextSha256": digest(body),
                    "ciphertextBytes": len(body),
                    "physicalReceiptSha256": "a" * 64,
                    "relativePath": relative,
                    "verifiedAt": recent,
                }
            )
    return objects


def _checkpoint(root: Path, identity: JsonObject, cycle: str, lease: str) -> bytes:
    document = {
        "format": "claimcore-witness-checkpoint-1",
        "cycleId": cycle,
        "installationId": identity["installationId"],
        "lineageId": identity["lineageId"],
        "epoch": 1,
        "writerGeneration": 1,
        "sequence": CHECKPOINT_SEQUENCE,
        "hash": "d" * 64,
        "backupCaptureSequence": CHECKPOINT_SEQUENCE,
        "backupCaptureHash": "d" * 64,
        "leaseId": lease,
        "captureNonce": "9" * 64,
        "maintenanceEvidenceSha256": "1" * 64,
    }
    raw = canonical(document)
    private(root / "checkpoint", cycle + ".json", raw)
    return raw


def _restore_files(restore: Path, identity: JsonObject) -> tuple[bytes, bytes]:
    report = {
        "format": "claimcore-restore-qualification-1",
        "scope": "full",
        **identity,
        "primarySystemId": PRIMARY_SYSTEM,
        "primaryTimeline": 1,
        "witnessSystemId": WITNESS_SYSTEM,
        "witnessTimeline": 1,
        "primaryRegisteredWalHorizon": "0/100",
        "witnessRegisteredWalHorizon": "0/100",
        "witnessCutoff": RESTORE_CUTOFF,
        "witnessCutoffHash": "e" * 64,
    }
    audit = {
        "kind": "dataAuditResult",
        "command": "VERIFY_DATA",
        "status": "VERIFIED",
        **identity,
        "witnessCutoff": RESTORE_CUTOFF,
        "witnessTipHash": "e" * 64,
    }
    report_raw, audit_raw = canonical(report), canonical(audit)
    private(restore, "objects/report.json", report_raw)
    private(restore, "objects/audit.json", audit_raw)
    return report_raw, audit_raw


def _base_id(objects: list[JsonObject], cluster: str) -> str:
    found: str = next(
        item["copyId"] for item in objects if (item["cluster"], item["kind"]) == (cluster, "BASE")
    )
    return found


def _test_restore(objects: list[JsonObject], raws: tuple[bytes, bytes], recent: str) -> JsonObject:
    report_raw, audit_raw = raws
    return {
        "reportSha256": digest(report_raw),
        "reportRelativePath": "objects/report.json",
        "fullAuditSha256": digest(audit_raw),
        "auditRelativePath": "objects/audit.json",
        "witnessCutoff": RESTORE_CUTOFF,
        "witnessCutoffHash": "e" * 64,
        "primaryBaseCopyId": _base_id(objects, "PRIMARY"),
        "witnessBaseCopyId": _base_id(objects, "WITNESS"),
        "primaryWalHorizon": "0/100",
        "witnessWalHorizon": "0/100",
        "primarySystemId": PRIMARY_SYSTEM,
        "primaryTimeline": 1,
        "witnessSystemId": WITNESS_SYSTEM,
        "witnessTimeline": 1,
        "verifiedAt": recent,
    }


def fixture(root: Path) -> tuple[JsonObject, JsonObject, dict[str, Path], Path]:
    """Build a synthetic policy, source, role keys and archive that all agree."""
    policy, keys, archive = _policy(root)
    identity = {"installationId": str(uuid.uuid4()), "lineageId": str(uuid.uuid4()), "epoch": 1}
    cycle, lease = str(uuid.uuid4()), str(uuid.uuid4())
    now = datetime.now(UTC).replace(microsecond=0)
    recent = utc(now - timedelta(seconds=5))
    objects = _objects(archive, recent)
    checkpoint_raw = _checkpoint(root, identity, cycle, lease)
    report_raw, audit_raw = _restore_files(root / "restore", identity)
    source = {
        "format": "claimcore-backup-health-source-1",
        **identity,
        "cycleId": cycle,
        "leaseId": lease,
        "captureNonce": "9" * 64,
        "captureReceiptSha256": "8" * 64,
        "backupCaptureSequence": CHECKPOINT_SEQUENCE,
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
            "sequence": CHECKPOINT_SEQUENCE,
            "hash": "d" * 64,
            "objectSha256": digest(checkpoint_raw),
            "objectBytes": len(checkpoint_raw),
            "relativePath": cycle + ".json",
            "verifiedAt": recent,
        },
        "testRestore": _test_restore(objects, (report_raw, audit_raw), recent),
    }
    return policy, source, keys, archive

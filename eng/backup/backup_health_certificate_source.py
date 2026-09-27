"""Exact health-certificate candidate assembly and independent role-signature checks."""

import base64
import hashlib
import subprocess
import tempfile
import uuid
from datetime import timedelta
from pathlib import Path

from deployment_common import canonical, require, timestamp, utc

FENCE_FIELDS = {
    "kind",
    "handoffId",
    "w1Sequence",
    "w1Hash",
    "activationSequence",
    "activationHash",
    "oldGeneration",
    "newGeneration",
}


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (ValueError, TypeError, AttributeError):
        return False


def _fence(value, generation, tip):
    require(
        isinstance(value, dict) and set(value) == FENCE_FIELDS,
        "health-certificate-fence",
    )
    if generation == 1:
        require(
            value["kind"] == "GENESIS"
            and all(value[name] is None for name in FENCE_FIELDS - {"kind"}),
            "health-certificate-genesis",
        )
    else:
        require(
            value["kind"] == "HANDOFF" and _uuid(value["handoffId"]),
            "health-certificate-handoff",
        )
        require(
            value["oldGeneration"] == generation - 1
            and value["newGeneration"] == generation
            and type(value["w1Sequence"]) is int
            and type(value["activationSequence"]) is int
            and 0 < value["w1Sequence"] < value["activationSequence"] <= tip
            and all(
                isinstance(value[name], str) and len(value[name]) == 64
                for name in ("w1Hash", "activationHash")
            ),
            "health-certificate-activation",
        )


def candidate(
    source, policy, signer_key_id, signer_holder_id, writer_fence, checked_at
):
    require(
        _uuid(signer_key_id) and _uuid(signer_holder_id), "health-certificate-signer"
    )
    _fence(writer_fence, source["writerGeneration"], source["witnessTipSequence"])
    checked = timestamp(checked_at)
    require(
        timestamp(source["checkedAt"]) <= checked < timestamp(source["validUntil"]),
        "health-certificate-time",
    )
    expiry = min(timestamp(source["validUntil"]), checked + timedelta(seconds=90))

    def copy(cluster, kind):
        return next(
            item
            for item in source["objects"]
            if (item["cluster"], item["kind"]) == (cluster, kind)
        )

    def base(item):
        return {
            name: item[name]
            for name in ("copyId", "revision", "physicalReceiptSha256", "verifiedAt")
        }

    def wal(cluster):
        objects = [
            item
            for item in source["objects"]
            if (item["cluster"], item["kind"]) == (cluster, "WAL")
        ]
        return {
            "copyIds": sorted(item["copyId"] for item in objects),
            "registeredHorizon": source["testRestore"][cluster.lower() + "WalHorizon"],
            "archiveInspectionSha256": hashlib.sha256(canonical(source)).hexdigest(),
            "verifiedAt": source["checkedAt"],
        }

    checkpoint = source["checkpoint"]
    restored = source["testRestore"]
    value = {
        "format": "claimcore-backup-health-1",
        "source": "ClaimCore.Database",
        "scope": "full",
        "installationId": source["installationId"],
        "lineageId": source["lineageId"],
        "epoch": source["epoch"],
        "writerGeneration": source["writerGeneration"],
        "policyId": policy["policyId"],
        "authorityRevision": source["authorityRevision"],
        "witnessTipSequence": source["witnessTipSequence"],
        "witnessTipHash": source["witnessTipHash"],
        "checkedAt": checked_at,
        "validUntil": utc(expiry),
        "maximumBackupAgeSeconds": policy["maximumBackupAgeSeconds"],
        "maximumWalLagSeconds": policy["maximumWalLagSeconds"],
        "maximumCheckpointAgeSeconds": policy["maximumCheckpointAgeSeconds"],
        "maximumRestoreTestAgeSeconds": policy["maximumRestoreTestAgeSeconds"],
        "restoreHorizonSeconds": policy["restoreHorizonSeconds"],
        "primarySystemId": restored["primarySystemId"],
        "primaryTimeline": restored["primaryTimeline"],
        "witnessSystemId": restored["witnessSystemId"],
        "witnessTimeline": restored["witnessTimeline"],
        "primaryBase": base(copy("PRIMARY", "BASE")),
        "witnessBase": base(copy("WITNESS", "BASE")),
        "primaryWal": wal("PRIMARY"),
        "witnessWal": wal("WITNESS"),
        "checkpoint": {
            name: checkpoint[name]
            for name in ("sequence", "hash", "objectSha256", "verifiedAt")
        },
        "testRestore": {
            name: restored[name]
            for name in (
                "reportSha256",
                "witnessCutoff",
                "witnessCutoffHash",
                "verifiedAt",
            )
        },
        "knownCopyInventorySha256": source["knownCopyInventorySha256"],
        "artifactCutoffSequence": source["artifactCutoffSequence"],
        "writerFence": writer_fence,
        "signerKeyId": signer_key_id,
        "signerHolderActorId": signer_holder_id,
    }
    return value


def _verify_raw(source_bytes, signature, public_key):
    require(
        isinstance(signature, bytes) and len(signature) == 64, "health-role-signature"
    )
    require(
        isinstance(public_key, bytes) and len(public_key) == 32,
        "health-role-public-key",
    )
    with tempfile.TemporaryDirectory(prefix="claimcore-health-verify-") as raw:
        root = Path(raw)
        root.chmod(0o700)
        source = root / "source.json"
        signed = root / "source.sig"
        key = root / "role.der"
        for path, body in (
            (source, source_bytes),
            (signed, signature),
            (key, bytes.fromhex("302a300506032b6570032100") + public_key),
        ):
            path.write_bytes(body)
            path.chmod(0o600)
        result = subprocess.run(
            [
                "openssl",
                "pkeyutl",
                "-verify",
                "-pubin",
                "-inkey",
                str(key),
                "-keyform",
                "DER",
                "-rawin",
                "-in",
                str(source),
                "-sigfile",
                str(signed),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        require(result.returncode == 0, "health-role-signature")


def verify_roles(source_bytes, policy, signature_paths):
    from backup_health_source_io import _read

    for role in policy["roles"]:
        encoded = role["publicKeyBase64"]
        public = base64.b64decode(encoded, validate=True)
        _verify_raw(source_bytes, _read(signature_paths[role["role"]], 64), public)

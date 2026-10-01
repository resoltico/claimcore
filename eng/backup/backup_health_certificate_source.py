"""Exact health-certificate candidate assembly and independent role-signature checks."""

import base64
import hashlib
import subprocess
import tempfile
from dataclasses import dataclass
from datetime import timedelta
from pathlib import Path

from backup_health_source_io import read_private
from backup_types import JsonObject
from deployment_common import SIGNATURE_BYTES, canonical, is_uuid, require, timestamp, utc

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
VALIDITY_SECONDS = 90
ROLE_KEY_BYTES = 32
DIGEST_LENGTH = 64
ED25519_PREFIX = bytes.fromhex("302a300506032b6570032100")
BASE_KEYS = ("copyId", "revision", "physicalReceiptSha256", "verifiedAt")
CHECKPOINT_KEYS = ("sequence", "hash", "objectSha256", "verifiedAt")
RESTORE_KEYS = ("reportSha256", "witnessCutoff", "witnessCutoffHash", "verifiedAt")
POLICY_KEYS = (
    "maximumBackupAgeSeconds",
    "maximumWalLagSeconds",
    "maximumCheckpointAgeSeconds",
    "maximumRestoreTestAgeSeconds",
    "restoreHorizonSeconds",
)


@dataclass(frozen=True)
class CertificateRequest:
    """The signer identities, writer fence and time a certificate candidate is prepared for."""

    signer_key_id: str
    signer_holder_id: str
    writer_fence: JsonObject
    checked_at: str


def _fence(value: JsonObject, generation: int, tip: int) -> None:
    require(isinstance(value, dict) and set(value) == FENCE_FIELDS, "health-certificate-fence")
    if generation == 1:
        require(
            value["kind"] == "GENESIS"
            and all(value[name] is None for name in FENCE_FIELDS - {"kind"}),
            "health-certificate-genesis",
        )
        return
    require(
        value["kind"] == "HANDOFF" and is_uuid(value["handoffId"]), "health-certificate-handoff"
    )
    require(
        value["oldGeneration"] == generation - 1
        and value["newGeneration"] == generation
        and type(value["w1Sequence"]) is int
        and type(value["activationSequence"]) is int
        and 0 < value["w1Sequence"] < value["activationSequence"] <= tip
        and all(
            isinstance(value[name], str) and len(value[name]) == DIGEST_LENGTH
            for name in ("w1Hash", "activationHash")
        ),
        "health-certificate-activation",
    )


def _copy(source: JsonObject, cluster: str, kind: str) -> JsonObject:
    found: JsonObject = next(
        item for item in source["objects"] if (item["cluster"], item["kind"]) == (cluster, kind)
    )
    return found


def _wal(source: JsonObject, cluster: str) -> JsonObject:
    objects = [
        item for item in source["objects"] if (item["cluster"], item["kind"]) == (cluster, "WAL")
    ]
    return {
        "copyIds": sorted(item["copyId"] for item in objects),
        "registeredHorizon": source["testRestore"][cluster.lower() + "WalHorizon"],
        "archiveInspectionSha256": hashlib.sha256(canonical(source)).hexdigest(),
        "verifiedAt": source["checkedAt"],
    }


def candidate(source: JsonObject, policy: JsonObject, request: CertificateRequest) -> JsonObject:
    """Assemble the exact, unsigned health-certificate candidate for a verified source."""
    require(
        is_uuid(request.signer_key_id) and is_uuid(request.signer_holder_id),
        "health-certificate-signer",
    )
    _fence(request.writer_fence, source["writerGeneration"], source["witnessTipSequence"])
    checked = timestamp(request.checked_at)
    require(
        timestamp(source["checkedAt"]) <= checked < timestamp(source["validUntil"]),
        "health-certificate-time",
    )
    expiry = min(timestamp(source["validUntil"]), checked + timedelta(seconds=VALIDITY_SECONDS))
    restored = source["testRestore"]
    return {
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
        "checkedAt": request.checked_at,
        "validUntil": utc(expiry),
        **{name: policy[name] for name in POLICY_KEYS},
        "primarySystemId": restored["primarySystemId"],
        "primaryTimeline": restored["primaryTimeline"],
        "witnessSystemId": restored["witnessSystemId"],
        "witnessTimeline": restored["witnessTimeline"],
        "primaryBase": {name: _copy(source, "PRIMARY", "BASE")[name] for name in BASE_KEYS},
        "witnessBase": {name: _copy(source, "WITNESS", "BASE")[name] for name in BASE_KEYS},
        "primaryWal": _wal(source, "PRIMARY"),
        "witnessWal": _wal(source, "WITNESS"),
        "checkpoint": {name: source["checkpoint"][name] for name in CHECKPOINT_KEYS},
        "testRestore": {name: restored[name] for name in RESTORE_KEYS},
        "knownCopyInventorySha256": source["knownCopyInventorySha256"],
        "artifactCutoffSequence": source["artifactCutoffSequence"],
        "writerFence": request.writer_fence,
        "signerKeyId": request.signer_key_id,
        "signerHolderActorId": request.signer_holder_id,
    }


def _verify_raw(source_bytes: bytes, signature: bytes, public_key: bytes) -> None:
    require(
        isinstance(signature, bytes) and len(signature) == SIGNATURE_BYTES, "health-role-signature"
    )
    require(
        isinstance(public_key, bytes) and len(public_key) == ROLE_KEY_BYTES,
        "health-role-public-key",
    )
    with tempfile.TemporaryDirectory(prefix="claimcore-health-verify-") as raw:
        root = Path(raw)
        root.chmod(0o700)
        source = root / "source.json"
        signed = root / "source.sig"
        verifier_file = root / "role.der"
        for path, body in (
            (source, source_bytes),
            (signed, signature),
            (verifier_file, ED25519_PREFIX + public_key),
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
                str(verifier_file),
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


def verify_roles(source_bytes: bytes, policy: JsonObject, signature_paths: dict[str, str]) -> None:
    """Require every policy role's independent signature over the exact source bytes."""
    for role in policy["roles"]:
        public = base64.b64decode(role["publicKeyBase64"], validate=True)
        signature = read_private(signature_paths[role["role"]], SIGNATURE_BYTES)
        _verify_raw(source_bytes, signature, public)

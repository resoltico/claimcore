"""Strict signed backup-health evidence codec; no issuer or readiness decision."""

import base64
import hashlib
import json
import re
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import Json, JsonObject
from deployment_common import (
    SIGNATURE_BYTES,
    canonical,
    is_sha256,
    is_uuid,
    private_path,
    require,
    timestamp,
    verify,
)

FIELDS = {
    "format",
    "source",
    "scope",
    "installationId",
    "lineageId",
    "epoch",
    "writerGeneration",
    "policyId",
    "authorityRevision",
    "witnessTipSequence",
    "witnessTipHash",
    "checkedAt",
    "validUntil",
    "maximumBackupAgeSeconds",
    "maximumWalLagSeconds",
    "maximumCheckpointAgeSeconds",
    "maximumRestoreTestAgeSeconds",
    "restoreHorizonSeconds",
    "primarySystemId",
    "primaryTimeline",
    "witnessSystemId",
    "witnessTimeline",
    "primaryBase",
    "witnessBase",
    "primaryWal",
    "witnessWal",
    "checkpoint",
    "testRestore",
    "knownCopyInventorySha256",
    "artifactCutoffSequence",
    "writerFence",
    "signerKeyId",
    "signerHolderActorId",
}
BASE_FIELDS = {"copyId", "revision", "physicalReceiptSha256", "verifiedAt"}
WAL_FIELDS = {"copyIds", "registeredHorizon", "archiveInspectionSha256", "verifiedAt"}
CHECKPOINT_FIELDS = {"sequence", "hash", "objectSha256", "verifiedAt"}
RESTORE_FIELDS = {"reportSha256", "witnessCutoff", "witnessCutoffHash", "verifiedAt"}
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


HEALTH_LIMIT = 65536
INT64_MAX = 2**63 - 1
INT32_MAX = 2**31 - 1
MAX_WAL_COPIES = 1000
MAX_POLICY_SECONDS = 365 * 86400
VALIDITY_MINUTES = 5
MIN_BASE_REVISION = 2
POLICY_SECONDS = (
    "maximumBackupAgeSeconds",
    "maximumWalLagSeconds",
    "maximumCheckpointAgeSeconds",
    "maximumRestoreTestAgeSeconds",
    "restoreHorizonSeconds",
)


def _lsn(value: Json) -> bool:
    return (
        isinstance(value, str) and re.fullmatch(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}", value) is not None
    )


def _positive(value: Json, maximum: int) -> bool:
    return type(value) is int and 1 <= value <= maximum


def _age(value: Json, checked: datetime, limit: int, category: str) -> None:
    observed = timestamp(value)
    require(observed <= checked and checked - observed <= timedelta(seconds=limit), category)


def _base(value: Json, checked: datetime, age: int) -> None:
    require(isinstance(value, dict) and set(value) == BASE_FIELDS, "health-base-shape")
    require(
        is_uuid(value["copyId"])
        and _positive(value["revision"], INT64_MAX)
        and value["revision"] >= MIN_BASE_REVISION
        and is_sha256(value["physicalReceiptSha256"]),
        "health-base-evidence",
    )
    _age(value["verifiedAt"], checked, age, "health-base-stale")


def _wal(value: Json, checked: datetime, age: int) -> None:
    require(isinstance(value, dict) and set(value) == WAL_FIELDS, "health-wal-shape")
    ids = value["copyIds"]
    require(
        isinstance(ids, list)
        and 1 <= len(ids) <= MAX_WAL_COPIES
        and all(is_uuid(item) for item in ids)
        and ids == sorted(set(ids))
        and _lsn(value["registeredHorizon"])
        and is_sha256(value["archiveInspectionSha256"]),
        "health-wal-evidence",
    )
    _age(value["verifiedAt"], checked, age, "health-wal-stale")


def _checkpoint(value: Json, checked: datetime, age: int, tip: int) -> None:
    require(isinstance(value, dict) and set(value) == CHECKPOINT_FIELDS, "health-checkpoint-shape")
    require(
        type(value["sequence"]) is int
        and 0 <= value["sequence"] <= tip
        and is_sha256(value["hash"])
        and is_sha256(value["objectSha256"]),
        "health-checkpoint-evidence",
    )
    _age(value["verifiedAt"], checked, age, "health-checkpoint-stale")


def _restore(value: Json, checked: datetime, age: int, tip: int) -> None:
    require(isinstance(value, dict) and set(value) == RESTORE_FIELDS, "health-restore-shape")
    require(
        is_sha256(value["reportSha256"])
        and is_sha256(value["witnessCutoffHash"])
        and type(value["witnessCutoff"]) is int
        and 0 <= value["witnessCutoff"] <= tip,
        "health-restore-evidence",
    )
    _age(value["verifiedAt"], checked, age, "health-restore-stale")


def _fence(value: Json, generation: int, tip: int) -> None:
    require(isinstance(value, dict) and set(value) == FENCE_FIELDS, "health-fence-shape")
    if generation == 1:
        require(
            value["kind"] == "GENESIS"
            and all(value[name] is None for name in FENCE_FIELDS - {"kind"}),
            "health-fence-genesis",
        )
        return
    require(value["kind"] == "HANDOFF" and is_uuid(value["handoffId"]), "health-fence-handoff")
    require(
        value["oldGeneration"] == generation - 1
        and value["newGeneration"] == generation
        and type(value["w1Sequence"]) is int
        and type(value["activationSequence"]) is int
        and 0 < value["w1Sequence"] < value["activationSequence"] <= tip
        and is_sha256(value["w1Hash"])
        and is_sha256(value["activationHash"]),
        "health-fence-activation",
    )


def _verify_envelope(
    raw: bytes, signature: bytes, pinned_public_key: str | Path
) -> tuple[JsonObject, str]:
    require(isinstance(raw, bytes) and 0 < len(raw) <= HEALTH_LIMIT, "health-size")
    require(
        isinstance(signature, bytes) and len(signature) == SIGNATURE_BYTES, "health-signature-size"
    )
    document = json.loads(raw)
    require(isinstance(document, dict) and raw == canonical(document), "health-canonical")
    return verify(
        {"report": document, "signatureBase64": base64.b64encode(signature).decode("ascii")},
        pinned_public_key,
    )


def _check_identity(verified: JsonObject, expected: JsonObject, required_scope: str) -> None:
    require(set(verified) == FIELDS, "health-shape")
    require(
        verified["format"] == "claimcore-backup-health-1"
        and verified["source"] == "ClaimCore.Database"
        and verified["scope"] in ("full", "synthetic-only")
        and verified["scope"] == required_scope,
        "health-scope",
    )
    require(
        is_uuid(verified["installationId"])
        and is_uuid(verified["lineageId"])
        and is_uuid(verified["signerKeyId"])
        and is_uuid(verified["signerHolderActorId"])
        and verified["installationId"] == expected["installationId"]
        and verified["lineageId"] == expected["lineageId"]
        and verified["epoch"] == expected["epoch"]
        and verified["writerGeneration"] == expected["writerGeneration"],
        "health-installation",
    )
    for name in ("epoch", "writerGeneration", "authorityRevision"):
        require(_positive(verified[name], INT64_MAX), "health-authority")


def _check_policy(verified: JsonObject) -> None:
    tip = verified["witnessTipSequence"]
    require(type(tip) is int and tip >= 0 and is_sha256(verified["witnessTipHash"]), "health-tip")
    require(
        re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:-]{0,79}", verified["policyId"])
        and is_sha256(verified["knownCopyInventorySha256"]),
        "health-policy",
    )
    for name in ("primary", "witness"):
        require(
            isinstance(verified[name + "SystemId"], str)
            and re.fullmatch(r"[0-9]{1,20}", verified[name + "SystemId"])
            and _positive(verified[name + "Timeline"], INT32_MAX),
            "health-cluster",
        )
    require(verified["primarySystemId"] != verified["witnessSystemId"], "health-shared-cluster")
    for name in POLICY_SECONDS:
        require(_positive(verified[name], MAX_POLICY_SECONDS), "health-policy-range")


def _check_evidence(verified: JsonObject, now: datetime | None) -> None:
    tip = verified["witnessTipSequence"]
    checked, until = timestamp(verified["checkedAt"]), timestamp(verified["validUntil"])
    current = now or datetime.now(UTC)
    require(
        checked <= current < until <= checked + timedelta(minutes=VALIDITY_MINUTES),
        "health-expired",
    )
    for cluster in ("primary", "witness"):
        _base(verified[cluster + "Base"], checked, verified["maximumBackupAgeSeconds"])
        _wal(verified[cluster + "Wal"], checked, verified["maximumWalLagSeconds"])
    _checkpoint(verified["checkpoint"], checked, verified["maximumCheckpointAgeSeconds"], tip)
    _restore(verified["testRestore"], checked, verified["maximumRestoreTestAgeSeconds"], tip)
    require(
        type(verified["artifactCutoffSequence"]) is int
        and 0 <= verified["artifactCutoffSequence"] <= tip,
        "health-artifact-cutoff",
    )
    _fence(verified["writerFence"], verified["writerGeneration"], tip)


def parse_health(
    raw: bytes,
    signature: bytes,
    pinned_public_key: str | Path,
    expected: JsonObject,
    *,
    now: datetime | None = None,
    required_scope: str = "synthetic-only",
    trusted_key_sha256: str | None = None,
) -> tuple[JsonObject, str]:
    """Parse necessary evidence only; product supplies the immutable witnessed key pin."""
    if required_scope == "full":
        require(trusted_key_sha256 is not None, "health-trust-unavailable")
        public = private_path(pinned_public_key)
        require(
            hashlib.sha256(public.read_bytes()).hexdigest() == trusted_key_sha256,
            "health-trust-mismatch",
        )
    verified, digest = _verify_envelope(raw, signature, pinned_public_key)
    _check_identity(verified, expected, required_scope)
    _check_policy(verified)
    _check_evidence(verified, now)
    return verified, digest

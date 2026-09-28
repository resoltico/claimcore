"""Strict signed backup-health evidence codec; no issuer or readiness decision."""

import base64
import hashlib
import json
import re
import uuid
from datetime import datetime, timedelta, timezone

from deployment_common import canonical, private_path, require, timestamp, verify

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


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (ValueError, TypeError, AttributeError):
        return False


def _sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _lsn(value):
    return (
        isinstance(value, str)
        and re.fullmatch(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}", value) is not None
    )


def _positive(value, maximum):
    return type(value) is int and 1 <= value <= maximum


def _age(value, checked, limit, category):
    observed = timestamp(value)
    require(
        observed <= checked and checked - observed <= timedelta(seconds=limit), category
    )


def _base(value, checked, age):
    require(isinstance(value, dict) and set(value) == BASE_FIELDS, "health-base-shape")
    require(
        _uuid(value["copyId"])
        and _positive(value["revision"], 2**63 - 1)
        and value["revision"] >= 2
        and _sha(value["physicalReceiptSha256"]),
        "health-base-evidence",
    )
    _age(value["verifiedAt"], checked, age, "health-base-stale")


def _wal(value, checked, age):
    require(isinstance(value, dict) and set(value) == WAL_FIELDS, "health-wal-shape")
    ids = value["copyIds"]
    require(
        isinstance(ids, list)
        and 1 <= len(ids) <= 1000
        and all(_uuid(item) for item in ids)
        and ids == sorted(set(ids))
        and _lsn(value["registeredHorizon"])
        and _sha(value["archiveInspectionSha256"]),
        "health-wal-evidence",
    )
    _age(value["verifiedAt"], checked, age, "health-wal-stale")


def _checkpoint(value, checked, age, tip):
    require(
        isinstance(value, dict) and set(value) == CHECKPOINT_FIELDS,
        "health-checkpoint-shape",
    )
    require(
        type(value["sequence"]) is int
        and 0 <= value["sequence"] <= tip
        and _sha(value["hash"])
        and _sha(value["objectSha256"]),
        "health-checkpoint-evidence",
    )
    _age(value["verifiedAt"], checked, age, "health-checkpoint-stale")


def _restore(value, checked, age, tip):
    require(
        isinstance(value, dict) and set(value) == RESTORE_FIELDS, "health-restore-shape"
    )
    require(
        _sha(value["reportSha256"])
        and _sha(value["witnessCutoffHash"])
        and type(value["witnessCutoff"]) is int
        and 0 <= value["witnessCutoff"] <= tip,
        "health-restore-evidence",
    )
    _age(value["verifiedAt"], checked, age, "health-restore-stale")


def _fence(value, generation, tip):
    require(
        isinstance(value, dict) and set(value) == FENCE_FIELDS, "health-fence-shape"
    )
    if generation == 1:
        require(
            value["kind"] == "GENESIS"
            and all(value[name] is None for name in FENCE_FIELDS - {"kind"}),
            "health-fence-genesis",
        )
        return
    require(
        value["kind"] == "HANDOFF" and _uuid(value["handoffId"]), "health-fence-handoff"
    )
    require(
        value["oldGeneration"] == generation - 1
        and value["newGeneration"] == generation
        and type(value["w1Sequence"]) is int
        and type(value["activationSequence"]) is int
        and 0 < value["w1Sequence"] < value["activationSequence"] <= tip
        and _sha(value["w1Hash"])
        and _sha(value["activationHash"]),
        "health-fence-activation",
    )


def parse_health(
    raw,
    signature,
    pinned_public_key,
    expected,
    *,
    now=None,
    required_scope="synthetic-only",
    trusted_key_sha256=None,
):
    """Parse necessary evidence only; product supplies the immutable witnessed key pin."""
    if required_scope == "full":
        require(trusted_key_sha256 is not None, "health-trust-unavailable")
        public = private_path(pinned_public_key)
        require(
            hashlib.sha256(public.read_bytes()).hexdigest() == trusted_key_sha256,
            "health-trust-mismatch",
        )
    require(isinstance(raw, bytes) and 0 < len(raw) <= 65536, "health-size")
    require(
        isinstance(signature, bytes) and len(signature) == 64, "health-signature-size"
    )
    document = json.loads(raw)
    require(
        isinstance(document, dict) and raw == canonical(document), "health-canonical"
    )
    verified, digest = verify(
        {
            "report": document,
            "signatureBase64": base64.b64encode(signature).decode("ascii"),
        },
        pinned_public_key,
    )
    require(set(verified) == FIELDS, "health-shape")
    require(
        verified["format"] == "claimcore-backup-health-1"
        and verified["source"] == "ClaimCore.Database"
        and verified["scope"] in ("full", "synthetic-only")
        and verified["scope"] == required_scope,
        "health-scope",
    )
    require(
        _uuid(verified["installationId"])
        and _uuid(verified["lineageId"])
        and _uuid(verified["signerKeyId"])
        and _uuid(verified["signerHolderActorId"])
        and verified["installationId"] == expected["installationId"]
        and verified["lineageId"] == expected["lineageId"]
        and verified["epoch"] == expected["epoch"]
        and verified["writerGeneration"] == expected["writerGeneration"],
        "health-installation",
    )
    for name in ("epoch", "writerGeneration", "authorityRevision"):
        require(_positive(verified[name], 2**63 - 1), "health-authority")
    tip = verified["witnessTipSequence"]
    require(
        type(tip) is int and tip >= 0 and _sha(verified["witnessTipHash"]), "health-tip"
    )
    require(
        re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:-]{0,79}", verified["policyId"])
        and _sha(verified["knownCopyInventorySha256"]),
        "health-policy",
    )
    for name in ("primary", "witness"):
        require(
            isinstance(verified[name + "SystemId"], str)
            and re.fullmatch(r"[0-9]{1,20}", verified[name + "SystemId"])
            and _positive(verified[name + "Timeline"], 2**31 - 1),
            "health-cluster",
        )
    require(
        verified["primarySystemId"] != verified["witnessSystemId"],
        "health-shared-cluster",
    )
    for name in (
        "maximumBackupAgeSeconds",
        "maximumWalLagSeconds",
        "maximumCheckpointAgeSeconds",
        "maximumRestoreTestAgeSeconds",
        "restoreHorizonSeconds",
    ):
        require(_positive(verified[name], 365 * 86400), "health-policy-range")
    checked, until = timestamp(verified["checkedAt"]), timestamp(verified["validUntil"])
    now = now or datetime.now(timezone.utc)
    require(checked <= now < until <= checked + timedelta(minutes=5), "health-expired")
    for cluster in ("primary", "witness"):
        _base(verified[cluster + "Base"], checked, verified["maximumBackupAgeSeconds"])
        _wal(verified[cluster + "Wal"], checked, verified["maximumWalLagSeconds"])
    _checkpoint(
        verified["checkpoint"], checked, verified["maximumCheckpointAgeSeconds"], tip
    )
    _restore(
        verified["testRestore"], checked, verified["maximumRestoreTestAgeSeconds"], tip
    )
    require(
        type(verified["artifactCutoffSequence"]) is int
        and 0 <= verified["artifactCutoffSequence"] <= tip,
        "health-artifact-cutoff",
    )
    _fence(verified["writerFence"], verified["writerGeneration"], tip)
    return verified, digest

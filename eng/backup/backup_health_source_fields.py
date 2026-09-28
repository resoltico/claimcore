"""Exact signed source field sets and scalar/physical-copy decoders."""

import re
import uuid

from deployment_common import require, timestamp

POLICY_FIELDS = {
    "format",
    "policyId",
    "backupIntervalSeconds",
    "maximumBackupAgeSeconds",
    "maximumWalLagSeconds",
    "maximumCheckpointAgeSeconds",
    "maximumRestoreTestAgeSeconds",
    "restoreHorizonSeconds",
    "archiveRoot",
    "checkpointRoot",
    "restoreRoot",
    "roles",
}
ROLE_FIELDS = {
    "role",
    "publicKeyBase64",
    "machineSha256",
    "storageSha256",
    "adminActorId",
    "signerHolderActorId",
}
SOURCE_FIELDS = {
    "format",
    "cycleId",
    "leaseId",
    "captureNonce",
    "captureReceiptSha256",
    "backupCaptureSequence",
    "backupCaptureHash",
    "installationId",
    "lineageId",
    "epoch",
    "writerGeneration",
    "policyId",
    "authorityRevision",
    "witnessTipSequence",
    "witnessTipHash",
    "knownCopyInventorySha256",
    "artifactCutoffSequence",
    "checkedAt",
    "validUntil",
    "objects",
    "checkpoint",
    "testRestore",
}
OBJECT_FIELDS = {
    "copyId",
    "revision",
    "cluster",
    "kind",
    "postgresSystemId",
    "timeline",
    "walSegmentBytes",
    "walHorizon",
    "walSegment",
    "ciphertextSha256",
    "ciphertextBytes",
    "physicalReceiptSha256",
    "relativePath",
    "verifiedAt",
}
CHECKPOINT_FIELDS = {
    "sequence",
    "hash",
    "objectSha256",
    "objectBytes",
    "relativePath",
    "verifiedAt",
}
RESTORE_FIELDS = {
    "reportSha256",
    "reportRelativePath",
    "fullAuditSha256",
    "auditRelativePath",
    "witnessCutoff",
    "witnessCutoffHash",
    "primaryBaseCopyId",
    "witnessBaseCopyId",
    "primaryWalHorizon",
    "witnessWalHorizon",
    "primarySystemId",
    "primaryTimeline",
    "witnessSystemId",
    "witnessTimeline",
    "verifiedAt",
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


def _relative(value):
    return (
        isinstance(value, str)
        and re.fullmatch(r"[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+){0,3}", value) is not None
        and all(part not in (".", "..") for part in value.split("/"))
    )


def _object(value):
    require(
        isinstance(value, dict) and set(value) == OBJECT_FIELDS,
        "health-source-object-shape",
    )
    require(
        _uuid(value["copyId"])
        and value["cluster"] in ("PRIMARY", "WITNESS")
        and value["kind"] in ("BASE", "WAL"),
        "health-source-object-id",
    )
    require(
        type(value["revision"]) is int and value["revision"] >= 2,
        "health-source-object-revision",
    )
    require(
        isinstance(value["postgresSystemId"], str)
        and re.fullmatch(r"[0-9]{1,20}", value["postgresSystemId"]),
        "health-source-system",
    )
    require(
        type(value["timeline"]) is int and value["timeline"] > 0,
        "health-source-timeline",
    )
    size = value["walSegmentBytes"]
    require(
        type(size) is int and 1048576 <= size <= 1073741824 and size & (size - 1) == 0,
        "health-source-segment-size",
    )
    require(
        _lsn(value["walHorizon"])
        and _sha(value["ciphertextSha256"])
        and _sha(value["physicalReceiptSha256"]),
        "health-source-object-digest",
    )
    require(
        type(value["ciphertextBytes"]) is int
        and 1 <= value["ciphertextBytes"] <= 1099511627776,
        "health-source-object-size",
    )
    require(_relative(value["relativePath"]), "health-source-object-path")
    segment = value["walSegment"]
    require(
        (
            segment is None
            if value["kind"] == "BASE"
            else isinstance(segment, str) and re.fullmatch(r"[0-9A-F]{24}", segment)
        ),
        "health-source-wal-segment",
    )
    timestamp(value["verifiedAt"])

"""Exact signed source field sets and scalar/physical-copy decoders."""

import re

from backup_types import Json, JsonObject
from deployment_common import is_sha256, is_uuid, require, timestamp

MIN_SEGMENT_BYTES = 1048576
MAX_SEGMENT_BYTES = 1073741824
MAX_CIPHERTEXT_BYTES = 1099511627776
MIN_OBJECT_REVISION = 2

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


def _lsn(value: Json) -> bool:
    return (
        isinstance(value, str) and re.fullmatch(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}", value) is not None
    )


def is_relative_path(value: Json) -> bool:
    """Return whether `value` is a short relative path without dot segments."""
    return (
        isinstance(value, str)
        and re.fullmatch(r"[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+){0,3}", value) is not None
        and all(part not in (".", "..") for part in value.split("/"))
    )


def _check_object_identity(value: JsonObject) -> None:
    require(
        is_uuid(value["copyId"])
        and value["cluster"] in ("PRIMARY", "WITNESS")
        and value["kind"] in ("BASE", "WAL"),
        "health-source-object-id",
    )
    require(
        type(value["revision"]) is int and value["revision"] >= MIN_OBJECT_REVISION,
        "health-source-object-revision",
    )
    require(
        isinstance(value["postgresSystemId"], str)
        and re.fullmatch(r"[0-9]{1,20}", value["postgresSystemId"]),
        "health-source-system",
    )
    require(type(value["timeline"]) is int and value["timeline"] > 0, "health-source-timeline")


def _check_object_bytes(value: JsonObject) -> None:
    size = value["walSegmentBytes"]
    require(
        type(size) is int
        and MIN_SEGMENT_BYTES <= size <= MAX_SEGMENT_BYTES
        and size & (size - 1) == 0,
        "health-source-segment-size",
    )
    require(
        _lsn(value["walHorizon"])
        and is_sha256(value["ciphertextSha256"])
        and is_sha256(value["physicalReceiptSha256"]),
        "health-source-object-digest",
    )
    require(
        type(value["ciphertextBytes"]) is int
        and 1 <= value["ciphertextBytes"] <= MAX_CIPHERTEXT_BYTES,
        "health-source-object-size",
    )
    require(is_relative_path(value["relativePath"]), "health-source-object-path")


def check_object(value: JsonObject) -> None:
    """Refuse a signed source object whose fields are not exactly well formed."""
    require(isinstance(value, dict) and set(value) == OBJECT_FIELDS, "health-source-object-shape")
    _check_object_identity(value)
    _check_object_bytes(value)
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

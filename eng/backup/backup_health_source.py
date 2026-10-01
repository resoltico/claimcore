"""Canonical independently signed current backup-health source claims."""

import json
from datetime import UTC, datetime, timedelta

from backup_health_source_fields import (
    CHECKPOINT_FIELDS,
    RESTORE_FIELDS,
    SOURCE_FIELDS,
    check_object,
    is_relative_path,
)
from backup_types import JsonObject
from deployment_common import canonical, is_sha256, is_uuid, require, timestamp

SOURCE_LIMIT = 131072
CHECKPOINT_LIMIT = 16777216
VALIDITY_MINUTES = 5
MIN_OBJECTS = 4
MAX_OBJECTS = 1000
POSITIVE = (
    "epoch",
    "writerGeneration",
    "authorityRevision",
    "backupCaptureSequence",
    "witnessTipSequence",
)
DIGESTS = (
    "captureNonce",
    "captureReceiptSha256",
    "backupCaptureHash",
    "witnessTipHash",
    "knownCopyInventorySha256",
)


def _check_identity(source: JsonObject, policy: JsonObject, now: datetime | None) -> None:
    require(
        set(source) == SOURCE_FIELDS and source["format"] == "claimcore-backup-health-source-1",
        "health-source-shape",
    )
    for name in ("cycleId", "leaseId", "installationId", "lineageId"):
        require(is_uuid(source[name]), "health-source-identity")
    for name in DIGESTS:
        require(is_sha256(source[name]), "health-source-digest")
    for name in POSITIVE:
        require(type(source[name]) is int and source[name] >= 1, "health-source-authority")
    require(
        source["policyId"] == policy["policyId"]
        and source["backupCaptureSequence"] <= source["witnessTipSequence"],
        "health-source-authority",
    )
    checked, until = timestamp(source["checkedAt"]), timestamp(source["validUntil"])
    current = now or datetime.now(UTC)
    require(
        checked <= current < until <= checked + timedelta(minutes=VALIDITY_MINUTES),
        "health-source-expired",
    )


def _check_objects(objects: list[JsonObject]) -> None:
    require(
        isinstance(objects, list) and MIN_OBJECTS <= len(objects) <= MAX_OBJECTS,
        "health-source-object-count",
    )
    for item in objects:
        check_object(item)
    pairs = [(item["cluster"], item["kind"]) for item in objects]
    require(
        pairs.count(("PRIMARY", "BASE")) == pairs.count(("WITNESS", "BASE")) == 1,
        "health-source-base-pair",
    )
    require(
        pairs.count(("PRIMARY", "WAL")) >= 1 and pairs.count(("WITNESS", "WAL")) >= 1,
        "health-source-wal-pair",
    )
    require(
        len({item["copyId"] for item in objects}) == len(objects), "health-source-copy-duplicate"
    )
    require(
        len({item["relativePath"] for item in objects}) == len(objects),
        "health-source-path-duplicate",
    )


def _check_checkpoint(source: JsonObject) -> None:
    checkpoint = source["checkpoint"]
    require(
        isinstance(checkpoint, dict) and set(checkpoint) == CHECKPOINT_FIELDS,
        "health-source-checkpoint",
    )
    require(
        checkpoint["sequence"] == source["backupCaptureSequence"]
        and checkpoint["hash"] == source["backupCaptureHash"],
        "health-source-checkpoint-cutoff",
    )
    require(
        is_sha256(checkpoint["objectSha256"])
        and is_relative_path(checkpoint["relativePath"])
        and checkpoint["relativePath"] == source["cycleId"] + ".json",
        "health-source-checkpoint-object",
    )
    require(
        type(checkpoint["objectBytes"]) is int
        and 1 <= checkpoint["objectBytes"] <= CHECKPOINT_LIMIT,
        "health-source-checkpoint-object",
    )


def _base_id(objects: list[JsonObject], cluster: str) -> str:
    found: str = next(
        item["copyId"] for item in objects if (item["cluster"], item["kind"]) == (cluster, "BASE")
    )
    return found


def _check_restore(source: JsonObject, objects: list[JsonObject]) -> None:
    restored = source["testRestore"]
    require(isinstance(restored, dict) and set(restored) == RESTORE_FIELDS, "health-source-restore")
    for name in ("reportSha256", "fullAuditSha256", "witnessCutoffHash"):
        require(is_sha256(restored[name]), "health-source-restore-digest")
    for name in ("reportRelativePath", "auditRelativePath"):
        require(is_relative_path(restored[name]), "health-source-restore-path")
    require(
        type(restored["witnessCutoff"]) is int
        and 0 <= restored["witnessCutoff"] <= source["witnessTipSequence"],
        "health-source-restore-cutoff",
    )
    require(
        restored["primaryBaseCopyId"] == _base_id(objects, "PRIMARY"),
        "health-source-restore-primary",
    )
    require(
        restored["witnessBaseCopyId"] == _base_id(objects, "WITNESS"),
        "health-source-restore-witness",
    )
    for cluster in ("primary", "witness"):
        base = next(
            item for item in objects if (item["cluster"], item["kind"]) == (cluster.upper(), "BASE")
        )
        wal = [
            item for item in objects if (item["cluster"], item["kind"]) == (cluster.upper(), "WAL")
        ]
        require(
            restored[cluster + "SystemId"] == base["postgresSystemId"]
            and restored[cluster + "Timeline"] == base["timeline"],
            "health-source-restore-identity",
        )
        require(
            all(
                item["postgresSystemId"] == base["postgresSystemId"]
                and item["timeline"] == base["timeline"]
                and item["walSegmentBytes"] == base["walSegmentBytes"]
                and item["walHorizon"] == restored[cluster + "WalHorizon"]
                for item in wal
            ),
            "health-source-wal-identity",
        )


def parse_source(raw: bytes, policy: JsonObject, *, now: datetime | None = None) -> JsonObject:
    """Parse the canonical health source claims, cross-checking objects, checkpoint and restore."""
    require(isinstance(raw, bytes) and 0 < len(raw) <= SOURCE_LIMIT, "health-source-size")
    source: JsonObject = json.loads(raw)
    require(isinstance(source, dict) and raw == canonical(source), "health-source-canonical")
    _check_identity(source, policy, now)
    objects = source["objects"]
    _check_objects(objects)
    _check_checkpoint(source)
    _check_restore(source, objects)
    require(
        source["checkpoint"]["sequence"] <= source["witnessTipSequence"],
        "health-source-checkpoint-cutoff",
    )
    return source

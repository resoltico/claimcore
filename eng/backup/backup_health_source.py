"""Canonical independently signed current backup-health source claims."""

import json
from datetime import datetime, timedelta, timezone

from backup_health_source_fields import (
    CHECKPOINT_FIELDS,
    RESTORE_FIELDS,
    SOURCE_FIELDS,
    _object,
    _relative,
    _sha,
    _uuid,
)
from deployment_common import canonical, require, timestamp


def parse_source(raw, policy, *, now=None):
    require(isinstance(raw, bytes) and 0 < len(raw) <= 131072, "health-source-size")
    source = json.loads(raw)
    require(
        isinstance(source, dict) and raw == canonical(source), "health-source-canonical"
    )
    require(
        set(source) == SOURCE_FIELDS
        and source["format"] == "claimcore-backup-health-source-1",
        "health-source-shape",
    )
    for name in ("cycleId", "leaseId", "installationId", "lineageId"):
        require(_uuid(source[name]), "health-source-identity")
    for name in (
        "captureNonce",
        "captureReceiptSha256",
        "backupCaptureHash",
        "witnessTipHash",
        "knownCopyInventorySha256",
    ):
        require(_sha(source[name]), "health-source-digest")
    for name in (
        "epoch",
        "writerGeneration",
        "authorityRevision",
        "backupCaptureSequence",
        "witnessTipSequence",
    ):
        require(
            type(source[name]) is int and source[name] >= 1, "health-source-authority"
        )
    require(
        source["policyId"] == policy["policyId"]
        and source["backupCaptureSequence"] <= source["witnessTipSequence"],
        "health-source-authority",
    )
    checked, until = timestamp(source["checkedAt"]), timestamp(source["validUntil"])
    now = now or datetime.now(timezone.utc)
    require(
        checked <= now < until <= checked + timedelta(minutes=5),
        "health-source-expired",
    )
    objects = source["objects"]
    require(
        isinstance(objects, list) and 4 <= len(objects) <= 1000,
        "health-source-object-count",
    )
    for item in objects:
        _object(item)
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
        len({item["copyId"] for item in objects}) == len(objects),
        "health-source-copy-duplicate",
    )
    require(
        len({item["relativePath"] for item in objects}) == len(objects),
        "health-source-path-duplicate",
    )
    checkpoint, restored = source["checkpoint"], source["testRestore"]
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
        _sha(checkpoint["objectSha256"])
        and _relative(checkpoint["relativePath"])
        and checkpoint["relativePath"] == source["cycleId"] + ".json",
        "health-source-checkpoint-object",
    )
    require(
        type(checkpoint["objectBytes"]) is int
        and 1 <= checkpoint["objectBytes"] <= 16777216,
        "health-source-checkpoint-object",
    )
    require(
        isinstance(restored, dict) and set(restored) == RESTORE_FIELDS,
        "health-source-restore",
    )
    for name in ("reportSha256", "fullAuditSha256", "witnessCutoffHash"):
        require(_sha(restored[name]), "health-source-restore-digest")
    for name in ("reportRelativePath", "auditRelativePath"):
        require(_relative(restored[name]), "health-source-restore-path")
    require(
        type(restored["witnessCutoff"]) is int
        and 0 <= restored["witnessCutoff"] <= source["witnessTipSequence"],
        "health-source-restore-cutoff",
    )
    require(
        restored["primaryBaseCopyId"]
        == next(
            item["copyId"]
            for item in objects
            if (item["cluster"], item["kind"]) == ("PRIMARY", "BASE")
        ),
        "health-source-restore-primary",
    )
    require(
        restored["witnessBaseCopyId"]
        == next(
            item["copyId"]
            for item in objects
            if (item["cluster"], item["kind"]) == ("WITNESS", "BASE")
        ),
        "health-source-restore-witness",
    )
    for cluster in ("primary", "witness"):
        base = next(
            item
            for item in objects
            if (item["cluster"], item["kind"]) == (cluster.upper(), "BASE")
        )
        wal = [
            item
            for item in objects
            if (item["cluster"], item["kind"]) == (cluster.upper(), "WAL")
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
    require(
        source["checkpoint"]["sequence"] <= source["witnessTipSequence"],
        "health-source-checkpoint-cutoff",
    )
    return source

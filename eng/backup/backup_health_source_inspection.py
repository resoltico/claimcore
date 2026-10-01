"""Role-specific handle-first observation before independent health-source signing."""

import json
from pathlib import Path

from backup_health_source_io import hash_private, read_private
from backup_types import Json, JsonObject
from deployment_common import canonical, is_sha256, private_path, require

LOG_SHIFT = 32
MAX_WAL_RANGE = 1000
REPORT_LIMIT = 131072
AUDIT_LIMIT = 1048576


def _unique_object(pairs: list[tuple[str, Json]]) -> JsonObject:
    result: JsonObject = {}
    for name, value in pairs:
        require(name not in result, "health-source-duplicate-field")
        result[name] = value
    return result


def _position(value: str) -> int:
    high, low = value.split("/")
    return (int(high, 16) << LOG_SHIFT) + int(low, 16)


def _expected_prefix(base: JsonObject, wal: list[JsonObject]) -> list[str]:
    size = base["walSegmentBytes"]
    start = _position(base["walHorizon"]) // size
    endpoint = _position(wal[0]["walHorizon"])
    require(endpoint > _position(base["walHorizon"]), "health-source-wal-range")
    last = (endpoint - 1) // size
    require(start <= last and last - start < MAX_WAL_RANGE, "health-source-wal-range")
    per_log = (1 << LOG_SHIFT) // size
    return [
        f"{base['timeline']:08X}{number // per_log:08X}{number % per_log:08X}"
        for number in range(start, last + 1)
    ]


def inspect_archive(source: JsonObject, policy: JsonObject) -> None:
    """Prove every archived object and that each cluster's WAL is a contiguous prefix."""
    root = private_path(policy["archiveRoot"], directory=True)
    for item in source["objects"]:
        digest, size = hash_private(root / item["relativePath"], item["ciphertextBytes"])
        require(
            (digest, size) == (item["ciphertextSha256"], item["ciphertextBytes"]),
            "health-source-archive-object",
        )
    for cluster in ("PRIMARY", "WITNESS"):
        base = next(
            item
            for item in source["objects"]
            if (item["cluster"], item["kind"]) == (cluster, "BASE")
        )
        wal = [
            item
            for item in source["objects"]
            if (item["cluster"], item["kind"]) == (cluster, "WAL")
        ]
        actual = sorted(item["walSegment"] for item in wal)
        require(actual == _expected_prefix(base, wal), "health-source-wal-prefix")


def inspect_checkpoint(source: JsonObject, policy: JsonObject) -> None:
    """Prove the checkpoint object's bytes and its binding to the capture."""
    root = private_path(policy["checkpointRoot"], directory=True)
    item = source["checkpoint"]
    path = root / item["relativePath"]
    require(
        hash_private(path, item["objectBytes"]) == (item["objectSha256"], item["objectBytes"]),
        "health-source-checkpoint-object",
    )
    raw = read_private(path, item["objectBytes"])
    checkpoint = json.loads(raw)
    require(
        isinstance(checkpoint, dict) and raw == canonical(checkpoint),
        "health-source-checkpoint-canonical",
    )
    require(
        checkpoint.get("format") == "claimcore-witness-checkpoint-1",
        "health-source-checkpoint-format",
    )
    for name in ("installationId", "lineageId", "epoch", "writerGeneration"):
        require(checkpoint.get(name) == source[name], "health-source-checkpoint-identity")
    for left, right in (
        ("sequence", "backupCaptureSequence"),
        ("hash", "backupCaptureHash"),
        ("cycleId", "cycleId"),
        ("leaseId", "leaseId"),
        ("captureNonce", "captureNonce"),
    ):
        require(checkpoint.get(left) == source[right], "health-source-checkpoint-binding")
    require(
        is_sha256(checkpoint.get("maintenanceEvidenceSha256")),
        "health-source-checkpoint-maintenance",
    )


def _check_report(root: Path, source: JsonObject, restored: JsonObject) -> None:
    report_raw = read_private(root / restored["reportRelativePath"], REPORT_LIMIT)
    report = json.loads(report_raw)
    require(
        isinstance(report, dict) and report_raw == canonical(report),
        "health-source-restore-canonical",
    )
    require(
        report.get("format") == "claimcore-restore-qualification-1"
        and report.get("scope") == "full",
        "health-source-restore-format",
    )
    for name in ("installationId", "lineageId", "epoch"):
        require(report.get(name) == source[name], "health-source-restore-identity")
    for cluster in ("primary", "witness"):
        require(
            report.get(cluster + "SystemId") == restored[cluster + "SystemId"]
            and report.get(cluster + "Timeline") == restored[cluster + "Timeline"]
            and report.get(cluster + "RegisteredWalHorizon") == restored[cluster + "WalHorizon"],
            "health-source-restore-cluster",
        )
    require(
        report.get("witnessCutoff") == restored["witnessCutoff"]
        and report.get("witnessCutoffHash") == restored["witnessCutoffHash"],
        "health-source-restore-cutoff",
    )


def _check_audit(root: Path, source: JsonObject, restored: JsonObject) -> None:
    audit_raw = read_private(root / restored["auditRelativePath"], AUDIT_LIMIT)
    audit = json.loads(audit_raw, object_pairs_hook=_unique_object)
    require(isinstance(audit, dict), "health-source-audit-shape")
    require(
        audit.get("kind") == "dataAuditResult"
        and audit.get("command") == "VERIFY_DATA"
        and audit.get("status") == "VERIFIED",
        "health-source-audit-status",
    )
    require(
        audit.get("installationId") == source["installationId"]
        and audit.get("lineageId") == source["lineageId"]
        and audit.get("epoch") == source["epoch"],
        "health-source-audit-identity",
    )
    require(
        audit.get("witnessCutoff") == restored["witnessCutoff"]
        and audit.get("witnessTipHash") == restored["witnessCutoffHash"],
        "health-source-audit-cutoff",
    )


def inspect_restore(source: JsonObject, policy: JsonObject) -> None:
    """Prove the test-restore report and audit objects and their agreement with the source."""
    root = private_path(policy["restoreRoot"], directory=True)
    restored = source["testRestore"]
    for path_name, digest_name, maximum in (
        ("reportRelativePath", "reportSha256", REPORT_LIMIT),
        ("auditRelativePath", "fullAuditSha256", AUDIT_LIMIT),
    ):
        require(
            hash_private(root / restored[path_name], maximum)[0] == restored[digest_name],
            "health-source-restore-object",
        )
    _check_report(root, source, restored)
    _check_audit(root, source, restored)

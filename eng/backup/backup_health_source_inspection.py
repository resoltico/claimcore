"""Role-specific handle-first observation before independent health-source signing."""

import json

from backup_health_source_fields import _sha
from backup_health_source_io import _hash, _read
from deployment_common import canonical, private_path, require


def _unique_object(pairs):
    result = {}
    for name, value in pairs:
        require(name not in result, "health-source-duplicate-field")
        result[name] = value
    return result


def inspect_archive(source, policy):
    root = private_path(policy["archiveRoot"], directory=True)
    for item in source["objects"]:
        digest, size = _hash(root / item["relativePath"], item["ciphertextBytes"])
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
        size = base["walSegmentBytes"]
        start = _position(base["walHorizon"]) // size
        endpoint = _position(wal[0]["walHorizon"])
        require(endpoint > _position(base["walHorizon"]), "health-source-wal-range")
        last = (endpoint - 1) // size
        require(start <= last and last - start < 1000, "health-source-wal-range")
        per_log = (1 << 32) // size
        expected = [
            f"{base['timeline']:08X}{number // per_log:08X}{number % per_log:08X}"
            for number in range(start, last + 1)
        ]
        actual = sorted(item["walSegment"] for item in wal)
        require(actual == expected, "health-source-wal-prefix")


def _position(value):
    high, low = value.split("/")
    return (int(high, 16) << 32) + int(low, 16)


def inspect_checkpoint(source, policy):
    root = private_path(policy["checkpointRoot"], directory=True)
    item = source["checkpoint"]
    path = root / item["relativePath"]
    require(
        _hash(path, item["objectBytes"]) == (item["objectSha256"], item["objectBytes"]),
        "health-source-checkpoint-object",
    )
    raw = _read(path, item["objectBytes"])
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
        require(
            checkpoint.get(name) == source[name], "health-source-checkpoint-identity"
        )
    for left, right in (
        ("sequence", "backupCaptureSequence"),
        ("hash", "backupCaptureHash"),
        ("cycleId", "cycleId"),
        ("leaseId", "leaseId"),
        ("captureNonce", "captureNonce"),
    ):
        require(
            checkpoint.get(left) == source[right], "health-source-checkpoint-binding"
        )
    require(
        _sha(checkpoint.get("maintenanceEvidenceSha256")),
        "health-source-checkpoint-maintenance",
    )


def inspect_restore(source, policy):
    root = private_path(policy["restoreRoot"], directory=True)
    restored = source["testRestore"]
    for path_name, digest_name, maximum in (
        ("reportRelativePath", "reportSha256", 131072),
        ("auditRelativePath", "fullAuditSha256", 1048576),
    ):
        path = root / restored[path_name]
        require(
            _hash(path, maximum)[0] == restored[digest_name],
            "health-source-restore-object",
        )
    report_raw = _read(root / restored["reportRelativePath"], 131072)
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
            and report.get(cluster + "RegisteredWalHorizon")
            == restored[cluster + "WalHorizon"],
            "health-source-restore-cluster",
        )
    require(
        report.get("witnessCutoff") == restored["witnessCutoff"]
        and report.get("witnessCutoffHash") == restored["witnessCutoffHash"],
        "health-source-restore-cutoff",
    )
    audit_raw = _read(root / restored["auditRelativePath"], 1048576)
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

"""Functional restore proof: decrypt, extract, verify and hand the pair to the restore verifier."""

import hashlib
import json
import re
import subprocess
import tarfile
import tempfile
from pathlib import Path

from backup_types import JsonObject
from managed_common import (
    CLUSTERS,
    BackupFailureError,
    decrypt_blocks,
    digest,
    private_path,
    require,
    tool,
)

REPORT_LINE = rb"synthetic restore verifier failed at line ([0-9]{1,3})"
REPORT_FORMAT = "claimcore-restored-pair-report-1"


def safe_extract(source: Path, destination: Path, max_bytes: int, max_entries: int) -> None:
    """Extract a backup tar only if every member is a bounded, contained file or directory."""
    with tarfile.open(source, mode="r:") as archive:
        expanded = 0
        for count, member in enumerate(archive, start=1):
            require(0 <= member.size <= max_bytes, "backup-member-size")
            expanded += member.size
            require(count <= max_entries and expanded <= max_bytes, "backup-expansion-limit")
            name = Path(member.name)
            require(not name.is_absolute() and ".." not in name.parts, "unsafe-backup-member")
            require(member.isfile() or member.isdir(), "unsupported-backup-member")
            target = destination / name
            require(target.resolve().is_relative_to(destination.resolve()), "unsafe-backup-member")
        archive.extractall(destination, filter="data")


def _decrypt_tar(config: JsonObject, ciphertext: Path, plain: Path) -> None:
    with plain.open("xb") as output:
        for block in decrypt_blocks(
            config, ciphertext, limit="backup-decryption-limit", failure="decrypt-failed"
        ):
            output.write(block)


def _check_pg_manifest(data: Path, record: JsonObject) -> None:
    pg_manifest_bytes = (data / "backup_manifest").read_bytes()
    pg_manifest = json.loads(pg_manifest_bytes)
    ranges = pg_manifest.get("WAL-Ranges")
    require(isinstance(ranges, list) and len(ranges) == 1, "backup-manifest-ranges")
    require(
        hashlib.sha256(pg_manifest_bytes).hexdigest() == record["backupManifestSha256"],
        "backup-manifest-attestation",
    )
    require(
        str(pg_manifest.get("System-Identifier")) == record["postgresSystemId"],
        "backup-system-identity",
    )
    require(
        ranges[0].get("Timeline") == record["timeline"]
        and ranges[0].get("Start-LSN") == record["walStartLsn"]
        and ranges[0].get("End-LSN") == record["walEndLsn"],
        "backup-wal-range-attestation",
    )


def _restore_cluster(
    config: JsonObject, work: Path, cluster: str, expected: tuple[JsonObject, JsonObject, Path]
) -> None:
    manifest, record, cycle = expected
    ciphertext = private_path(cycle / (cluster + ".tar.age"))
    require(digest(ciphertext) == manifest[cluster]["ciphertextSha256"], "ciphertext-digest")
    plain = work / (cluster + ".tar")
    _decrypt_tar(config, ciphertext, plain)
    data = work / cluster
    data.mkdir(mode=0o700)
    safe_extract(plain, data, config["maxBackupBytes"], config["maxTarEntries"])
    plain.unlink()
    _check_pg_manifest(data, record)
    result = subprocess.run(
        [tool("pg_verifybackup", "pg_verifybackup (PostgreSQL) 18.6"), str(data)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    require(result.returncode == 0, "pg-verifybackup-failed")


def _run_verifier(verifier: str, work: Path, sources: tuple[Path, Path], report_path: Path) -> None:
    manifest_file, checkpoint = sources
    result = subprocess.run(
        [
            verifier,
            str(work / "primary"),
            str(work / "witness"),
            str(manifest_file),
            str(checkpoint),
            str(report_path),
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        line = re.search(REPORT_LINE, result.stderr)
        reason = "test-restore-failed" + ("-line-" + line.group(1).decode("ascii") if line else "")
        raise BackupFailureError(reason)


def _check_report(report_path: Path, manifest: JsonObject, tip: JsonObject) -> None:
    require(report_path.is_file() and not report_path.is_symlink(), "restore-report-missing")
    report = json.loads(report_path.read_bytes())
    require(report.get("format") == REPORT_FORMAT, "restore-report-format")
    require(
        report.get("installationId") == manifest["installationId"]
        and report.get("lineageId") == manifest["lineageId"]
        and report.get("epoch") == manifest["epoch"],
        "restore-report-identity",
    )
    require(
        report.get("witnessCutoff") == tip["sequence"] and report.get("witnessHash") == tip["hash"],
        "restore-report-cutoff",
    )
    require(
        report.get("recoveredDataChecked") is True and report.get("pairCompared") is True,
        "restored-data-unchecked",
    )
    require(report.get("scope") == "synthetic-only", "restore-report-scope")


def restore_and_verify(
    config: JsonObject,
    verifier: str,
    evidence: tuple[JsonObject, dict[str, JsonObject], JsonObject],
    files: tuple[Path, Path, Path],
) -> None:
    """Restore both clusters into scratch space and require the restore verifier to pass."""
    manifest, attestations, tip = evidence
    cycle, manifest_file, checkpoint = files
    tool("age", "v1.3.2")
    tool("pg_verifybackup", "pg_verifybackup (PostgreSQL) 18.6")
    with tempfile.TemporaryDirectory(prefix="claimcore-restore-") as temporary:
        work = Path(temporary)
        work.chmod(0o700)
        for cluster in CLUSTERS:
            _restore_cluster(config, work, cluster, (manifest, attestations[cluster], cycle))
        report_path = work / "restore-report.json"
        _run_verifier(verifier, work, (manifest_file, checkpoint), report_path)
        _check_report(report_path, manifest, tip)

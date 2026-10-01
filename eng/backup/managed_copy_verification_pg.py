"""Isolated PostgreSQL BASE/WAL inspection; never an installation-readiness claim."""

import hashlib
import json
import re
from pathlib import Path

from backup_types import JsonObject
from managed_copy_boot import boot_identity
from managed_copy_source import extract_base, run_fixed
from managed_copy_verification_io import require, tool

MANIFEST_LIMIT = 32 * 1024 * 1024
MANIFEST_VERSION = 2
VERIFY_TIMEOUT_SECONDS = 180
WALDUMP_TIMEOUT_SECONDS = 60


def verify_base(config: JsonObject, decrypted: Path, work: Path) -> dict[str, bool | int | str]:
    """Extract, verify and boot a base backup, returning the checks that passed."""
    data = work / "data"
    data.mkdir(mode=0o700)
    extract_base(decrypted, data, config["maximumPlaintextBytes"], config["maximumTarEntries"])
    manifest_file = data / "backup_manifest"
    require(
        manifest_file.is_file() and manifest_file.stat().st_size <= MANIFEST_LIMIT,
        "BACKUP_MANIFEST_MISSING",
    )
    manifest_bytes = manifest_file.read_bytes()
    manifest = json.loads(manifest_bytes)
    ranges = manifest.get("WAL-Ranges")
    require(
        manifest.get("PostgreSQL-Backup-Manifest-Version") == MANIFEST_VERSION
        and str(manifest.get("System-Identifier")) == config["postgresSystemId"]
        and isinstance(ranges, list)
        and len(ranges) == 1,
        "BACKUP_MANIFEST_DIVERGED",
    )
    wal = ranges[0]
    require(
        wal.get("Timeline") == config["timeline"]
        and wal.get("Start-LSN") == config["walStartLsn"]
        and wal.get("End-LSN") == config["walEndLsn"]
        and hashlib.sha256(manifest_bytes).hexdigest() == config["backupManifestSha256"],
        "BACKUP_WAL_RANGE_DIVERGED",
    )
    run_fixed(
        [tool("pg_verifybackup", "pg_verifybackup (PostgreSQL) 18.6"), str(data)],
        VERIFY_TIMEOUT_SECONDS,
    )
    count = boot_identity(config, data)
    return {
        "pgVerifyBackup": True,
        "isolatedBoot": True,
        "recoveredIdentityChecked": True,
        "recoveredRowCount": count,
        "pgVerifyBackupManifestSha256": hashlib.sha256(manifest_bytes).hexdigest(),
        "walFirstRecordParsed": False,
        "walTimelineMatched": False,
    }


def verify_wal(config: JsonObject, decrypted: Path) -> dict[str, bool | None]:
    """Parse the first record of a WAL segment and match its timeline."""
    name = config["walSegment"]
    size = config["walSegmentBytes"]
    require(
        re.fullmatch(r"[0-9A-F]{24}", name) is not None
        and int(name[:8], 16) == config["timeline"]
        and decrypted.stat().st_size == size,
        "WAL_SEGMENT_DIVERGED",
    )
    renamed = decrypted.with_name(name)
    decrypted.rename(renamed)
    run_fixed(
        [tool("pg_waldump", "pg_waldump (PostgreSQL) 18.6"), "--quiet", "--limit=1", str(renamed)],
        WALDUMP_TIMEOUT_SECONDS,
    )
    return {
        "pgVerifyBackup": False,
        "isolatedBoot": False,
        "recoveredIdentityChecked": False,
        "recoveredRowCount": None,
        "pgVerifyBackupManifestSha256": None,
        "walFirstRecordParsed": True,
        "walTimelineMatched": True,
    }

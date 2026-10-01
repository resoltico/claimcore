"""Encrypted, idempotent archiving of one WAL segment without replacing an existing copy."""

import json
import os
import re
import subprocess
import uuid
from datetime import UTC, datetime
from pathlib import Path

import inventory
from backup_types import JsonObject
from managed_common import (
    CLUSTERS,
    BackupFailureError,
    digest,
    json_bytes,
    private_path,
    require,
    sync_directory,
    sync_file,
    tool,
    write_new,
)

WAL_NAME = r"(?:[0-9A-F]{24}|[0-9A-F]{8}\.history)"


def _encrypt_segment(config: JsonObject, source: Path, temporary: Path) -> None:
    with temporary.open("xb") as sealed:
        result = subprocess.run(
            [
                tool("age", "v1.3.2"),
                "--encrypt",
                "--recipient",
                config["ageRecipient"],
                str(source),
            ],
            stdout=sealed,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        sealed.flush()
        os.fsync(sealed.fileno())
    require(result.returncode == 0, "wal-encryption-failed")


def _confirm_duplicate(archive: Path, segment: str, target: Path, source_hash: str) -> None:
    require(not target.is_symlink(), "wal-target-invalid")
    record = archive / (segment + ".json")
    require(record.is_file() and not record.is_symlink(), "wal-duplicate-needs-review")
    existing_record = json.loads(record.read_bytes())
    require(
        existing_record.get("sourceSha256") == source_hash
        and existing_record.get("ciphertextSha256") == digest(target),
        "wal-duplicate-conflict",
    )
    sync_file(target)
    sync_file(record)
    sync_directory(archive)


def archive_wal(config: JsonObject, cluster: str, source: str | Path, segment: str) -> None:
    """Encrypt one WAL segment into the archive, idempotently and without replacing a copy."""
    require(cluster in CLUSTERS, "cluster-name")
    require(re.fullmatch(WAL_NAME, segment) is not None, "wal-name")
    origin = Path(source)
    require(origin.is_file() and not origin.is_symlink(), "wal-source")
    archive = config["archiveRoot"] / "wal" / cluster
    archive.mkdir(parents=True, mode=0o700, exist_ok=True)
    archive = private_path(archive, directory=True)
    target = archive / (segment + ".age")
    source_hash = digest(origin)
    if target.exists():
        _confirm_duplicate(archive, segment, target, source_hash)
        return
    temporary = archive / ("." + segment + "." + str(uuid.uuid4()))
    try:
        _encrypt_segment(config, origin, temporary)
        require(digest(origin) == source_hash, "wal-source-changed-during-archive")
        os.link(temporary, target, follow_symlinks=False)
    except FileExistsError:
        msg = "wal-concurrent-duplicate-needs-review"
        raise BackupFailureError(msg) from None
    finally:
        temporary.unlink(missing_ok=True)
    write_new(
        archive / (segment + ".json"),
        json_bytes(
            {
                "format": "claimcore-wal-copy-1",
                "cluster": cluster,
                "segment": segment,
                "sourceSha256": source_hash,
                "ciphertextSha256": digest(target),
                "archivedAt": inventory.utc_time(datetime.now(UTC)),
            }
        ),
    )
    sync_directory(archive)

"""Encrypted PostgreSQL base backups and the checks that read them back."""

import hashlib
import json
import os
import re
import subprocess
import tarfile
from pathlib import Path

from backup_types import JsonObject
from managed_common import (
    BackupFailureError,
    digest,
    metadata,
    pg_env,
    private_path,
    refuse,
    require,
    sync_directory,
    tool,
)

MANIFEST_LIMIT = 32 * 1024 * 1024
MIN_SEGMENT_BYTES = 1024 * 1024
MAX_SEGMENT_BYTES = 1024 * 1024 * 1024
SYSTEM_COLUMNS = 3
IDENTITY_PREFIX = 3
LINEAGE_PREFIX = 2
CONTROL_SQL = (
    "SELECT s.system_identifier::text, c.timeline_id::text, i.bytes_per_wal_segment::text "
    "FROM pg_control_system() s CROSS JOIN pg_control_checkpoint() c "
    "CROSS JOIN pg_control_init() i"
)
FAILURE_FRAGMENTS = (
    (b"pg_hba.conf", "hba"),
    (b"password authentication failed", "authentication"),
    (b"no password supplied", "authentication"),
    (b"must be superuser or replication role", "replication-role"),
    (b"permission denied", "permission"),
    (b"replication slot", "replication-slot"),
    (b"replication privileges", "replication-privileges"),
    (b"manifest", "manifest"),
    (b"stdout", "stdout"),
    (b"WAL", "wal"),
    (b"replication connection", "replication-connection"),
    (b"connection", "connection"),
    (b"replication", "replication"),
)


def _basebackup_category(pg_error: bytes) -> str:
    category = "basebackup-source-failed"
    for fragment, label in FAILURE_FRAGMENTS:
        if fragment in pg_error:
            return category + "-" + label
    return category


def _stream_backup(config: JsonObject, cluster: str, output: Path) -> tuple[int, int, bytes]:
    with output.open("xb") as sealed:
        age = subprocess.Popen(
            [tool("age", "v1.3.2"), "--encrypt", "--recipient", config["ageRecipient"]],
            stdin=subprocess.PIPE,
            stdout=sealed,
            stderr=subprocess.DEVNULL,
        )
        pg = subprocess.Popen(
            [
                tool("pg_basebackup", "pg_basebackup (PostgreSQL) 18.6"),
                "--pgdata=-",
                "--format=tar",
                "--wal-method=fetch",
                "--manifest-checksums=SHA256",
                "--no-password",
                "--checkpoint=fast",
            ],
            env=pg_env(config[cluster]["replicationService"]),
            stdout=age.stdin,
            stderr=subprocess.PIPE,
        )
        if age.stdin is not None:
            age.stdin.close()
        _, pg_error = pg.communicate()
        age_status = age.wait()
        sealed.flush()
        os.fsync(sealed.fileno())
    return pg.returncode, age_status, pg_error


def capture_one(config: JsonObject, cluster: str, cycle: Path) -> JsonObject:
    """Take, encrypt and flush one cluster's base backup, proving its identity did not change."""
    before = metadata(config, cluster)
    output = cycle / (cluster + ".tar.age")
    pg_status, age_status, pg_error = _stream_backup(config, cluster, output)
    if pg_status != 0:
        raise BackupFailureError(_basebackup_category(pg_error))
    require(age_status == 0, "basebackup-encryption-failed")
    sync_directory(cycle)
    after = metadata(config, cluster)
    require(before[:IDENTITY_PREFIX] == after[:IDENTITY_PREFIX], "identity-changed-during-backup")
    require(before[:LINEAGE_PREFIX] == after[:LINEAGE_PREFIX], "lineage-changed-during-backup")
    return {
        "before": before,
        "after": after,
        "ciphertextSha256": digest(output),
        "ciphertextBytes": output.stat().st_size,
    }


def _decrypt_command(config: JsonObject, ciphertext: Path) -> list[str]:
    return [
        tool("age", "v1.3.2"),
        "--decrypt",
        "--identity",
        str(private_path(config["ageIdentity"])),
        str(ciphertext),
    ]


def _manifest_member(archive: tarfile.TarFile, member: tarfile.TarInfo) -> bytes:
    require(member.isfile() and member.size <= MANIFEST_LIMIT, "backup-manifest-shape")
    stream = archive.extractfile(member)
    if stream is None:
        refuse("backup-manifest-shape")
    content = stream.read(member.size + 1)
    require(len(content) == member.size, "backup-manifest-short")
    return content


def read_pg_manifest(config: JsonObject, ciphertext: Path) -> JsonObject:
    """Decrypt a base backup far enough to read its PostgreSQL manifest, within size bounds."""
    require(ciphertext.stat().st_size <= config["maxBackupBytes"], "backup-size-policy")
    age = subprocess.Popen(
        _decrypt_command(config, ciphertext), stdout=subprocess.PIPE, stderr=subprocess.DEVNULL
    )
    manifest_bytes: bytes | None = None
    try:
        with tarfile.open(fileobj=age.stdout, mode="r|") as archive:
            expanded = 0
            for count, member in enumerate(archive, start=1):
                expanded += member.size
                require(
                    count <= config["maxTarEntries"] and expanded <= config["maxBackupBytes"],
                    "backup-expansion-limit",
                )
                if member.name.removeprefix("./") == "backup_manifest":
                    require(manifest_bytes is None, "backup-manifest-shape")
                    manifest_bytes = _manifest_member(archive, member)
        require(age.wait() == 0 and manifest_bytes is not None, "backup-manifest-missing")
    finally:
        if age.poll() is None:
            age.kill()
            age.wait()
    manifest: JsonObject = json.loads(manifest_bytes or b"")
    manifest["_sha256"] = hashlib.sha256(manifest_bytes or b"").hexdigest()
    return manifest


def postgres_control(config: JsonObject, cluster: str) -> tuple[str, int, int]:
    """Read the cluster's system identifier, timeline and WAL segment size."""
    result = subprocess.run(
        [
            tool("psql", "psql (PostgreSQL) 18.6"),
            "-X",
            "-w",
            "-q",
            "-A",
            "-t",
            "-F",
            "\t",
            "-v",
            "ON_ERROR_STOP=1",
            "-c",
            CONTROL_SQL,
        ],
        env=pg_env(config[cluster]["metadataService"]),
        capture_output=True,
        check=False,
    )
    require(result.returncode == 0, "postgres-system-read-failed")
    parts = result.stdout.decode("ascii", "strict").strip().split("\t")
    require(
        len(parts) == SYSTEM_COLUMNS and re.fullmatch(r"[0-9]{1,20}", parts[0]) is not None,
        "postgres-system-shape",
    )
    timeline = int(parts[1])
    segment_bytes = int(parts[2])
    require(
        timeline > 0
        and MIN_SEGMENT_BYTES <= segment_bytes <= MAX_SEGMENT_BYTES
        and segment_bytes & (segment_bytes - 1) == 0,
        "postgres-wal-control",
    )
    return parts[0], timeline, segment_bytes

"""Primitives shared by the managed backup commands: refusal, private paths, tools, signing."""

import hashlib
import json
import os
import re
import stat
import subprocess
import uuid
from collections.abc import Iterator
from datetime import datetime
from pathlib import Path
from typing import NoReturn

from backup_types import Json, JsonObject
from tool_versions import locate as locate_tool
from tool_versions import matches as matches_tool_version

ROOT = Path(__file__).resolve().parents[2]
CLUSTERS = ("primary", "witness")
SIGNATURE_BYTES = 64
HASH_BLOCK = 1024 * 1024
PG_CONNECT_TIMEOUT = "10"
PRIMARY_IDENTITY_FIELDS = 3
WITNESS_IDENTITY_FIELDS = 5
IDENTITY_SQL = {
    "primary": (
        "SELECT installation_id::text, lineage_id::text, witness_epoch::text "
        "FROM claimcore.installation_lineage WHERE singleton"
    ),
    "witness": (
        "SELECT installation_id::text, lineage_id::text, epoch::text, tip_sequence::text, "
        "encode(tip_hash, 'hex') FROM claimcore_witness.installation WHERE singleton"
    ),
}


class BackupFailureError(Exception):
    """A backup command refused with a safe category."""


def refuse(category: str) -> NoReturn:
    """Refuse with `category`; for branches that must also narrow a type."""
    raise BackupFailureError(category)


def require(condition: object, category: str) -> None:
    """Refuse with `category` unless `condition` holds."""
    if not condition:
        raise BackupFailureError(category)


def private_path(path: str | os.PathLike[str], *, directory: bool = False) -> Path:
    """Return an existing owner-private path outside the repository, refusing links."""
    unresolved = Path(os.path.normpath(Path.cwd() / path))
    for component in (unresolved, *unresolved.parents):
        require(not stat.S_ISLNK(os.lstat(component).st_mode), "linked-private-path-refused")
    resolved = unresolved.resolve(strict=True)
    require(not resolved.is_relative_to(ROOT), "repository-path-refused")
    mode = resolved.stat().st_mode & 0o777
    require(mode & 0o077 == 0, "private-permissions-required")
    require(resolved.is_dir() if directory else resolved.is_file(), "private-path-type")
    return resolved


def tool(name: str, version_prefix: str | None = None) -> str:
    """Locate a required executable, optionally proving its exact version."""
    found = locate_tool(name)
    if found is None:
        raise BackupFailureError("missing-tool-" + name)
    if version_prefix:
        result = subprocess.run([found, "--version"], capture_output=True, check=False)
        require(
            result.returncode == 0
            and matches_tool_version(
                name, version_prefix, result.stdout.decode("utf-8", "replace")
            ),
            "wrong-tool-version-" + name,
        )
    return found


def pg_env(service: str) -> dict[str, str]:
    """Return the only environment a PostgreSQL client may see: service file and name."""
    # Libpq gains environment options over time; only the admitted service file
    # and exact service name may control a capture connection.
    environment = {name: value for name, value in os.environ.items() if not name.startswith("PG")}
    environment["PGSERVICEFILE"] = os.environ["PGSERVICEFILE"]
    environment["PGSERVICE"] = service
    environment["PGCONNECT_TIMEOUT"] = PG_CONNECT_TIMEOUT
    return environment


def metadata(config: JsonObject, cluster: str) -> list[str]:
    """Read one cluster's installation identity (and, for the witness, its tip)."""
    psql = tool("psql", "psql (PostgreSQL) 18.6")
    result = subprocess.run(
        [
            psql,
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
            IDENTITY_SQL[cluster],
        ],
        env=pg_env(config[cluster]["metadataService"]),
        capture_output=True,
        check=False,
    )
    require(result.returncode == 0, "metadata-read-failed")
    lines = result.stdout.decode("ascii", "strict").splitlines()
    expected = PRIMARY_IDENTITY_FIELDS if cluster == "primary" else WITNESS_IDENTITY_FIELDS
    require(len(lines) == 1 and len(lines[0].split("\t")) == expected, "metadata-shape")
    fields = lines[0].split("\t")
    for value in fields[:2]:
        uuid.UUID(value)
    require(int(fields[2]) > 0, "metadata-epoch")
    if cluster == "witness":
        require(
            int(fields[3]) >= 0 and re.fullmatch(r"[0-9a-f]{64}", fields[4]) is not None,
            "metadata-tip",
        )
    return fields


def digest(path: str | os.PathLike[str]) -> str:
    """Return the SHA-256 of a file."""
    sha = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(HASH_BLOCK), b""):
            sha.update(block)
    return sha.hexdigest()


def sync_file(path: str | os.PathLike[str]) -> None:
    """Flush a file's contents to stable storage."""
    with Path(path).open("rb") as stream:
        os.fsync(stream.fileno())


def sync_directory(path: str | os.PathLike[str]) -> None:
    """Flush a directory's entries to stable storage."""
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def decrypt_blocks(
    config: JsonObject, ciphertext: Path, *, limit: str, failure: str
) -> Iterator[bytes]:
    """Yield the plaintext of an age file in blocks, refusing to exceed the configured size."""
    age = subprocess.Popen(
        [
            tool("age", "v1.3.2"),
            "--decrypt",
            "--identity",
            str(private_path(config["ageIdentity"])),
            str(ciphertext),
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
    )
    stream = age.stdout
    if stream is None:
        refuse(failure)
    total = 0
    try:
        for block in iter(lambda: stream.read(HASH_BLOCK), b""):
            total += len(block)
            require(total <= config["maxBackupBytes"], limit)
            yield block
        require(age.wait() == 0, failure)
    finally:
        if age.poll() is None:
            age.kill()
            age.wait()


def json_bytes(value: Json) -> bytes:
    """Return the canonical newline-terminated JSON encoding."""
    return (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n"
    ).encode("ascii")


def utc_timestamp(value: Json) -> datetime:
    """Parse a second-precision UTC timestamp, refusing any other form."""
    require(
        isinstance(value, str)
        and re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", value)
        is not None,
        "timestamp-format",
    )
    return datetime.fromisoformat(value)


def write_new(path: str | os.PathLike[str], payload: bytes) -> None:
    """Create a new owner-only file with `payload`, flushed to stable storage."""
    fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())


def sign_with_key(key: Path, source: Path, signature: Path) -> None:
    """Sign `source` with `key`, publishing the signature atomically without replacing a file."""
    temporary = signature.with_name("." + signature.name + "." + str(uuid.uuid4()))
    try:
        result = subprocess.run(
            [
                tool("openssl"),
                "pkeyutl",
                "-sign",
                "-rawin",
                "-inkey",
                str(key),
                "-in",
                str(source),
                "-out",
                str(temporary),
            ],
            capture_output=True,
            check=False,
        )
        require(result.returncode == 0, "signing-failed")
        require(temporary.stat().st_size == SIGNATURE_BYTES, "signature-length")
        os.link(temporary, signature, follow_symlinks=False)
    finally:
        temporary.unlink(missing_ok=True)
    sync_file(signature)
    sync_directory(signature.parent)


def sign(config: JsonObject, source: Path, signature: Path) -> None:
    """Sign `source` with the configured copy-signing key."""
    sign_with_key(config["signingKey"], source, signature)


def verify_with_key(key: Path, source: Path, signature: Path) -> None:
    """Verify a detached signature over `source`, refusing anything else."""
    checked_source = private_path(source)
    checked_signature = private_path(signature)
    require(checked_signature.stat().st_size == SIGNATURE_BYTES, "signature-length")
    result = subprocess.run(
        [
            tool("openssl"),
            "pkeyutl",
            "-verify",
            "-rawin",
            "-pubin",
            "-inkey",
            str(key),
            "-in",
            str(checked_source),
            "-sigfile",
            str(checked_signature),
        ],
        capture_output=True,
        check=False,
    )
    require(result.returncode == 0, "signature-invalid")


def verify_signature(config: JsonObject, source: Path, signature: Path) -> None:
    """Verify a copy signature with the configured verification key."""
    verify_with_key(config["verificationKey"], source, signature)


def verify_checkpoint_signature(config: JsonObject, source: Path, signature: Path) -> None:
    """Verify a checkpoint signature with the separate checkpoint verification key."""
    verify_with_key(config["checkpointVerificationKey"], source, signature)

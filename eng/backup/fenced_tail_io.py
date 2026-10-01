"""Private, bounded I/O for synthetic fenced-tail capture."""

import hashlib
import json
import os
import re
import stat
import subprocess
from collections.abc import Sequence
from pathlib import Path

from backup_types import Json


class CaptureRefusalError(Exception):
    """Safe refusal category, never a subprocess or private-file payload."""


LSN = re.compile(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}\Z")
SEGMENT = re.compile(r"[0-9A-F]{24}\Z")
CONTAINER = re.compile(r"[0-9a-f]{64}\Z")
SYSTEM_ID = re.compile(r"[1-9][0-9]{0,19}\Z")
HASH_BLOCK = 1024 * 1024
METADATA_LIMIT = 1024 * 1024
DEFAULT_OUTPUT_LIMIT = 256
DEFAULT_TIMEOUT_SECONDS = 30
SEGMENT_CHECK_TIMEOUT_SECONDS = 10
NOT_FOUND = 1
STAGES = frozenset(
    {
        "cluster-identity",
        "wal-horizon-query",
        "wal-switch-query",
        "container-inspect",
        "wal-segment-copy",
        "wal-inspection",
        "wal-inspection-tool",
        "wal-encryption",
        "recipient-derivation",
    }
)


def require(condition: object, reason: str) -> None:
    """Refuse with `reason` unless `condition` holds."""
    if not condition:
        raise CaptureRefusalError(reason)


def command(
    args: Sequence[str],
    maximum: int = DEFAULT_OUTPUT_LIMIT,
    timeout: int = DEFAULT_TIMEOUT_SECONDS,
    *,
    stage: str,
) -> str:
    """Run a fixed argv within bounds, returning its trimmed stdout or a stage-coded refusal."""
    require(stage in STAGES, "capture-stage-invalid")
    try:
        result = subprocess.run(
            args,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        raise CaptureRefusalError(stage + "-unavailable") from None
    require(result.returncode == 0, stage + "-exit")
    require(len(result.stdout) <= maximum, stage + "-stdout-bound")
    return result.stdout.decode("ascii", errors="strict").strip()


def require_container_segment(container: str, source: str) -> None:
    """Require that a WAL segment file exists inside the container."""
    try:
        result = subprocess.run(
            ["docker", "exec", "-u", "postgres", container, "test", "-f", source],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=SEGMENT_CHECK_TIMEOUT_SECONDS,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        msg = "wal-segment-check-unavailable"
        raise CaptureRefusalError(msg) from None
    if result.returncode == NOT_FOUND:
        msg = "wal-segment-missing"
        raise CaptureRefusalError(msg)
    require(result.returncode == 0 and result.stdout == b"", "wal-segment-check-refused")


def _private_entry(path: str | os.PathLike[str], kind: str) -> tuple[Path, os.stat_result]:
    value = Path(path)
    require(value.is_absolute(), kind)
    for ancestor in (value, *value.parents):
        require(not ancestor.is_symlink(), kind + "-link")
    try:
        info = value.stat()
    except OSError:
        raise CaptureRefusalError(kind) from None
    return value, info


def private_directory(path: str | os.PathLike[str]) -> Path:
    """Return an absolute, unlinked directory owned by this user and closed to others."""
    value, info = _private_entry(path, "private-directory")
    require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid(), "private-directory")
    require(info.st_mode & 0o077 == 0, "private-directory-permissions")
    return value


def private_file(path: str | os.PathLike[str]) -> Path:
    """Return an absolute, unlinked, single-link regular file private to this user."""
    value, info = _private_entry(path, "private-file")
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, "private-file")
    require(info.st_uid == os.getuid() and info.st_mode & 0o077 == 0, "private-file-permissions")
    return value


def regular_segment(path: Path, expected_bytes: int) -> None:
    """Require a single-link regular file of exactly `expected_bytes`."""
    try:
        info = path.lstat()
    except OSError:
        msg = "wal-segment-missing"
        raise CaptureRefusalError(msg) from None
    require(
        stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_size == expected_bytes,
        "wal-segment-invalid",
    )


def hash_file(path: Path) -> tuple[str, int]:
    """Return the SHA-256 and length of a file."""
    digest = hashlib.sha256()
    length = 0
    with path.open("rb") as source:
        for block in iter(lambda: source.read(HASH_BLOCK), b""):
            length += len(block)
            digest.update(block)
    return digest.hexdigest(), length


def create_private_json(path: str | os.PathLike[str], value: Json) -> None:
    """Create a new owner-only canonical JSON file, flushed to stable storage."""
    payload = (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n"
    ).encode("ascii")
    require(len(payload) <= METADATA_LIMIT, "tail-metadata-limit")
    try:
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    except OSError:
        msg = "tail-output-exists"
        raise CaptureRefusalError(msg) from None
    with os.fdopen(descriptor, "wb") as output:
        output.write(payload)
        output.flush()
        os.fsync(output.fileno())

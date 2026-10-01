"""Private, bounded byte and signature operations for one managed-copy proof."""

import hashlib
import json
import os
import re
import stat
import subprocess
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import Json, JsonObject
from tool_versions import locate as locate_tool
from tool_versions import matches as matches_tool_version

READ_BLOCK = 65536
SIGNATURE_BYTES = 64
PROOF_LIMIT = 32768
KEY_LIMIT = 16384
PROOF_MINUTES = 5
TOOL_TIMEOUT_SECONDS = 10
SIGN_TIMEOUT_SECONDS = 30
PRIVATE_DIRECTORY_MODE = 0o700
NO_FOLLOW = getattr(os, "O_NOFOLLOW", 0)


class VerificationFailureError(Exception):
    """A managed-copy verification refusal carrying a fixed uppercase code."""

    def __init__(self, code: str) -> None:
        """Keep the fixed refusal code."""
        self.code = code
        super().__init__(code)


def require(condition: object, code: str) -> None:
    """Refuse with `code` unless `condition` holds."""
    if not condition:
        raise VerificationFailureError(code)


def _canonical_path(raw: Json) -> Path:
    require(
        isinstance(raw, str) and raw.startswith("/") and "\x00" not in raw,
        "PRIVATE_PATH_REFUSED",
    )
    path = Path(raw)
    require(
        str(path) == raw and all(part not in (".", "..") for part in path.parts),
        "PRIVATE_PATH_REFUSED",
    )
    for component in (path, *path.parents):
        try:
            require(not stat.S_ISLNK(os.lstat(component).st_mode), "LINKED_PATH_REFUSED")
        except FileNotFoundError:
            msg = "PRIVATE_PATH_REFUSED"
            raise VerificationFailureError(msg) from None
    return path


def private_file(raw: Json, maximum: int) -> Path:
    """Return an owner-private single-link regular file within `maximum` bytes."""
    path = _canonical_path(raw)
    info = os.lstat(path)
    require(
        stat.S_ISREG(info.st_mode)
        and info.st_uid == os.geteuid()
        and info.st_nlink == 1
        and info.st_mode & 0o077 == 0
        and 0 < info.st_size <= maximum,
        "PRIVATE_FILE_REFUSED",
    )
    return path


def private_directory(raw: str) -> Path:
    """Return an owner-only (0700) directory."""
    path = _canonical_path(raw)
    info = os.lstat(path)
    require(
        stat.S_ISDIR(info.st_mode)
        and info.st_uid == os.geteuid()
        and info.st_mode & 0o777 == PRIVATE_DIRECTORY_MODE,
        "PRIVATE_DIRECTORY_REFUSED",
    )
    return path


def _identity(info: os.stat_result) -> tuple[int, int, int, int, int]:
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def read_private(raw: Json, maximum: int) -> bytes:
    """Read a private file in full, refusing if it changes while being read."""
    path = private_file(raw, maximum)
    descriptor = os.open(path, os.O_RDONLY | NO_FOLLOW)
    try:
        before = os.fstat(descriptor)
        require(before.st_nlink == 1 and before.st_size <= maximum, "PRIVATE_FILE_REFUSED")
        chunks = []
        total = 0
        while True:
            block = os.read(descriptor, min(READ_BLOCK, maximum + 1 - total))
            if not block:
                break
            total += len(block)
            require(total <= maximum, "PRIVATE_FILE_TOO_LARGE")
            chunks.append(block)
        after = os.fstat(descriptor)
        current = os.lstat(path)
        require(
            _identity(before) == _identity(after)
            and total == after.st_size
            and (current.st_dev, current.st_ino) == (after.st_dev, after.st_ino),
            "PRIVATE_FILE_CHANGED",
        )
        return b"".join(chunks)
    finally:
        os.close(descriptor)


def hash_private(raw: Json, expected_bytes: int, maximum: int) -> str:
    """Hash a private file of exactly `expected_bytes`, refusing if it changes."""
    path = private_file(raw, maximum)
    require(expected_bytes <= maximum, "COPY_SIZE_REFUSED")
    descriptor = os.open(path, os.O_RDONLY | NO_FOLLOW)
    try:
        before = os.fstat(descriptor)
        digest = hashlib.sha256()
        total = 0
        while True:
            block = os.read(descriptor, READ_BLOCK)
            if not block:
                break
            total += len(block)
            require(total <= expected_bytes, "COPY_SIZE_REFUSED")
            digest.update(block)
        after = os.fstat(descriptor)
        current = os.lstat(path)
        require(
            total == expected_bytes
            and total == after.st_size
            and _identity(before) == _identity(after)
            and (current.st_dev, current.st_ino) == (after.st_dev, after.st_ino),
            "COPY_CHANGED",
        )
        return digest.hexdigest()
    finally:
        os.close(descriptor)


def write_new(raw: str, value: bytes) -> None:
    """Create a new owner-only file with `value`, flushing file and directory."""
    path = Path(raw)
    private_directory(str(path.parent))
    require(not path.exists() and not path.is_symlink(), "OUTPUT_ALREADY_EXISTS")
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | NO_FOLLOW, 0o600)
    try:
        with os.fdopen(descriptor, "wb", closefd=False) as stream:
            stream.write(value)
            stream.flush()
            os.fsync(stream.fileno())
    finally:
        os.close(descriptor)
    directory = os.open(path.parent, os.O_RDONLY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def canonical(value: Json) -> bytes:
    """Return the canonical newline-terminated JSON of a proof, bounded in size."""
    encoded = (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n"
    ).encode("ascii")
    require(len(encoded) <= PROOF_LIMIT, "PROOF_TOO_LARGE")
    return encoded


def exact_json(raw: bytes) -> JsonObject:
    """Parse JSON, refusing duplicate fields and invalid text."""

    def pairs(items: list[tuple[str, Json]]) -> JsonObject:
        result: JsonObject = {}
        for name, value in items:
            require(name not in result, "DUPLICATE_FIELD")
            result[name] = value
        return result

    try:
        parsed: JsonObject = json.loads(raw, object_pairs_hook=pairs)
    except (ValueError, UnicodeError):
        msg = "INVALID_JSON"
        raise VerificationFailureError(msg) from None
    return parsed


def tool(name: str, version: str | None) -> str:
    """Locate a required executable, optionally proving its exact version."""
    found = locate_tool(name)
    if found is None:
        msg = "TOOL_UNAVAILABLE"
        raise VerificationFailureError(msg)
    if version:
        result = subprocess.run(
            [found, "--version"], capture_output=True, timeout=TOOL_TIMEOUT_SECONDS, check=False
        )
        require(
            result.returncode == 0
            and matches_tool_version(name, version, result.stdout.decode("utf-8", "replace")),
            "TOOL_VERSION_REFUSED",
        )
    return found


def sign(private_key: Json, proof: Path) -> bytes:
    """Sign the proof file with an owner-private Ed25519 key."""
    key = private_file(private_key, KEY_LIMIT)
    result = subprocess.run(
        [
            tool("openssl", None),
            "pkeyutl",
            "-sign",
            "-rawin",
            "-inkey",
            str(key),
            "-in",
            str(proof),
        ],
        capture_output=True,
        timeout=SIGN_TIMEOUT_SECONDS,
        check=False,
    )
    require(
        result.returncode == 0 and len(result.stdout) == SIGNATURE_BYTES, "SIGNATURE_UNAVAILABLE"
    )
    return result.stdout


def now_and_expiry() -> tuple[str, str]:
    """Return the current time and a five-minute expiry as second-precision UTC text."""
    now = datetime.now(UTC).replace(microsecond=0)
    return (
        now.isoformat().replace("+00:00", "Z"),
        (now + timedelta(minutes=PROOF_MINUTES)).isoformat().replace("+00:00", "Z"),
    )


def uuid_text(value: Json) -> str:
    """Return `value` when it is the canonical text of a non-nil UUID."""
    try:
        parsed = uuid.UUID(value)
    except (TypeError, ValueError):
        msg = "IDENTITY_REFUSED"
        raise VerificationFailureError(msg) from None
    require(parsed.int != 0 and str(parsed) == value, "IDENTITY_REFUSED")
    return str(value)


def sha_text(value: Json) -> str:
    """Return `value` when it is a lowercase SHA-256 digest."""
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value), "DIGEST_REFUSED")
    return str(value)

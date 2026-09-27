"""Private, bounded byte and signature operations for one managed-copy proof."""

import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
from datetime import datetime, timedelta, timezone
from pathlib import Path


class VerificationFailure(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def require(condition, code):
    if not condition:
        raise VerificationFailure(code)


def _canonical_path(raw):
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
            require(
                not stat.S_ISLNK(os.lstat(component).st_mode), "LINKED_PATH_REFUSED"
            )
        except FileNotFoundError:
            raise VerificationFailure("PRIVATE_PATH_REFUSED") from None
    return path


def private_file(raw, maximum):
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


def private_directory(raw):
    path = _canonical_path(raw)
    info = os.lstat(path)
    require(
        stat.S_ISDIR(info.st_mode)
        and info.st_uid == os.geteuid()
        and info.st_mode & 0o777 == 0o700,
        "PRIVATE_DIRECTORY_REFUSED",
    )
    return path


def read_private(raw, maximum):
    path = private_file(raw, maximum)
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    try:
        before = os.fstat(descriptor)
        require(
            before.st_nlink == 1 and before.st_size <= maximum, "PRIVATE_FILE_REFUSED"
        )
        chunks = []
        total = 0
        while True:
            block = os.read(descriptor, min(65536, maximum + 1 - total))
            if not block:
                break
            total += len(block)
            require(total <= maximum, "PRIVATE_FILE_TOO_LARGE")
            chunks.append(block)
        after = os.fstat(descriptor)
        current = os.lstat(path)
        require(
            (
                before.st_dev,
                before.st_ino,
                before.st_size,
                before.st_mtime_ns,
                before.st_ctime_ns,
            )
            == (
                after.st_dev,
                after.st_ino,
                after.st_size,
                after.st_mtime_ns,
                after.st_ctime_ns,
            )
            and total == after.st_size
            and (current.st_dev, current.st_ino) == (after.st_dev, after.st_ino),
            "PRIVATE_FILE_CHANGED",
        )
        return b"".join(chunks)
    finally:
        os.close(descriptor)


def hash_private(raw, expected_bytes, maximum):
    path = private_file(raw, maximum)
    require(expected_bytes <= maximum, "COPY_SIZE_REFUSED")
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
    try:
        before = os.fstat(descriptor)
        digest = hashlib.sha256()
        total = 0
        while True:
            block = os.read(descriptor, 65536)
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
            and (
                before.st_dev,
                before.st_ino,
                before.st_size,
                before.st_mtime_ns,
                before.st_ctime_ns,
            )
            == (
                after.st_dev,
                after.st_ino,
                after.st_size,
                after.st_mtime_ns,
                after.st_ctime_ns,
            )
            and (current.st_dev, current.st_ino) == (after.st_dev, after.st_ino),
            "COPY_CHANGED",
        )
        return digest.hexdigest()
    finally:
        os.close(descriptor)


def write_new(raw, value):
    path = Path(raw)
    private_directory(str(path.parent))
    require(not path.exists() and not path.is_symlink(), "OUTPUT_ALREADY_EXISTS")
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags, 0o600)
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


def canonical(value):
    encoded = (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
        + "\n"
    ).encode("ascii")
    require(len(encoded) <= 32768, "PROOF_TOO_LARGE")
    return encoded


def exact_json(raw):
    def pairs(items):
        result = {}
        for name, value in items:
            require(name not in result, "DUPLICATE_FIELD")
            result[name] = value
        return result

    try:
        return json.loads(raw, object_pairs_hook=pairs)
    except (ValueError, UnicodeError):
        raise VerificationFailure("INVALID_JSON") from None


def tool(name, version):
    found = shutil.which(name)
    require(found is not None, "TOOL_UNAVAILABLE")
    if version:
        result = subprocess.run(
            [found, "--version"], capture_output=True, timeout=10, check=False
        )
        require(
            result.returncode == 0
            and result.stdout.decode("utf-8", "replace").startswith(version),
            "TOOL_VERSION_REFUSED",
        )
    return found


def sign(private_key, proof):
    key = private_file(private_key, 16384)
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
        timeout=30,
        check=False,
    )
    require(
        result.returncode == 0 and len(result.stdout) == 64, "SIGNATURE_UNAVAILABLE"
    )
    return result.stdout


def now_and_expiry():
    now = datetime.now(timezone.utc).replace(microsecond=0)
    return (
        now.isoformat().replace("+00:00", "Z"),
        (now + timedelta(minutes=5)).isoformat().replace("+00:00", "Z"),
    )


def uuid_text(value):
    import uuid

    try:
        parsed = uuid.UUID(value)
        require(parsed.int != 0 and str(parsed) == value, "IDENTITY_REFUSED")
        return value
    except (TypeError, ValueError):
        raise VerificationFailure("IDENTITY_REFUSED") from None


def sha_text(value):
    require(
        isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value),
        "DIGEST_REFUSED",
    )
    return value

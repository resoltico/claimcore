"""Private, bounded I/O for synthetic fenced-tail capture."""

import hashlib
import json
import os
import re
import stat
import subprocess
from pathlib import Path


class CaptureRefusal(Exception):
    """Safe refusal category, never a subprocess or private-file payload."""


LSN = re.compile(r"[0-9A-F]{1,8}/[0-9A-F]{1,8}\Z")
SEGMENT = re.compile(r"[0-9A-F]{24}\Z")
CONTAINER = re.compile(r"[0-9a-f]{64}\Z")
SYSTEM_ID = re.compile(r"[1-9][0-9]{0,19}\Z")


def require(condition, reason):
    if not condition:
        raise CaptureRefusal(reason)


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


def command(args, maximum=256, timeout=30, *, stage):
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
        raise CaptureRefusal(stage + "-unavailable") from None
    require(result.returncode == 0, stage + "-exit")
    require(len(result.stdout) <= maximum, stage + "-stdout-bound")
    return result.stdout.decode("ascii", errors="strict").strip()


def require_container_segment(container, source):
    try:
        result = subprocess.run(
            ["docker", "exec", "-u", "postgres", container, "test", "-f", source],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=10,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        raise CaptureRefusal("wal-segment-check-unavailable") from None
    if result.returncode == 1:
        raise CaptureRefusal("wal-segment-missing")
    require(
        result.returncode == 0 and result.stdout == b"",
        "wal-segment-check-refused",
    )


def private_directory(path):
    value = Path(path)
    require(value.is_absolute(), "private-directory")
    for ancestor in (value, *value.parents):
        require(not ancestor.is_symlink(), "private-directory-link")
    try:
        info = value.stat()
    except OSError:
        raise CaptureRefusal("private-directory") from None
    require(
        stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid(), "private-directory"
    )
    require(info.st_mode & 0o077 == 0, "private-directory-permissions")
    return value


def private_file(path):
    value = Path(path)
    require(value.is_absolute(), "private-file")
    for ancestor in (value, *value.parents):
        require(not ancestor.is_symlink(), "private-file-link")
    try:
        info = value.stat()
    except OSError:
        raise CaptureRefusal("private-file") from None
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, "private-file")
    require(
        info.st_uid == os.getuid() and info.st_mode & 0o077 == 0,
        "private-file-permissions",
    )
    return value


def regular_segment(path, expected_bytes):
    try:
        info = path.lstat()
    except OSError:
        raise CaptureRefusal("wal-segment-missing") from None
    require(
        stat.S_ISREG(info.st_mode)
        and info.st_nlink == 1
        and info.st_size == expected_bytes,
        "wal-segment-invalid",
    )


def hash_file(path):
    digest = hashlib.sha256()
    length = 0
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            length += len(block)
            digest.update(block)
    return digest.hexdigest(), length


def create_private_json(path, value):
    payload = (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
        + "\n"
    ).encode("ascii")
    require(len(payload) <= 1024 * 1024, "tail-metadata-limit")
    try:
        descriptor = os.open(
            path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600
        )
    except OSError:
        raise CaptureRefusal("tail-output-exists") from None
    with os.fdopen(descriptor, "wb") as output:
        output.write(payload)
        output.flush()
        os.fsync(output.fileno())

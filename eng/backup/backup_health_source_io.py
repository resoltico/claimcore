"""Handle-first owner-private health source I/O and individual signer key admission."""

import hashlib
import os
import stat
import subprocess
from pathlib import Path

from deployment_common import DeploymentRefusal, private_path, require


def _open_exact(path, maximum):
    target = Path(os.path.abspath(path))
    parts = target.parts[1:]
    require(parts, "health-source-path")
    descriptor = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        for component in parts[:-1]:
            child = os.open(
                component,
                os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                dir_fd=descriptor,
            )
            os.close(descriptor)
            descriptor = child
        parent = os.fstat(descriptor)
        require(
            parent.st_uid == os.geteuid() and parent.st_mode & 0o077 == 0,
            "health-source-private-parent",
        )
        opened = os.open(
            parts[-1],
            os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK,
            dir_fd=descriptor,
        )
        try:
            details = os.fstat(opened)
            require(
                stat.S_ISREG(details.st_mode)
                and details.st_uid == os.geteuid()
                and details.st_mode & 0o077 == 0
                and 0 < details.st_size <= maximum,
                "health-source-private-file",
            )
            return opened, details.st_size
        except (DeploymentRefusal, OSError):
            os.close(opened)
            raise
    except OSError:
        raise DeploymentRefusal("health-source-private-file") from None
    finally:
        os.close(descriptor)


def _read(path, maximum):
    descriptor, size = _open_exact(path, maximum)
    try:
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            content = stream.read(maximum + 1)
        require(len(content) == size, "health-source-changed-file")
        return content
    finally:
        os.close(descriptor)


def _hash(path, maximum):
    digest, count = hashlib.sha256(), 0
    descriptor, size = _open_exact(path, maximum)
    try:
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            while chunk := stream.read(1024 * 1024):
                count += len(chunk)
                require(count <= maximum, "health-source-object-size")
                digest.update(chunk)
        require(count == size, "health-source-changed-file")
    finally:
        os.close(descriptor)
    return digest.hexdigest(), count


def role_public_key(private_key):
    key = private_path(private_key)
    result = subprocess.run(
        ["openssl", "pkey", "-in", str(key), "-pubout", "-outform", "DER"],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    prefix = bytes.fromhex("302a300506032b6570032100")
    require(
        result.returncode == 0
        and result.stdout.startswith(prefix)
        and len(result.stdout) == 44,
        "health-role-key",
    )
    return result.stdout[len(prefix) :]


def create_private(path, content, maximum):
    require(
        isinstance(content, bytes) and 0 < len(content) <= maximum,
        "health-source-output-size",
    )
    target = Path(os.path.abspath(path))
    parent = private_path(target.parent, directory=True)
    try:
        directory = os.open(
            parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
        )
        try:
            descriptor = os.open(
                target.name,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                0o600,
                dir_fd=directory,
            )
            try:
                with os.fdopen(descriptor, "wb", closefd=False) as stream:
                    stream.write(content)
                    stream.flush()
                    os.fsync(descriptor)
            finally:
                os.close(descriptor)
        finally:
            os.close(directory)
    except OSError:
        raise DeploymentRefusal("health-source-output-refused") from None

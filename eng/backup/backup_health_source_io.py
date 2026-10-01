"""Handle-first owner-private health source I/O and individual signer key admission."""

import hashlib
import os
import stat
import subprocess
from pathlib import Path

from deployment_common import DeploymentRefusalError, private_path, require

HASH_BLOCK = 1024 * 1024
ED25519_PREFIX = bytes.fromhex("302a300506032b6570032100")
ED25519_DER_LENGTH = 44


def _descend(parts: tuple[str, ...]) -> int:
    """Open the parent directory of the final component without following any link."""
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
    except OSError:
        os.close(descriptor)
        raise
    return descriptor


def _admit_file(opened: int, maximum: int) -> int:
    details = os.fstat(opened)
    require(
        stat.S_ISREG(details.st_mode)
        and details.st_uid == os.geteuid()
        and details.st_mode & 0o077 == 0
        and 0 < details.st_size <= maximum,
        "health-source-private-file",
    )
    return details.st_size


def _open_exact(path: str | os.PathLike[str], maximum: int) -> tuple[int, int]:
    target = Path(os.path.normpath(Path.cwd() / path))
    parts = target.parts[1:]
    require(parts, "health-source-path")
    try:
        descriptor = _descend(parts)
    except OSError:
        msg = "health-source-private-file"
        raise DeploymentRefusalError(msg) from None
    try:
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
            size = _admit_file(opened, maximum)
        except (DeploymentRefusalError, OSError):
            os.close(opened)
            raise
    except OSError:
        msg = "health-source-private-file"
        raise DeploymentRefusalError(msg) from None
    else:
        return opened, size
    finally:
        os.close(descriptor)


def read_private(path: str | os.PathLike[str], maximum: int) -> bytes:
    """Read an owner-only regular file of at most `maximum` bytes."""
    descriptor, size = _open_exact(path, maximum)
    try:
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            content = stream.read(maximum + 1)
        require(len(content) == size, "health-source-changed-file")
        return content
    finally:
        os.close(descriptor)


def hash_private(path: str | os.PathLike[str], maximum: int) -> tuple[str, int]:
    """Hash an owner-only regular file of at most `maximum` bytes."""
    digest, count = hashlib.sha256(), 0
    descriptor, size = _open_exact(path, maximum)
    try:
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            while chunk := stream.read(HASH_BLOCK):
                count += len(chunk)
                require(count <= maximum, "health-source-object-size")
                digest.update(chunk)
        require(count == size, "health-source-changed-file")
    finally:
        os.close(descriptor)
    return digest.hexdigest(), count


def role_public_key(private_key: str | os.PathLike[str]) -> bytes:
    """Return the raw Ed25519 public key of an owner-private key file."""
    key = private_path(private_key)
    result = subprocess.run(
        ["openssl", "pkey", "-in", str(key), "-pubout", "-outform", "DER"],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    require(
        result.returncode == 0
        and result.stdout.startswith(ED25519_PREFIX)
        and len(result.stdout) == ED25519_DER_LENGTH,
        "health-role-key",
    )
    return result.stdout[len(ED25519_PREFIX) :]


def create_private(path: str | os.PathLike[str], content: bytes, maximum: int) -> None:
    """Create a new owner-only file with `content`, never replacing or following links."""
    require(
        isinstance(content, bytes) and 0 < len(content) <= maximum,
        "health-source-output-size",
    )
    target = Path(os.path.normpath(Path.cwd() / path))
    parent = private_path(target.parent, directory=True)
    try:
        directory = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
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
        msg = "health-source-output-refused"
        raise DeploymentRefusalError(msg) from None

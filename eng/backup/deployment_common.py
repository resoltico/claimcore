"""Private, canonical signed deployment-probe documents."""

import base64
import hashlib
import json
import os
import re
import stat
import subprocess
import tempfile
import uuid
from datetime import UTC, datetime
from pathlib import Path
from typing import NoReturn

from backup_types import Json, JsonObject

SIGNATURE_BYTES = 64
MAX_SIGNATURE_TEXT = 128
ED25519_PREFIX = bytes.fromhex("302a300506032b6570032100")
PUBLIC_KEY_LIMIT = 512
KEY_PARSE_TIMEOUT_SECONDS = 10


class DeploymentRefusalError(Exception):
    """A deployment probe refusal carrying a safe category."""


def refuse(category: str) -> NoReturn:
    """Refuse with a safe category; for checks that must also narrow a type."""
    raise DeploymentRefusalError(category)


def require(value: object, category: str) -> None:
    """Refuse with the safe `category` unless `value` is truthy."""
    if not value:
        raise DeploymentRefusalError(category)


def private_path(
    raw: str | os.PathLike[str], *, directory: bool = False, owner: bool = True
) -> Path:
    """Resolve an owner-only path, refusing links and permissive ancestors."""
    target = Path(os.path.normpath(Path.cwd() / raw))
    for component in (target, *target.parents):
        require(not stat.S_ISLNK(os.lstat(component).st_mode), "linked-private-path")
    for private in (target.parent, target):
        flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC
        if private == target.parent or directory:
            flags |= os.O_DIRECTORY
        try:
            descriptor = os.open(private, flags)
        except OSError:
            msg = "private-path-inaccessible"
            raise DeploymentRefusalError(msg) from None
        try:
            status = os.fstat(descriptor)
            expected_kind = stat.S_ISDIR if private == target.parent or directory else stat.S_ISREG
            require(expected_kind(status.st_mode), "private-path-kind")
            require(status.st_mode & 0o077 == 0, "private-path-permissions")
            if owner or private == target.parent:
                require(status.st_uid == os.geteuid(), "private-path-owner")
            try:
                acl = os.getxattr(descriptor, "system.posix_acl_access")
            except (AttributeError, OSError):
                acl = b""
            require(not acl, "private-path-acl")
            if sys_platform_darwin():
                result = subprocess.run(
                    ["ls", "-lde", str(private)], capture_output=True, check=False
                )
                require(result.returncode == 0, "private-path-acl")
                mode = result.stdout.split(maxsplit=1)[0]
                require(not mode.endswith(b"+"), "private-path-acl")
        finally:
            os.close(descriptor)
    return target


def sys_platform_darwin() -> bool:
    """Return whether the host operating system is macOS."""
    return os.uname().sysname == "Darwin"


def is_uuid(value: Json) -> bool:
    """Whether `value` is the canonical text of a non-nil UUID."""
    try:
        parsed = uuid.UUID(value)
    except (TypeError, ValueError, AttributeError):
        return False
    return parsed.int != 0 and str(parsed) == value


def is_sha256(value: Json) -> bool:
    """Whether `value` is a lowercase hexadecimal SHA-256 digest."""
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def canonical(value: Json) -> bytes:
    """Serialize `value` as canonical ASCII JSON with one trailing newline."""
    return (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n"
    ).encode("ascii")


def timestamp(value: Json) -> datetime:
    """Parse a UTC ISO-8601 timestamp string."""
    require(isinstance(value, str), "timestamp-type")
    require(
        re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", value) is not None,
        "timestamp-format",
    )
    return datetime.fromisoformat(value)


def utc(value: datetime) -> str:
    """Format `value` as a UTC ISO-8601 timestamp with second precision."""
    return value.astimezone(UTC).isoformat(timespec="seconds").replace("+00:00", "Z")


def public_key_identity(path: str | os.PathLike[str]) -> bytes:
    """Parse the raw Ed25519 public key; PEM wrapping is not signer identity."""
    key = private_path(path)
    require(key.stat().st_size <= PUBLIC_KEY_LIMIT, "signer-public-key-size")
    result = subprocess.run(
        ["openssl", "pkey", "-pubin", "-in", str(key), "-outform", "DER"],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=KEY_PARSE_TIMEOUT_SECONDS,
        check=False,
    )
    require(
        result.returncode == 0
        and len(result.stdout) == len(ED25519_PREFIX) + 32
        and result.stdout.startswith(ED25519_PREFIX),
        "signer-public-key-invalid",
    )
    return result.stdout[len(ED25519_PREFIX) :]


def _run_signature(
    args: list[str], input_bytes: bytes, signature_bytes: bytes | None = None
) -> bytes | None:
    with tempfile.TemporaryDirectory(prefix="claimcore-deploy-sign-") as temporary:
        root = Path(temporary)
        root.chmod(0o700)
        source = root / "statement.json"
        source.write_bytes(input_bytes)
        source.chmod(0o600)
        signature = root / "statement.sig"
        if signature_bytes is not None:
            signature.write_bytes(signature_bytes)
            signature.chmod(0o600)
        command = ["openssl", "pkeyutl", *args, "-rawin", "-in", str(source)]
        command.extend(
            ["-sigfile", str(signature)]
            if signature_bytes is not None
            else ["-out", str(signature)]
        )
        result = subprocess.run(
            command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False
        )
        require(result.returncode == 0, "deployment-signature-invalid")
        if signature_bytes is None:
            result_bytes = signature.read_bytes()
            require(len(result_bytes) == SIGNATURE_BYTES, "deployment-signature-length")
            return result_bytes
        return None


def sign(report: JsonObject, private_key: str | os.PathLike[str]) -> JsonObject:
    """Sign the canonical bytes of `report` with an owner-only Ed25519 key."""
    key = private_path(private_key)
    body = canonical(report)
    signature = _run_signature(["-sign", "-inkey", str(key)], body)
    if signature is None:
        msg = "deployment-signature-invalid"
        raise DeploymentRefusalError(msg)
    return {
        "report": report,
        "signatureBase64": base64.b64encode(signature).decode("ascii"),
    }


def verify(envelope: Json, public_key: str | os.PathLike[str]) -> tuple[JsonObject, str]:
    """Verify a signed envelope and return its report and canonical digest."""
    require(
        isinstance(envelope, dict) and set(envelope) == {"report", "signatureBase64"},
        "deployment-envelope-shape",
    )
    report = envelope["report"]
    require(isinstance(report, dict), "deployment-report-shape")
    encoded = envelope["signatureBase64"]
    require(
        isinstance(encoded, str) and len(encoded) <= MAX_SIGNATURE_TEXT,
        "deployment-signature-shape",
    )
    signature = base64.b64decode(encoded, validate=True)
    require(len(signature) == SIGNATURE_BYTES, "deployment-signature-length")
    key = private_path(public_key)
    body = canonical(report)
    _run_signature(["-verify", "-pubin", "-inkey", str(key)], body, signature)
    return report, hashlib.sha256(body).hexdigest()


def verify_root_signed(
    root_key: bytes | str | os.PathLike[str], envelope: JsonObject
) -> tuple[JsonObject, str]:
    """Verify an envelope against a reviewed root key given as bytes or as a private key file."""
    if not isinstance(root_key, bytes):
        return verify(envelope, root_key)
    with tempfile.TemporaryDirectory(prefix="claimcore-root-") as raw:
        root = Path(raw).resolve()
        root.chmod(0o700)
        key = root / "root.pub"
        key.write_bytes(root_key)
        key.chmod(0o600)
        return verify(envelope, key)

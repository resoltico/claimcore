"""Private, canonical signed deployment-probe documents."""

import base64
import hashlib
import json
import os
import re
import stat
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path


class DeploymentRefusal(Exception):
    pass


def require(value, category):
    if not value:
        raise DeploymentRefusal(category)


def private_path(raw, directory=False, owner=True):
    target = Path(os.path.abspath(raw))
    for component in (target, *target.parents):
        require(not stat.S_ISLNK(os.lstat(component).st_mode), "linked-private-path")
    for private in (target.parent, target):
        flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC
        if private == target.parent or directory:
            flags |= os.O_DIRECTORY
        try:
            descriptor = os.open(private, flags)
        except OSError:
            raise DeploymentRefusal("private-path-inaccessible") from None
        try:
            status = os.fstat(descriptor)
            expected_kind = (
                stat.S_ISDIR if private == target.parent or directory else stat.S_ISREG
            )
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


def sys_platform_darwin():
    return os.uname().sysname == "Darwin"


def canonical(value):
    return (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
        + "\n"
    ).encode("ascii")


def timestamp(value):
    require(isinstance(value, str), "timestamp-type")
    require(
        re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", value)
        is not None,
        "timestamp-format",
    )
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def utc(value):
    return (
        value.astimezone(timezone.utc)
        .isoformat(timespec="seconds")
        .replace("+00:00", "Z")
    )


def _run_signature(args, input_bytes, signature_bytes=None):
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
            require(len(result_bytes) == 64, "deployment-signature-length")
            return result_bytes
        return None


def sign(report, private_key):
    key = private_path(private_key)
    body = canonical(report)
    signature = _run_signature(["-sign", "-inkey", str(key)], body)
    return {
        "report": report,
        "signatureBase64": base64.b64encode(signature).decode("ascii"),
    }


def verify(envelope, public_key):
    require(
        isinstance(envelope, dict) and set(envelope) == {"report", "signatureBase64"},
        "deployment-envelope-shape",
    )
    report = envelope["report"]
    require(isinstance(report, dict), "deployment-report-shape")
    encoded = envelope["signatureBase64"]
    require(
        isinstance(encoded, str) and len(encoded) <= 128, "deployment-signature-shape"
    )
    signature = base64.b64decode(encoded, validate=True)
    require(len(signature) == 64, "deployment-signature-length")
    key = private_path(public_key)
    body = canonical(report)
    _run_signature(["-verify", "-pubin", "-inkey", str(key)], body, signature)
    return report, hashlib.sha256(body).hexdigest()

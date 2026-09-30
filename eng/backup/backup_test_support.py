"""Helpers shared by the backup drills: key pairs, private files and refusal assertions."""

import base64
import subprocess
from collections.abc import Callable
from pathlib import Path

from backup_types import JsonObject
from deployment_common import DeploymentRefusalError, canonical, sign


def keypair(root: Path, name: str) -> tuple[Path, Path]:
    """Create an Ed25519 key pair beside `root`, readable only by the owner."""
    private, public = root / (name + ".key"), root / (name + ".pub")
    for arguments in (
        ["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(private)],
        ["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)],
    ):
        completed = subprocess.run(
            arguments, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False
        )
        if completed.returncode != 0:
            msg = f"openssl failed to create {name}"
            raise RuntimeError(msg)
    private.chmod(0o600)
    public.chmod(0o600)
    return private, public


def private_bytes(root: Path, name: str, data: bytes) -> Path:
    """Write an owner-only file beneath `root`."""
    path = root / name
    path.write_bytes(data)
    path.chmod(0o600)
    return path


def signed_files(root: Path, name: str, document: JsonObject, key: Path) -> tuple[Path, Path]:
    """Write `document` and its detached raw Ed25519 signature as owner-only files."""
    envelope = sign(document, key)
    return (
        private_bytes(root, name + ".json", canonical(document)),
        private_bytes(root, name + ".sig", base64.b64decode(envelope["signatureBase64"])),
    )


def refuses(action: Callable[[], object], category: str) -> None:
    """Require `action` to refuse with exactly the safe `category`."""
    try:
        action()
    except DeploymentRefusalError as error:
        if error.args[0] != category:
            msg = f"Expected refusal {category}, got {error.args[0]}"
            raise AssertionError(msg) from None
        return
    msg = f"Expected refusal {category}, but the action succeeded"
    raise AssertionError(msg)


def ensure(condition: object, message: str) -> None:
    """Fail the drill with `message` unless `condition` holds."""
    if not condition:
        raise AssertionError(message)

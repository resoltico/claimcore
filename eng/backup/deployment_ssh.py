"""Fixed, host-key-pinned ssh transport for owner-only deployment probes."""

import base64
import binascii
import hashlib
import re
import subprocess
from collections.abc import Sequence
from pathlib import Path

from backup_types import JsonObject
from deployment_common import DeploymentRefusalError, private_path, require

KNOWN_HOSTS_LINES = 1024
HOST_KEY_BYTES = 2048
MAX_PORT = 65535
STANDARD_PORT = 22


def matching_text(value: object, pattern: str, category: str) -> str:
    """Return `value` when it is text that matches `pattern` completely."""
    require(isinstance(value, str) and re.fullmatch(pattern, value) is not None, category)
    return str(value)


def _host_key_digest(words: list[str], lookup: str) -> str | None:
    require(len(words) in (3, 4), "ssh-known-hosts-invalid")
    require(not words[0].startswith("@"), "ssh-known-hosts-ambiguous")
    host_pattern, algorithm, encoded = words[:3]
    require(
        not any(mark in host_pattern for mark in ("*", "?", "!", "|", ",")),
        "ssh-known-hosts-ambiguous",
    )
    if host_pattern != lookup:
        return None
    require(algorithm == "ssh-ed25519", "ssh-host-key-algorithm")
    try:
        key_bytes = base64.b64decode(encoded, validate=True)
    except (ValueError, binascii.Error):
        msg = "ssh-known-hosts-invalid"
        raise DeploymentRefusalError(msg) from None
    require(len(key_bytes) <= HOST_KEY_BYTES, "ssh-known-hosts-invalid")
    return hashlib.sha256(key_bytes).hexdigest()


def require_pinned_host_key(known: Path, lookup: str, expected: str) -> None:
    """Require exactly one unambiguous known-hosts entry for `lookup` with the expected digest."""
    matching_text(expected, r"[0-9a-f]{64}", "ssh-host-key-digest")
    try:
        lines = known.read_text("ascii").splitlines()
    except (OSError, UnicodeError):
        msg = "ssh-known-hosts-invalid"
        raise DeploymentRefusalError(msg) from None
    require(len(lines) <= KNOWN_HOSTS_LINES, "ssh-known-hosts-invalid")
    digests = [
        digest
        for line in lines
        if line and not line.startswith("#")
        for digest in [_host_key_digest(line.split(), lookup)]
        if digest is not None
    ]
    require(len(digests) == 1, "ssh-host-key-not-uniquely-pinned")
    require(digests[0] == expected, "ssh-host-key-digest")


def _endpoint(pinned: JsonObject) -> tuple[str, str, str, int]:
    host = matching_text(pinned["sshHost"], r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}", "ssh-host")
    user = matching_text(pinned["sshUser"], r"[A-Za-z_][A-Za-z0-9_-]{0,31}", "ssh-user")
    remote = matching_text(pinned["remoteProbePath"], r"/[A-Za-z0-9_./-]{1,255}", "ssh-probe-path")
    require(".." not in Path(remote).parts, "ssh-probe-path")
    port = pinned["sshPort"]
    require(type(port) is int and 1 <= port <= MAX_PORT, "ssh-port")
    return host, user, remote, port


def fixed_ssh_command(identity: Path, known: Path, port: int) -> list[str]:
    """Return the ssh invocation prefix that forbids every implicit trust and routing path."""
    options = [
        "IdentitiesOnly=yes",
        "BatchMode=yes",
        "StrictHostKeyChecking=yes",
        f"UserKnownHostsFile={known}",
        "GlobalKnownHostsFile=/dev/null",
        "ProxyCommand=none",
        "ProxyJump=none",
        "ConnectTimeout=10",
    ]
    command = ["ssh", "-F", "/dev/null", "-i", str(identity)]
    for option in options:
        command.extend(["-o", option])
    return [*command, "-p", str(port)]


def ssh_command(
    config: JsonObject,
    role: str,
    nonce: str,
    qualification_sha: str,
    challenge: str | None,
    *,
    pinned: JsonObject | None = None,
    extra: Sequence[str] = (),
) -> list[str]:
    """Build the fixed ssh command for one role after pinning its host key."""
    pinned = config["roles"][role] if pinned is None else pinned
    host, user, remote, port = _endpoint(pinned)
    known = private_path(pinned["knownHostsFile"])
    identity = private_path(pinned["sshIdentityFile"])
    private_path(pinned["probePublicKey"])
    lookup = host if port == STANDARD_PORT else f"[{host}]:{port}"
    require_pinned_host_key(known, lookup, pinned["sshHostKeySha256"])
    known_result = subprocess.run(
        ["ssh-keygen", "-F", lookup, "-f", str(known)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    require(known_result.returncode == 0, "ssh-host-key-not-pinned")
    command = [
        *fixed_ssh_command(identity, known, port),
        f"{user}@{host}",
        remote,
        "--role",
        role,
        "--nonce",
        nonce,
        "--qualification-sha256",
        qualification_sha,
    ]
    if role == "key":
        require(challenge is not None, "key-challenge-missing")
        command.extend(["--key-challenge", str(challenge)])
    command.extend(extra)
    return command

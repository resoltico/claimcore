"""Root-pinned strict SSH to the fixed remote CHECKPOINT signer subcommand."""

import base64
import hashlib
import json
import os
import re
import selectors
import subprocess
import tempfile
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

from deployment_common import (
    DeploymentRefusal,
    canonical,
    private_path,
    require,
    timestamp,
    verify,
)
from deployment_publication_root import reviewed_publication_root
from deployment_verify import _pinned_host_key, _text

REMOTE_COMMAND = "/usr/local/libexec/claimcore/CheckpointSigner.py"
FIELDS = {
    "format",
    "installationId",
    "lineageId",
    "epoch",
    "purpose",
    "checkpointSigningKeyId",
    "checkpointPublicKeySha256",
    "sshHost",
    "sshUser",
    "sshPort",
    "sshHostKeySha256",
    "machineHash",
    "storageHash",
    "adminActorId",
    "issuedAt",
    "validUntil",
}


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (TypeError, ValueError, AttributeError):
        return False


def topology(config, candidate, *, root_key=None, now=None):
    require(
        "checkpointSignerRootPath" not in config
        and "checkpointSignerCommand" not in config,
        "checkpoint-remote-config",
    )
    root_key = reviewed_publication_root() if root_key is None else root_key
    require(root_key is not None, "publication-root-unavailable")
    source = private_path(config["checkpointSignerTopologyFile"])
    detached = private_path(config["checkpointSignerTopologySignatureFile"])
    raw, signed = source.read_bytes(), detached.read_bytes()
    require(0 < len(raw) <= 16384 and len(signed) == 64, "checkpoint-topology-size")
    body = json.loads(raw)
    require(
        isinstance(body, dict) and raw == canonical(body),
        "checkpoint-topology-canonical",
    )
    if isinstance(root_key, bytes):
        with tempfile.TemporaryDirectory(
            prefix="claimcore-checkpoint-root-"
        ) as temporary:
            root = Path(temporary).resolve()
            root.chmod(0o700)
            key = root / "root.pub"
            key.write_bytes(root_key)
            key.chmod(0o600)
            report, digest = verify(
                {
                    "report": body,
                    "signatureBase64": base64.b64encode(signed).decode("ascii"),
                },
                key,
            )
    else:
        report, digest = verify(
            {
                "report": body,
                "signatureBase64": base64.b64encode(signed).decode("ascii"),
            },
            root_key,
        )
    require(
        set(report) == FIELDS
        and report["format"] == "claimcore-checkpoint-signer-topology-1"
        and report["purpose"] == "CHECKPOINT",
        "checkpoint-topology-shape",
    )
    for name in (
        "installationId",
        "lineageId",
        "checkpointSigningKeyId",
        "adminActorId",
    ):
        require(_uuid(report[name]), "checkpoint-topology-identity")
    require(
        report["installationId"] == candidate["installationId"]
        and report["lineageId"] == candidate["lineageId"]
        and report["epoch"] == candidate["epoch"]
        and report["checkpointSigningKeyId"] == candidate["checkpointSigningKeyId"],
        "checkpoint-topology-candidate",
    )
    for name in (
        "checkpointPublicKeySha256",
        "sshHostKeySha256",
        "machineHash",
        "storageHash",
    ):
        require(
            isinstance(report[name], str)
            and re.fullmatch(r"[0-9a-f]{64}", report[name]),
            "checkpoint-topology-digest",
        )
    public = private_path(config["checkpointVerificationKey"])
    require(
        hashlib.sha256(public.read_bytes()).hexdigest()
        == report["checkpointPublicKeySha256"],
        "checkpoint-topology-key",
    )
    issued, expires = timestamp(report["issuedAt"]), timestamp(report["validUntil"])
    now = now or datetime.now(timezone.utc)
    require(
        issued <= now < expires <= issued + timedelta(days=7),
        "checkpoint-topology-expired",
    )
    return report, digest


def ssh_command(config, manifest):
    host = _text(manifest["sshHost"], r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}", "ssh-host")
    user = _text(manifest["sshUser"], r"[A-Za-z_][A-Za-z0-9_-]{0,31}", "ssh-user")
    port = manifest["sshPort"]
    require(type(port) is int and 1 <= port <= 65535, "ssh-port")
    known = private_path(config["checkpointKnownHostsFile"])
    identity = private_path(config["checkpointSshIdentityFile"])
    lookup = host if port == 22 else f"[{host}]:{port}"
    _pinned_host_key(known, lookup, manifest["sshHostKeySha256"])
    found = subprocess.run(
        ["ssh-keygen", "-F", lookup, "-f", str(known)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    require(found.returncode == 0, "ssh-host-key-not-pinned")
    return [
        "ssh",
        "-F",
        "/dev/null",
        "-i",
        str(identity),
        "-o",
        "IdentitiesOnly=yes",
        "-o",
        "BatchMode=yes",
        "-o",
        "StrictHostKeyChecking=yes",
        "-o",
        f"UserKnownHostsFile={known}",
        "-o",
        "GlobalKnownHostsFile=/dev/null",
        "-o",
        "ProxyCommand=none",
        "-o",
        "ProxyJump=none",
        "-o",
        "ConnectTimeout=10",
        "-p",
        str(port),
        f"{user}@{host}",
        REMOTE_COMMAND,
        "--stdio-sign",
    ]


def _exchange(command, payload):
    try:
        child = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
        )
        child.stdin.write(payload)
        child.stdin.close()
        output = bytearray()
        deadline = time.monotonic() + 30
        with selectors.DefaultSelector() as selector:
            selector.register(child.stdout, selectors.EVENT_READ)
            while selector.get_map():
                remaining = deadline - time.monotonic()
                require(remaining > 0, "checkpoint-remote-timeout")
                for key, _ in selector.select(remaining):
                    block = os.read(key.fd, min(4096, 32769 - len(output)))
                    if not block:
                        selector.unregister(key.fileobj)
                    else:
                        output.extend(block)
                        require(
                            len(output) <= 32768, "checkpoint-remote-response-limit"
                        )
        require(child.wait(timeout=1) == 0, "checkpoint-remote-refused")
        return bytes(output)
    except (OSError, subprocess.TimeoutExpired):
        raise DeploymentRefusal("checkpoint-remote-unavailable") from None
    finally:
        if "child" in locals() and child.poll() is None:
            child.kill()
            child.wait()


def request_remote(config, payload, candidate, *, root_key=None, runner=None):
    manifest, _ = topology(config, candidate, root_key=root_key)
    command = ssh_command(config, manifest)
    raw = _exchange(command, payload) if runner is None else runner(command, payload)
    require(
        isinstance(raw, bytes) and 0 < len(raw) <= 32768,
        "checkpoint-remote-response-limit",
    )
    response = json.loads(raw)
    require(
        isinstance(response, dict) and raw == canonical(response),
        "checkpoint-remote-response",
    )
    return response

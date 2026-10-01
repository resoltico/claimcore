"""Root-pinned strict SSH to the fixed remote CHECKPOINT signer subcommand."""

import base64
import hashlib
import json
import os
import selectors
import subprocess
import time
from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from pathlib import Path

from backup_types import JsonObject
from deployment_common import (
    SIGNATURE_BYTES,
    DeploymentRefusalError,
    canonical,
    is_sha256,
    is_uuid,
    private_path,
    require,
    timestamp,
    verify_root_signed,
)
from deployment_publication_root import reviewed_publication_root
from deployment_ssh import fixed_ssh_command, matching_text, require_pinned_host_key

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
TOPOLOGY_LIMIT = 16384
RESPONSE_LIMIT = 32768
RECEIVE_BLOCK = 4096
EXCHANGE_TIMEOUT_SECONDS = 30
VALIDITY_DAYS = 7
MAX_PORT = 65535
STANDARD_PORT = 22


def _read_topology(config: JsonObject, root_key: bytes | str | Path) -> tuple[JsonObject, str]:
    source = private_path(config["checkpointSignerTopologyFile"])
    detached = private_path(config["checkpointSignerTopologySignatureFile"])
    raw, signed = source.read_bytes(), detached.read_bytes()
    require(
        0 < len(raw) <= TOPOLOGY_LIMIT and len(signed) == SIGNATURE_BYTES,
        "checkpoint-topology-size",
    )
    body = json.loads(raw)
    require(isinstance(body, dict) and raw == canonical(body), "checkpoint-topology-canonical")
    return verify_root_signed(
        root_key,
        {"report": body, "signatureBase64": base64.b64encode(signed).decode("ascii")},
    )


def _check_topology(report: JsonObject, candidate: JsonObject, config: JsonObject) -> None:
    require(
        set(report) == FIELDS
        and report["format"] == "claimcore-checkpoint-signer-topology-1"
        and report["purpose"] == "CHECKPOINT",
        "checkpoint-topology-shape",
    )
    for name in ("installationId", "lineageId", "checkpointSigningKeyId", "adminActorId"):
        require(is_uuid(report[name]), "checkpoint-topology-identity")
    require(
        report["installationId"] == candidate["installationId"]
        and report["lineageId"] == candidate["lineageId"]
        and report["epoch"] == candidate["epoch"]
        and report["checkpointSigningKeyId"] == candidate["checkpointSigningKeyId"],
        "checkpoint-topology-candidate",
    )
    for name in ("checkpointPublicKeySha256", "sshHostKeySha256", "machineHash", "storageHash"):
        require(is_sha256(report[name]), "checkpoint-topology-digest")
    public = private_path(config["checkpointVerificationKey"])
    require(
        hashlib.sha256(public.read_bytes()).hexdigest() == report["checkpointPublicKeySha256"],
        "checkpoint-topology-key",
    )


def topology(
    config: JsonObject,
    candidate: JsonObject,
    *,
    root_key: bytes | str | Path | None = None,
    now: datetime | None = None,
) -> tuple[JsonObject, str]:
    """Verify the root-signed signer topology against the candidate and configuration."""
    require(
        "checkpointSignerRootPath" not in config and "checkpointSignerCommand" not in config,
        "checkpoint-remote-config",
    )
    root_key = reviewed_publication_root() if root_key is None else root_key
    if root_key is None:
        msg = "publication-root-unavailable"
        raise DeploymentRefusalError(msg)
    report, digest = _read_topology(config, root_key)
    _check_topology(report, candidate, config)
    issued, expires = timestamp(report["issuedAt"]), timestamp(report["validUntil"])
    current = now or datetime.now(UTC)
    require(
        issued <= current < expires <= issued + timedelta(days=VALIDITY_DAYS),
        "checkpoint-topology-expired",
    )
    return report, digest


def ssh_command(config: JsonObject, manifest: JsonObject) -> list[str]:
    """Build the fixed ssh command that runs the remote signer over stdio."""
    host = matching_text(manifest["sshHost"], r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}", "ssh-host")
    user = matching_text(manifest["sshUser"], r"[A-Za-z_][A-Za-z0-9_-]{0,31}", "ssh-user")
    port = manifest["sshPort"]
    require(type(port) is int and 1 <= port <= MAX_PORT, "ssh-port")
    known = private_path(config["checkpointKnownHostsFile"])
    identity = private_path(config["checkpointSshIdentityFile"])
    lookup = host if port == STANDARD_PORT else f"[{host}]:{port}"
    require_pinned_host_key(known, lookup, manifest["sshHostKeySha256"])
    found = subprocess.run(
        ["ssh-keygen", "-F", lookup, "-f", str(known)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    require(found.returncode == 0, "ssh-host-key-not-pinned")
    return [
        *fixed_ssh_command(identity, known, port),
        f"{user}@{host}",
        REMOTE_COMMAND,
        "--stdio-sign",
    ]


def _collect(child: subprocess.Popen[bytes]) -> bytes:
    stream = child.stdout
    if stream is None:
        msg = "checkpoint-remote-unavailable"
        raise DeploymentRefusalError(msg)
    output = bytearray()
    deadline = time.monotonic() + EXCHANGE_TIMEOUT_SECONDS
    with selectors.DefaultSelector() as selector:
        selector.register(stream, selectors.EVENT_READ)
        while selector.get_map():
            remaining = deadline - time.monotonic()
            require(remaining > 0, "checkpoint-remote-timeout")
            for key, _ in selector.select(remaining):
                block = os.read(key.fd, min(RECEIVE_BLOCK, RESPONSE_LIMIT + 1 - len(output)))
                if not block:
                    selector.unregister(key.fileobj)
                else:
                    output.extend(block)
                    require(len(output) <= RESPONSE_LIMIT, "checkpoint-remote-response-limit")
    return bytes(output)


def _exchange(command: list[str], payload: bytes) -> bytes:
    child: subprocess.Popen[bytes] | None = None
    try:
        child = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL
        )
        if child.stdin is None:
            msg = "checkpoint-remote-unavailable"
            raise DeploymentRefusalError(msg)
        child.stdin.write(payload)
        child.stdin.close()
        output = _collect(child)
        require(child.wait(timeout=1) == 0, "checkpoint-remote-refused")
    except (OSError, subprocess.TimeoutExpired):
        msg = "checkpoint-remote-unavailable"
        raise DeploymentRefusalError(msg) from None
    else:
        return output
    finally:
        if child is not None and child.poll() is None:
            child.kill()
            child.wait()


def request_remote(
    config: JsonObject,
    payload: bytes,
    candidate: JsonObject,
    *,
    root_key: bytes | str | Path | None = None,
    runner: Callable[[list[str], bytes], bytes] | None = None,
) -> JsonObject:
    """Send the sign request to the pinned remote signer and return its canonical answer."""
    manifest, _ = topology(config, candidate, root_key=root_key)
    command = ssh_command(config, manifest)
    raw = _exchange(command, payload) if runner is None else runner(command, payload)
    require(
        isinstance(raw, bytes) and 0 < len(raw) <= RESPONSE_LIMIT,
        "checkpoint-remote-response-limit",
    )
    response: JsonObject = json.loads(raw)
    require(isinstance(response, dict) and raw == canonical(response), "checkpoint-remote-response")
    return response

"""Exact local CHECKPOINT signing request; capture never loads its private key."""

import base64
import hashlib
import json
import os
import socket
import stat
import subprocess
import tempfile
from pathlib import Path

from backup_types import JsonObject
from checkpoint_signer_remote import request_remote
from deployment_common import SIGNATURE_BYTES, canonical, private_path, refuse, require

RESPONSE_LIMIT = 32768
REQUEST_LIMIT = 32768
CANDIDATE_LIMIT = 16384
RECEIVE_BLOCK = 4096
SOCKET_TIMEOUT_SECONDS = 10
NONCE_BYTES = 32
RESPONSE_FIELDS = {
    "format",
    "status",
    "nonce",
    "candidateSha256",
    "checkpointSigningKeyId",
    "signatureBase64",
}


def _response(connection: socket.socket) -> JsonObject:
    value = bytearray()
    while len(value) < RESPONSE_LIMIT:
        block = connection.recv(min(RECEIVE_BLOCK, RESPONSE_LIMIT - len(value)))
        require(block, "checkpoint-signer-unavailable")
        value.extend(block)
        if value.endswith(b"\n"):
            document: JsonObject = json.loads(value)
            require(bytes(value) == canonical(document), "checkpoint-signer-response")
            return document
    return refuse("checkpoint-signer-response-limit")


def _verify_signature(public_key: str | Path, candidate: bytes, signed: bytes) -> None:
    with tempfile.TemporaryDirectory(prefix="claimcore-checkpoint-verify-") as raw:
        root = Path(raw).resolve()
        root.chmod(0o700)
        source, signature = root / "checkpoint.json", root / "checkpoint.sig"
        source.write_bytes(candidate)
        signature.write_bytes(signed)
        source.chmod(0o600)
        signature.chmod(0o600)
        result = subprocess.run(
            [
                "openssl",
                "pkeyutl",
                "-verify",
                "-rawin",
                "-pubin",
                "-inkey",
                str(public_key),
                "-in",
                str(source),
                "-sigfile",
                str(signature),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        require(result.returncode == 0, "checkpoint-signer-signature")


def _local_response(sock: Path, payload: bytes) -> JsonObject:
    try:
        info = sock.lstat()
    except OSError:
        refuse("checkpoint-signer-unavailable")
    require(
        stat.S_ISSOCK(info.st_mode) and info.st_uid == os.geteuid() and info.st_mode & 0o077 == 0,
        "checkpoint-signer-socket",
    )
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(SOCKET_TIMEOUT_SECONDS)
            connection.connect(str(sock))
            connection.sendall(payload)
            return _response(connection)
    except (OSError, TimeoutError):
        refuse("checkpoint-signer-unavailable")


def _check_response(response: JsonObject, config: JsonObject, request: JsonObject) -> bytes:
    require(
        isinstance(response, dict)
        and set(response) == RESPONSE_FIELDS
        and response["format"] == "claimcore-checkpoint-sign-response-1"
        and response["status"] == "SIGNED"
        and response["nonce"] == request["nonce"]
        and response["candidateSha256"] == request["candidateSha256"]
        and response["checkpointSigningKeyId"] == config["checkpointSigningKeyId"],
        "checkpoint-signer-response",
    )
    signature = base64.b64decode(response["signatureBase64"], validate=True)
    require(len(signature) == SIGNATURE_BYTES, "checkpoint-signer-signature")
    return signature


def _write_signature(source: Path, target: str | Path, signature: bytes) -> None:
    destination = Path(target)
    require(
        destination.parent == source.parent and not destination.exists(),
        "checkpoint-signature-target",
    )
    descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, "wb") as output:
        output.write(signature)
        output.flush()
        os.fsync(output.fileno())


def sign_checkpoint(config: JsonObject, source: str | Path, target: str | Path) -> None:
    """Have the isolated signer sign the exact candidate and store the verified signature."""
    origin = private_path(source)
    raw = origin.read_bytes()
    require(0 < len(raw) <= CANDIDATE_LIMIT, "checkpoint-candidate-size")
    document = json.loads(raw)
    require(
        isinstance(document, dict) and raw == canonical(document),
        "checkpoint-candidate-canonical",
    )
    request = {
        "format": "claimcore-checkpoint-sign-request-1",
        "nonce": os.urandom(NONCE_BYTES).hex(),
        "checkpointSigningKeyId": config["checkpointSigningKeyId"],
        "candidateSha256": hashlib.sha256(raw).hexdigest(),
        "candidateBase64": base64.b64encode(raw).decode("ascii"),
    }
    payload = canonical(request)
    require(len(payload) <= REQUEST_LIMIT, "checkpoint-sign-request-limit")
    if config["checkpointSignerMode"] == "LOCAL_SYNTHETIC":
        response = _local_response(config["checkpointSignerSocket"], payload)
    else:
        response = request_remote(config, payload, document)
    signature = _check_response(response, config, request)
    _verify_signature(config["checkpointVerificationKey"], raw, signature)
    _write_signature(origin, target, signature)

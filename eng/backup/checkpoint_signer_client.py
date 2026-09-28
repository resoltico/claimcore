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

from deployment_common import canonical, private_path, require


def _response(connection):
    value = bytearray()
    while len(value) < 32768:
        block = connection.recv(min(4096, 32768 - len(value)))
        require(block, "checkpoint-signer-unavailable")
        value.extend(block)
        if value.endswith(b"\n"):
            document = json.loads(value)
            require(bytes(value) == canonical(document), "checkpoint-signer-response")
            return document
    require(False, "checkpoint-signer-response-limit")


def _verify_signature(public_key, candidate, signed):
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


def _local_response(sock, payload):
    try:
        info = sock.lstat()
    except OSError:
        require(False, "checkpoint-signer-unavailable")
    require(
        stat.S_ISSOCK(info.st_mode)
        and info.st_uid == os.geteuid()
        and info.st_mode & 0o077 == 0,
        "checkpoint-signer-socket",
    )
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(10)
            connection.connect(str(sock))
            connection.sendall(payload)
            return _response(connection)
    except (OSError, TimeoutError):
        require(False, "checkpoint-signer-unavailable")


def sign_checkpoint(config, source, target):
    source = private_path(source)
    raw = source.read_bytes()
    require(0 < len(raw) <= 16384, "checkpoint-candidate-size")
    document = json.loads(raw)
    require(
        isinstance(document, dict) and raw == canonical(document),
        "checkpoint-candidate-canonical",
    )
    nonce = os.urandom(32).hex()
    request = {
        "format": "claimcore-checkpoint-sign-request-1",
        "nonce": nonce,
        "checkpointSigningKeyId": config["checkpointSigningKeyId"],
        "candidateSha256": hashlib.sha256(raw).hexdigest(),
        "candidateBase64": base64.b64encode(raw).decode("ascii"),
    }
    payload = canonical(request)
    require(len(payload) <= 32768, "checkpoint-sign-request-limit")
    if config["checkpointSignerMode"] == "LOCAL_SYNTHETIC":
        response = _local_response(config["checkpointSignerSocket"], payload)
    else:
        from checkpoint_signer_remote import request_remote

        response = request_remote(config, payload, document)
    require(
        isinstance(response, dict)
        and set(response)
        == {
            "format",
            "status",
            "nonce",
            "candidateSha256",
            "checkpointSigningKeyId",
            "signatureBase64",
        }
        and response["format"] == "claimcore-checkpoint-sign-response-1"
        and response["status"] == "SIGNED"
        and response["nonce"] == nonce
        and response["candidateSha256"] == request["candidateSha256"]
        and response["checkpointSigningKeyId"] == config["checkpointSigningKeyId"],
        "checkpoint-signer-response",
    )
    signature = base64.b64decode(response["signatureBase64"], validate=True)
    require(len(signature) == 64, "checkpoint-signer-signature")
    _verify_signature(config["checkpointVerificationKey"], raw, signature)
    target = Path(target)
    require(
        target.parent == source.parent and not target.exists(),
        "checkpoint-signature-target",
    )
    descriptor = os.open(
        target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600
    )
    with os.fdopen(descriptor, "wb") as output:
        output.write(signature)
        output.flush()
        os.fsync(output.fileno())

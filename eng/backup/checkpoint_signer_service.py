"""Separate local CHECKPOINT signer; synthetic IPC is not independent host custody."""

import base64
import contextlib
import json
import os
import socket
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

from backup_types import JsonObject
from checkpoint_signer_policy import decode_candidate, require_current_candidate
from deployment_common import (
    SIGNATURE_BYTES,
    DeploymentRefusalError,
    canonical,
    private_path,
    require,
    verify,
)
from managed_common import sync_directory

REQUEST_LIMIT = 32768
RECEIVE_BLOCK = 4096
CONNECTION_TIMEOUT_SECONDS = 10
BACKLOG = 4


def _sign(config: JsonObject, source: bytes) -> bytes:
    with tempfile.TemporaryDirectory(prefix="claimcore-checkpoint-sign-") as raw:
        root = Path(raw).resolve()
        root.chmod(0o700)
        statement, signature = root / "candidate.json", root / "candidate.sig"
        statement.write_bytes(source)
        statement.chmod(0o600)
        result = subprocess.run(
            [
                "openssl",
                "pkeyutl",
                "-sign",
                "-rawin",
                "-inkey",
                str(config["signingKeyFile"]),
                "-in",
                str(statement),
                "-out",
                str(signature),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        require(
            result.returncode == 0 and signature.stat().st_size == SIGNATURE_BYTES,
            "checkpoint-signature-unavailable",
        )
        verified = subprocess.run(
            [
                "openssl",
                "pkeyutl",
                "-verify",
                "-rawin",
                "-pubin",
                "-inkey",
                str(config["verificationKeyFile"]),
                "-in",
                str(statement),
                "-sigfile",
                str(signature),
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        require(verified.returncode == 0, "checkpoint-signer-key-mismatch")
        return signature.read_bytes()


def _recorded_signature(
    config: JsonObject, record: Path, request: JsonObject, candidate: JsonObject
) -> str:
    raw = private_path(record).read_bytes()
    saved = json.loads(raw)
    require(
        isinstance(saved, dict)
        and set(saved) == {"cycleId", "candidateSha256", "signatureBase64"}
        and raw == canonical(saved)
        and saved["cycleId"] == candidate["cycleId"],
        "checkpoint-ledger-invalid",
    )
    require(saved["candidateSha256"] == request["candidateSha256"], "checkpoint-cycle-reused")
    encoded: str = saved["signatureBase64"]
    try:
        verify({"report": candidate, "signatureBase64": encoded}, config["verificationKeyFile"])
    except DeploymentRefusalError:
        msg = "checkpoint-ledger-signature-invalid"
        raise DeploymentRefusalError(msg) from None
    return encoded


def _record_signature(
    config: JsonObject, record: Path, candidate: JsonObject, request: JsonObject, raw: bytes
) -> str:
    encoded = base64.b64encode(_sign(config, raw)).decode("ascii")
    result = canonical(
        {
            "cycleId": candidate["cycleId"],
            "candidateSha256": request["candidateSha256"],
            "signatureBase64": encoded,
        }
    )
    descriptor = os.open(record, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(result)
        stream.flush()
        os.fsync(stream.fileno())
    return encoded


def sign_request(config: JsonObject, request: JsonObject) -> JsonObject:
    """Admit the candidate, sign it once per cycle, and return the signed response."""
    candidate, raw = decode_candidate(config, request)
    record = config["ledgerRoot"] / (candidate["cycleId"] + ".json")
    if record.exists():
        encoded = _recorded_signature(config, record, request, candidate)
    else:
        require_current_candidate(candidate)
        encoded = _record_signature(config, record, candidate, request, raw)
    sync_directory(record.parent)
    return {
        "format": "claimcore-checkpoint-sign-response-1",
        "status": "SIGNED",
        "nonce": request["nonce"],
        "candidateSha256": request["candidateSha256"],
        "checkpointSigningKeyId": config["checkpointSigningKeyId"],
        "signatureBase64": encoded,
    }


def _receive(connection: socket.socket) -> bytes:
    raw = bytearray()
    while len(raw) < REQUEST_LIMIT:
        block = connection.recv(min(RECEIVE_BLOCK, REQUEST_LIMIT - len(raw)))
        if not block:
            break
        raw.extend(block)
        if raw.endswith(b"\n"):
            break
    return bytes(raw)


def _answer(config: JsonObject, raw: bytes) -> JsonObject:
    try:
        require(0 < len(raw) <= REQUEST_LIMIT, "checkpoint-request-limit")
        request = json.loads(raw)
        require(canonical(request) == raw, "checkpoint-request-canonical")
        return sign_request(config, request)
    except (DeploymentRefusalError, ValueError, KeyError, TypeError) as error:
        return {
            "format": "claimcore-checkpoint-sign-response-1",
            "status": "REFUSED",
            "reason": error.args[0]
            if isinstance(error, DeploymentRefusalError)
            else "checkpoint-request-refused",
        }


def serve(config: JsonObject) -> None:
    """Serve sign requests on the local socket until terminated."""
    path: Path = config["socketPath"]
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
        listener.bind(str(path))
        path.chmod(0o600)
        listener.listen(BACKLOG)
        try:
            while True:
                connection, _ = listener.accept()
                with connection:
                    connection.settimeout(CONNECTION_TIMEOUT_SECONDS)
                    connection.sendall(canonical(_answer(config, _receive(connection))))
        finally:
            with contextlib.suppress(OSError):
                if stat.S_ISSOCK(path.lstat().st_mode):
                    path.unlink()


def serve_once(config: JsonObject) -> None:
    """Answer exactly one request read from stdin, for the remote stdio transport."""
    raw = sys.stdin.buffer.readline(REQUEST_LIMIT + 1)
    require(0 < len(raw) <= REQUEST_LIMIT and raw.endswith(b"\n"), "checkpoint-request-limit")
    request = json.loads(raw)
    require(canonical(request) == raw, "checkpoint-request-canonical")
    sys.stdout.buffer.write(canonical(sign_request(config, request)))
    sys.stdout.buffer.flush()

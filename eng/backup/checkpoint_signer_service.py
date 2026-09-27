"""Separate local CHECKPOINT signer; synthetic IPC is not independent host custody."""

import base64
import json
import os
import socket
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

from checkpoint_signer_policy import _candidate
from deployment_common import (
    DeploymentRefusal,
    canonical,
    private_path,
    require,
)


def _sign(config, source):
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
            result.returncode == 0 and signature.stat().st_size == 64,
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


def sign_request(config, request):
    candidate, raw = _candidate(config, request)
    record = config["ledgerRoot"] / (candidate["cycleId"] + ".json")
    if record.exists():
        record = private_path(record)
        saved = json.loads(record.read_bytes())
        require(
            saved["candidateSha256"] == request["candidateSha256"],
            "checkpoint-cycle-reused",
        )
        encoded = saved["signatureBase64"]
    else:
        signature = _sign(config, raw)
        encoded = base64.b64encode(signature).decode("ascii")
        result = canonical(
            {
                "cycleId": candidate["cycleId"],
                "candidateSha256": request["candidateSha256"],
                "signatureBase64": encoded,
            }
        )
        descriptor = os.open(
            record, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600
        )
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(result)
            stream.flush()
            os.fsync(stream.fileno())
    return {
        "format": "claimcore-checkpoint-sign-response-1",
        "status": "SIGNED",
        "nonce": request["nonce"],
        "candidateSha256": request["candidateSha256"],
        "checkpointSigningKeyId": config["checkpointSigningKeyId"],
        "signatureBase64": encoded,
    }


def serve(config):
    path = config["socketPath"]
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
        listener.bind(str(path))
        os.chmod(path, 0o600)
        listener.listen(4)
        try:
            while True:
                connection, _ = listener.accept()
                with connection:
                    connection.settimeout(10)
                    raw = bytearray()
                    while len(raw) < 32768:
                        block = connection.recv(min(4096, 32768 - len(raw)))
                        if not block:
                            break
                        raw.extend(block)
                        if raw.endswith(b"\n"):
                            break
                    try:
                        require(0 < len(raw) <= 32768, "checkpoint-request-limit")
                        request = json.loads(raw)
                        require(
                            canonical(request) == raw, "checkpoint-request-canonical"
                        )
                        response = sign_request(config, request)
                    except (
                        DeploymentRefusal,
                        ValueError,
                        KeyError,
                        TypeError,
                    ) as error:
                        response = {
                            "format": "claimcore-checkpoint-sign-response-1",
                            "status": "REFUSED",
                            "reason": error.args[0]
                            if isinstance(error, DeploymentRefusal)
                            else "checkpoint-request-refused",
                        }
                    connection.sendall(canonical(response))
        finally:
            try:
                if stat.S_ISSOCK(path.lstat().st_mode):
                    path.unlink()
            except OSError:
                pass


def serve_once(config):
    raw = sys.stdin.buffer.readline(32769)
    require(0 < len(raw) <= 32768 and raw.endswith(b"\n"), "checkpoint-request-limit")
    request = json.loads(raw)
    require(canonical(request) == raw, "checkpoint-request-canonical")
    sys.stdout.buffer.write(canonical(sign_request(config, request)))
    sys.stdout.buffer.flush()

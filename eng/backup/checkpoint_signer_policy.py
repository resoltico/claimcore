"""Closed CHECKPOINT signer config and exact candidate admission."""

import base64
import hashlib
import json
import os
import re
import stat
import uuid
from datetime import datetime, timezone
from pathlib import Path

from deployment_common import (
    DeploymentRefusal,
    canonical,
    private_path,
    require,
    timestamp,
)

REQUEST = {
    "format",
    "nonce",
    "checkpointSigningKeyId",
    "candidateSha256",
    "candidateBase64",
}
CANDIDATE = {
    "format",
    "cycleId",
    "installationId",
    "lineageId",
    "epoch",
    "sequence",
    "hash",
    "capturedAt",
    "checkpointSigningKeyId",
    "checkpointCustodianCommitment",
    "leaseId",
    "captureNonce",
    "writerGeneration",
    "backupCaptureSequence",
    "backupCaptureHash",
    "maintenanceEvidenceSha256",
}


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (TypeError, ValueError, AttributeError):
        return False


def _sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def socket_directory(path):
    root = Path(path)
    require(root.is_absolute(), "checkpoint-signer-socket")
    for ancestor in (root, *root.parents):
        require(not ancestor.is_symlink(), "checkpoint-signer-socket-link")
    try:
        info = root.lstat()
    except OSError:
        raise DeploymentRefusal("checkpoint-signer-socket") from None
    require(
        stat.S_ISDIR(info.st_mode)
        and info.st_uid == os.geteuid()
        and info.st_mode & 0o077 == 0,
        "checkpoint-signer-socket-permissions",
    )
    return root


def configuration(path, expected_transport="LOCAL_SOCKET"):
    source = private_path(path)
    require(source.stat().st_size <= 16384, "checkpoint-signer-config-size")
    config = json.loads(source.read_bytes())
    require(
        isinstance(config, dict)
        and set(config)
        == {
            "format",
            "purpose",
            "socketPath",
            "ledgerRoot",
            "signingKeyFile",
            "verificationKeyFile",
            "checkpointSigningKeyId",
            "installationId",
            "lineageId",
            "epoch",
            "transport",
        },
        "checkpoint-signer-config",
    )
    require(
        config["format"] == "claimcore-checkpoint-signer-config-1"
        and config["purpose"] == "CHECKPOINT"
        and config["transport"] == expected_transport,
        "checkpoint-signer-purpose",
    )
    for name in ("checkpointSigningKeyId", "installationId", "lineageId"):
        require(_uuid(config[name]), "checkpoint-signer-identity")
    require(
        type(config["epoch"]) is int and config["epoch"] > 0,
        "checkpoint-signer-identity",
    )
    config["ledgerRoot"] = private_path(config["ledgerRoot"], directory=True)
    config["signingKeyFile"] = private_path(config["signingKeyFile"])
    config["verificationKeyFile"] = private_path(config["verificationKeyFile"])
    if expected_transport == "LOCAL_SOCKET":
        sock = Path(config["socketPath"])
        require(
            sock.is_absolute() and not sock.exists() and len(os.fsencode(sock)) < 100,
            "checkpoint-signer-socket",
        )
        socket_directory(sock.parent)
        config["socketPath"] = sock
    else:
        require(config["socketPath"] is None, "checkpoint-signer-socket")
    return config


def _candidate(config, request):
    require(
        isinstance(request, dict) and set(request) == REQUEST,
        "checkpoint-request-shape",
    )
    require(
        request["format"] == "claimcore-checkpoint-sign-request-1"
        and _sha(request["nonce"]),
        "checkpoint-request-nonce",
    )
    require(
        request["checkpointSigningKeyId"] == config["checkpointSigningKeyId"]
        and _sha(request["candidateSha256"]),
        "checkpoint-request-key",
    )
    try:
        raw = base64.b64decode(request["candidateBase64"], validate=True)
    except (ValueError, base64.binascii.Error):
        raise DeploymentRefusal("checkpoint-request-candidate") from None
    require(
        0 < len(raw) <= 16384
        and hashlib.sha256(raw).hexdigest() == request["candidateSha256"],
        "checkpoint-request-candidate",
    )
    candidate = json.loads(raw)
    require(
        isinstance(candidate, dict)
        and set(candidate) == CANDIDATE
        and canonical(candidate) == raw,
        "checkpoint-candidate-shape",
    )
    require(
        candidate["format"] == "claimcore-witness-checkpoint-1"
        and candidate["checkpointSigningKeyId"] == config["checkpointSigningKeyId"]
        and candidate["installationId"] == config["installationId"]
        and candidate["lineageId"] == config["lineageId"]
        and candidate["epoch"] == config["epoch"]
        and _uuid(candidate["cycleId"])
        and type(candidate["sequence"]) is int
        and candidate["sequence"] >= 0
        and _sha(candidate["hash"])
        and _sha(candidate["checkpointCustodianCommitment"]),
        "checkpoint-candidate-identity",
    )
    captured = timestamp(candidate["capturedAt"])
    require(
        abs((datetime.now(timezone.utc) - captured).total_seconds()) <= 300,
        "checkpoint-candidate-time",
    )
    barrier = (
        "leaseId",
        "captureNonce",
        "writerGeneration",
        "backupCaptureSequence",
        "backupCaptureHash",
        "maintenanceEvidenceSha256",
    )
    if candidate["leaseId"] is None:
        require(
            all(candidate[name] is None for name in barrier), "checkpoint-barrier-shape"
        )
    else:
        require(
            _uuid(candidate["leaseId"])
            and _sha(candidate["captureNonce"])
            and type(candidate["writerGeneration"]) is int
            and candidate["writerGeneration"] > 0
            and candidate["backupCaptureSequence"] == candidate["sequence"]
            and candidate["backupCaptureHash"] == candidate["hash"]
            and _sha(candidate["maintenanceEvidenceSha256"]),
            "checkpoint-barrier-shape",
        )
    return candidate, raw

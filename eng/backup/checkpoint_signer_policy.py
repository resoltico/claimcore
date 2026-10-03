"""Closed CHECKPOINT signer config and exact candidate admission."""

import base64
import hashlib
import json
import os
import stat
from binascii import Error as Base64Error
from datetime import UTC, datetime
from pathlib import Path

from backup_types import JsonObject
from deployment_common import (
    DeploymentRefusalError,
    canonical,
    is_sha256,
    is_uuid,
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
BARRIER = (
    "leaseId",
    "captureNonce",
    "writerGeneration",
    "backupCaptureSequence",
    "backupCaptureHash",
    "maintenanceEvidenceSha256",
)
CONFIG_FIELDS = {
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
}
CONFIG_LIMIT = 16384
CANDIDATE_LIMIT = 16384
SOCKET_PATH_LIMIT = 100
MAX_CLOCK_SKEW_SECONDS = 300


def socket_directory(path: Path) -> Path:
    """Return the owner-only, unlinked directory that will hold the signer socket."""
    root = Path(path)
    require(root.is_absolute(), "checkpoint-signer-socket")
    for ancestor in (root, *root.parents):
        require(not ancestor.is_symlink(), "checkpoint-signer-socket-link")
    try:
        info = root.lstat()
    except OSError:
        msg = "checkpoint-signer-socket"
        raise DeploymentRefusalError(msg) from None
    require(
        stat.S_ISDIR(info.st_mode) and info.st_uid == os.geteuid() and info.st_mode & 0o077 == 0,
        "checkpoint-signer-socket-permissions",
    )
    return root


def configuration(path: str | Path, expected_transport: str = "LOCAL_SOCKET") -> JsonObject:
    """Load the closed signer configuration for the expected transport."""
    source = private_path(path)
    require(source.stat().st_size <= CONFIG_LIMIT, "checkpoint-signer-config-size")
    config: JsonObject = json.loads(source.read_bytes())
    require(isinstance(config, dict) and set(config) == CONFIG_FIELDS, "checkpoint-signer-config")
    require(
        config["format"] == "claimcore-checkpoint-signer-config-1"
        and config["purpose"] == "CHECKPOINT"
        and config["transport"] == expected_transport,
        "checkpoint-signer-purpose",
    )
    for name in ("checkpointSigningKeyId", "installationId", "lineageId"):
        require(is_uuid(config[name]), "checkpoint-signer-identity")
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
            sock.is_absolute() and not sock.exists() and len(os.fsencode(sock)) < SOCKET_PATH_LIMIT,
            "checkpoint-signer-socket",
        )
        socket_directory(sock.parent)
        config["socketPath"] = sock
    else:
        require(config["socketPath"] is None, "checkpoint-signer-socket")
    return config


def _request_candidate(config: JsonObject, request: JsonObject) -> bytes:
    require(isinstance(request, dict) and set(request) == REQUEST, "checkpoint-request-shape")
    require(
        request["format"] == "claimcore-checkpoint-sign-request-1" and is_sha256(request["nonce"]),
        "checkpoint-request-nonce",
    )
    require(
        request["checkpointSigningKeyId"] == config["checkpointSigningKeyId"]
        and is_sha256(request["candidateSha256"]),
        "checkpoint-request-key",
    )
    try:
        raw = base64.b64decode(request["candidateBase64"], validate=True)
    except (ValueError, Base64Error):
        msg = "checkpoint-request-candidate"
        raise DeploymentRefusalError(msg) from None
    require(
        0 < len(raw) <= CANDIDATE_LIMIT
        and hashlib.sha256(raw).hexdigest() == request["candidateSha256"],
        "checkpoint-request-candidate",
    )
    return raw


def _check_candidate_identity(config: JsonObject, candidate: JsonObject) -> None:
    require(
        candidate["format"] == "claimcore-witness-checkpoint-1"
        and candidate["checkpointSigningKeyId"] == config["checkpointSigningKeyId"]
        and candidate["installationId"] == config["installationId"]
        and candidate["lineageId"] == config["lineageId"]
        and candidate["epoch"] == config["epoch"]
        and is_uuid(candidate["cycleId"])
        and type(candidate["sequence"]) is int
        and candidate["sequence"] >= 0
        and is_sha256(candidate["hash"])
        and is_sha256(candidate["checkpointCustodianCommitment"]),
        "checkpoint-candidate-identity",
    )


def require_current_candidate(candidate: JsonObject) -> None:
    """Freshness authorizes new signing, not exact historical ledger readback."""
    captured = timestamp(candidate["capturedAt"])
    require(
        abs((datetime.now(UTC) - captured).total_seconds()) <= MAX_CLOCK_SKEW_SECONDS,
        "checkpoint-candidate-time",
    )


def _check_barrier(candidate: JsonObject) -> None:
    if candidate["leaseId"] is None:
        require(all(candidate[name] is None for name in BARRIER), "checkpoint-barrier-shape")
        return
    require(
        is_uuid(candidate["leaseId"])
        and is_sha256(candidate["captureNonce"])
        and type(candidate["writerGeneration"]) is int
        and candidate["writerGeneration"] > 0
        and candidate["backupCaptureSequence"] == candidate["sequence"]
        and candidate["backupCaptureHash"] == candidate["hash"]
        and is_sha256(candidate["maintenanceEvidenceSha256"]),
        "checkpoint-barrier-shape",
    )


def decode_candidate(config: JsonObject, request: JsonObject) -> tuple[JsonObject, bytes]:
    """Validate the exact checkpoint identity and bytes before issuance or readback."""
    raw = _request_candidate(config, request)
    candidate = json.loads(raw)
    require(
        isinstance(candidate, dict) and set(candidate) == CANDIDATE and canonical(candidate) == raw,
        "checkpoint-candidate-shape",
    )
    _check_candidate_identity(config, candidate)
    timestamp(candidate["capturedAt"])
    _check_barrier(candidate)
    return candidate, raw

"""Owner backup-barrier frame discipline; no authority or capture issuer here."""

import json
import os
import re
import secrets
from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Protocol

from backup_types import Json, JsonObject
from deployment_common import canonical, is_sha256, is_uuid, private_path, require, timestamp, utc

FRAME_LIMIT = 16384
LEASE_MINUTES = 30
NONCE_BYTES = 32

FORMAT = "claimcore-backup-barrier-frame-1"
HELD = {
    "format",
    "kind",
    "nonce",
    "leaseId",
    "installationId",
    "lineageId",
    "epoch",
    "writerGeneration",
    "cutoffSequence",
    "cutoffHash",
    "primarySystemId",
    "primaryTimeline",
    "witnessSystemId",
    "witnessTimeline",
    "maintenanceEvidenceSha256",
    "checkedAt",
    "validUntil",
    "cycleRoot",
}
SETTLED = {
    "format",
    "kind",
    "nonce",
    "leaseId",
    "cycleReceiptId",
    "witnessSequence",
    "witnessHash",
    "receiptSha256",
}
FILES = {
    "primaryCiphertextPath",
    "witnessCiphertextPath",
    "checkpointPath",
    "cycleManifestPath",
    "cycleManifestSignaturePath",
}


class BarrierUnknownError(Exception):
    """FINISH may have committed; retain exact private files and seek readback."""


class BarrierController(Protocol):
    """The owner-controlled transport that issues and settles the barrier."""

    def exchange(self, request: bytes) -> bytes:
        """Send one frame and return the controller's answer."""
        ...

    def observe(self, request: bytes) -> bytes:
        """Read the settled receipt back from the independent witness."""
        ...


def frame(value: Json) -> bytes:
    """Serialize one barrier message as a length-bounded canonical frame."""
    data = canonical(value)
    require(len(data) <= FRAME_LIMIT, "barrier-frame-limit")
    return data


def decode(raw: bytes) -> JsonObject:
    """Parse one bounded barrier frame into its JSON object."""
    require(isinstance(raw, bytes) and 0 < len(raw) <= FRAME_LIMIT, "barrier-frame-limit")
    value: JsonObject = json.loads(raw)
    require(isinstance(value, dict) and raw == frame(value), "barrier-frame-canonical")
    return value


def held(value: JsonObject, nonce: str, expected: JsonObject, now: datetime) -> JsonObject:
    """Validate the owner's HELD lease against the nonce, expectation and clock."""
    require(
        set(value) == HELD and value["format"] == FORMAT and value["kind"] == "HELD",
        "barrier-held-shape",
    )
    require(value["nonce"] == nonce and is_uuid(value["leaseId"]), "barrier-held-identity")
    for name in ("installationId", "lineageId", "epoch", "writerGeneration"):
        require(value[name] == expected[name], "barrier-installation")
    require(
        is_uuid(value["installationId"]) and is_uuid(value["lineageId"]),
        "barrier-installation",
    )
    require(
        type(value["epoch"]) is int
        and value["epoch"] > 0
        and type(value["writerGeneration"]) is int
        and value["writerGeneration"] > 0
        and type(value["cutoffSequence"]) is int
        and value["cutoffSequence"] >= 0
        and is_sha256(value["cutoffHash"])
        and is_sha256(value["maintenanceEvidenceSha256"]),
        "barrier-held-evidence",
    )
    for cluster in ("primary", "witness"):
        require(
            isinstance(value[cluster + "SystemId"], str)
            and re.fullmatch(r"[0-9]{1,20}", value[cluster + "SystemId"])
            and type(value[cluster + "Timeline"]) is int
            and value[cluster + "Timeline"] > 0,
            "barrier-cluster",
        )
    require(value["primarySystemId"] != value["witnessSystemId"], "barrier-shared-cluster")
    checked, expires = timestamp(value["checkedAt"]), timestamp(value["validUntil"])
    require(
        checked <= now < expires <= checked + timedelta(minutes=LEASE_MINUTES),
        "barrier-held-expired",
    )
    private_path(value["cycleRoot"], directory=True)
    return value


def sealed(value: JsonObject, nonce: str, lease: JsonObject, kind: str) -> JsonObject:
    """Validate a SEALED or OBSERVED receipt against the lease and nonce."""
    require(
        set(value) == SETTLED and value["format"] == FORMAT and value["kind"] == kind,
        "barrier-receipt-shape",
    )
    require(
        value["nonce"] == nonce and value["leaseId"] == lease["leaseId"],
        "barrier-receipt-identity",
    )
    require(
        is_uuid(value["cycleReceiptId"])
        and type(value["witnessSequence"]) is int
        and value["witnessSequence"] >= lease["cutoffSequence"]
        and is_sha256(value["witnessHash"])
        and is_sha256(value["receiptSha256"]),
        "barrier-receipt-evidence",
    )
    return value


def _verify_capture_paths(
    paths: JsonObject, lease: JsonObject, checkpoint_root: str | os.PathLike[str]
) -> None:
    require(isinstance(paths, dict) and set(paths) == FILES, "barrier-capture-files")
    cycle_root = private_path(lease["cycleRoot"], directory=True)
    checkpoint = private_path(checkpoint_root, directory=True)
    for name, raw in paths.items():
        require(".." not in Path(raw).parts, "barrier-capture-path")
        path = private_path(raw)
        parent = checkpoint if name == "checkpointPath" else cycle_root
        require(path != parent and path.is_relative_to(parent), "barrier-capture-path")
        require(os.lstat(path).st_nlink == 1, "barrier-capture-hardlink")


def _finish_and_observe(
    controller: BarrierController, nonce: str, lease: JsonObject, paths: JsonObject
) -> JsonObject:
    finish = {
        "format": FORMAT,
        "kind": "FINISH",
        "nonce": nonce,
        "leaseId": lease["leaseId"],
        **paths,
    }
    receipt = sealed(decode(controller.exchange(frame(finish))), nonce, lease, "SEALED")
    observation = {
        "format": FORMAT,
        "kind": "OBSERVE",
        "nonce": nonce,
        "cycleReceiptId": receipt["cycleReceiptId"],
        "receiptSha256": receipt["receiptSha256"],
    }
    observed = sealed(decode(controller.observe(frame(observation))), nonce, lease, "OBSERVED")
    require(
        observed["cycleReceiptId"] == receipt["cycleReceiptId"]
        and observed["receiptSha256"] == receipt["receiptSha256"]
        and observed["witnessSequence"] == receipt["witnessSequence"]
        and observed["witnessHash"] == receipt["witnessHash"],
        "barrier-readback-mismatch",
    )
    return receipt


def capture_with_barrier(
    controller: BarrierController,
    capture_action: Callable[[JsonObject], JsonObject],
    expected: JsonObject,
    *,
    checkpoint_root: str | os.PathLike[str],
    now: datetime | None = None,
) -> JsonObject:
    """Transport orchestration only; product must own locks, hashes and readback."""
    current = now or datetime.now(UTC)
    nonce = secrets.token_hex(NONCE_BYTES)
    begin = {
        "format": FORMAT,
        "kind": "BEGIN",
        "nonce": nonce,
        "expiresAt": utc(current + timedelta(minutes=LEASE_MINUTES)),
    }
    response = decode(controller.exchange(frame(begin)))
    received_at = now if now is not None else datetime.now(UTC)
    lease = held(response, nonce, expected, received_at)
    try:
        paths = capture_action(lease)
        _verify_capture_paths(paths, lease, checkpoint_root)
    except Exception:
        controller.exchange(
            frame({"format": FORMAT, "kind": "ABORT", "nonce": nonce, "leaseId": lease["leaseId"]})
        )
        raise
    try:
        receipt = _finish_and_observe(controller, nonce, lease, paths)
    except Exception as error:
        msg = "barrier-finish-uncertain"
        raise BarrierUnknownError(msg) from error
    return {
        "status": "captured-unverified-observed",
        "receiptId": receipt["cycleReceiptId"],
        "retained": False,
        "realDataReady": False,
    }

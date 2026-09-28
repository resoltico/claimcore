"""Owner backup-barrier frame discipline; no authority or capture issuer here."""

import os
import re
import secrets
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

from deployment_common import canonical, private_path, require, timestamp, utc

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


class BarrierUnknown(Exception):
    """FINISH may have committed; retain exact private files and seek readback."""


def _uuid(value):
    try:
        parsed = uuid.UUID(value)
        return parsed.int != 0 and str(parsed) == value
    except (TypeError, ValueError, AttributeError):
        return False


def _sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def frame(value):
    data = canonical(value)
    require(len(data) <= 16384, "barrier-frame-limit")
    return data


def decode(raw):
    require(isinstance(raw, bytes) and 0 < len(raw) <= 16384, "barrier-frame-limit")
    import json

    value = json.loads(raw)
    require(isinstance(value, dict) and raw == frame(value), "barrier-frame-canonical")
    return value


def held(value, nonce, expected, now):
    require(
        set(value) == HELD and value["format"] == FORMAT and value["kind"] == "HELD",
        "barrier-held-shape",
    )
    require(
        value["nonce"] == nonce and _uuid(value["leaseId"]), "barrier-held-identity"
    )
    for name in ("installationId", "lineageId", "epoch", "writerGeneration"):
        require(value[name] == expected[name], "barrier-installation")
    require(
        _uuid(value["installationId"]) and _uuid(value["lineageId"]),
        "barrier-installation",
    )
    require(
        type(value["epoch"]) is int
        and value["epoch"] > 0
        and type(value["writerGeneration"]) is int
        and value["writerGeneration"] > 0
        and type(value["cutoffSequence"]) is int
        and value["cutoffSequence"] >= 0
        and _sha(value["cutoffHash"])
        and _sha(value["maintenanceEvidenceSha256"]),
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
    require(
        value["primarySystemId"] != value["witnessSystemId"], "barrier-shared-cluster"
    )
    checked, expires = timestamp(value["checkedAt"]), timestamp(value["validUntil"])
    require(
        checked <= now < expires <= checked + timedelta(minutes=30),
        "barrier-held-expired",
    )
    private_path(value["cycleRoot"], directory=True)
    return value


def sealed(value, nonce, lease, kind):
    require(
        set(value) == SETTLED and value["format"] == FORMAT and value["kind"] == kind,
        "barrier-receipt-shape",
    )
    require(
        value["nonce"] == nonce and value["leaseId"] == lease["leaseId"],
        "barrier-receipt-identity",
    )
    require(
        _uuid(value["cycleReceiptId"])
        and type(value["witnessSequence"]) is int
        and value["witnessSequence"] >= lease["cutoffSequence"]
        and _sha(value["witnessHash"])
        and _sha(value["receiptSha256"]),
        "barrier-receipt-evidence",
    )
    return value


def capture_with_barrier(
    controller, capture_action, expected, *, checkpoint_root, now=None
):
    """Transport orchestration only; product must own locks, hashes and readback."""
    injected_now = now
    now = now or datetime.now(timezone.utc)
    nonce = secrets.token_hex(32)
    begin = {
        "format": FORMAT,
        "kind": "BEGIN",
        "nonce": nonce,
        "expiresAt": utc(now + timedelta(minutes=30)),
    }
    response = decode(controller.exchange(frame(begin)))
    received_at = (
        injected_now if injected_now is not None else datetime.now(timezone.utc)
    )
    lease = held(response, nonce, expected, received_at)
    finish_started = False
    try:
        paths = capture_action(lease)
        require(
            isinstance(paths, dict) and set(paths) == FILES, "barrier-capture-files"
        )
        cycle_root = private_path(lease["cycleRoot"], directory=True)
        checkpoint = private_path(checkpoint_root, directory=True)
        for name, raw in paths.items():
            require(".." not in Path(raw).parts, "barrier-capture-path")
            path = private_path(raw)
            parent = checkpoint if name == "checkpointPath" else cycle_root
            require(
                path != parent and path.is_relative_to(parent), "barrier-capture-path"
            )
            require(os.lstat(path).st_nlink == 1, "barrier-capture-hardlink")
        finish = {
            "format": FORMAT,
            "kind": "FINISH",
            "nonce": nonce,
            "leaseId": lease["leaseId"],
            **paths,
        }
        finish_started = True
        receipt = sealed(
            decode(controller.exchange(frame(finish))), nonce, lease, "SEALED"
        )
        observation = {
            "format": FORMAT,
            "kind": "OBSERVE",
            "nonce": nonce,
            "cycleReceiptId": receipt["cycleReceiptId"],
            "receiptSha256": receipt["receiptSha256"],
        }
        observed = sealed(
            decode(controller.observe(frame(observation))), nonce, lease, "OBSERVED"
        )
        require(
            observed["cycleReceiptId"] == receipt["cycleReceiptId"]
            and observed["receiptSha256"] == receipt["receiptSha256"]
            and observed["witnessSequence"] == receipt["witnessSequence"]
            and observed["witnessHash"] == receipt["witnessHash"],
            "barrier-readback-mismatch",
        )
        return {
            "status": "captured-unverified-observed",
            "receiptId": receipt["cycleReceiptId"],
            "retained": False,
            "realDataReady": False,
        }
    except Exception as error:
        if not finish_started:
            controller.exchange(
                frame(
                    {
                        "format": FORMAT,
                        "kind": "ABORT",
                        "nonce": nonce,
                        "leaseId": lease["leaseId"],
                    }
                )
            )
        else:
            raise BarrierUnknown("barrier-finish-uncertain") from error
        raise

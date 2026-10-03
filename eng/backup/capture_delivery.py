"""Capture completion knowledge survives process cleanup and response delivery failure."""

import json
import os
import re
import sys
from types import TracebackType

from backup_barrier import BarrierUnknownError
from backup_types import JsonObject
from deployment_common import DeploymentRefusalError
from managed_common import BackupFailureError


class CaptureCompletedError(Exception):
    """A sealed, observed capture is known despite a later failure."""

    def __init__(self, result: JsonObject) -> None:
        """Retain only the opaque receipt and lease identities."""
        super().__init__("capture-completion-response-failed")
        self.lease_id: str = result["leaseId"]
        self.receipt_id: str = result["receiptId"]


def _retire_stdout() -> None:
    """Prevent interpreter finalization from retrying a failed or blocked response stream."""
    try:
        target = sys.stdout.fileno()
        descriptor = os.open(os.devnull, os.O_WRONLY)
        try:
            os.dup2(descriptor, target)
        finally:
            if descriptor != target:
                os.close(descriptor)
    except (AttributeError, OSError, ValueError):
        # Preserve the original delivery failure even if stream retirement is unavailable.
        pass


def write_result(result: JsonObject) -> None:
    """Deliver observed completion, preserving it if stdout or flushing fails."""
    try:
        sys.stdout.write(
            json.dumps(
                {
                    "status": "CAPTURED_UNVERIFIED",
                    "cycleReceiptId": result["receiptId"],
                    "leaseId": result["leaseId"],
                    "realDataReady": False,
                },
                sort_keys=True,
                separators=(",", ":"),
            )
            + "\n"
        )
        sys.stdout.flush()
    except (Exception, KeyboardInterrupt) as error:
        _retire_stdout()
        raise CaptureCompletedError(result) from error


def report_failure(
    _kind: type[BaseException], error: BaseException, _traceback: TracebackType | None
) -> None:
    """Report safe knowledge and identities without exposing private paths or payload."""
    category = "capture-unavailable"
    if isinstance(
        error,
        (DeploymentRefusalError, BackupFailureError, BarrierUnknownError, CaptureCompletedError),
    ):
        candidate = error.args[0] if error.args else None
        if isinstance(candidate, str) and re.fullmatch(r"[a-z0-9-]{1,70}", candidate):
            category = candidate
    value: JsonObject = {
        "status": "REFUSED",
        "reason": category,
        "leaseId": None,
        "realDataReady": False,
    }
    if isinstance(error, BarrierUnknownError):
        value.update(status="CAPTURE_UNCONFIRMED", leaseId=error.lease_id)
    elif isinstance(error, CaptureCompletedError):
        value.update(
            status="CAPTURED_UNVERIFIED", leaseId=error.lease_id, cycleReceiptId=error.receipt_id
        )
    try:
        sys.stderr.write(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n")
        sys.stderr.flush()
    except OSError:
        # A broken diagnostic stream must not expose the original private exception.
        pass

"""Negative controls for bounded, nonpayload backup failure categories."""

import contextlib
import io
import json
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace, TracebackType
from typing import cast
from unittest.mock import Mock, patch

import managed
from backup_barrier import BarrierUnknownError
from backup_types import JsonObject
from capture_delivery import CaptureCompletedError, report_failure, write_result
from deployment_common import DeploymentRefusalError
from managed_common import BackupFailureError


def capture_diagnostics(error: BaseException) -> JsonObject:
    stream = io.StringIO()
    with contextlib.redirect_stderr(stream):
        report_failure(type(error), error, None)
    result: JsonObject = json.loads(stream.getvalue())
    return result


def capture_delivery_checks() -> None:
    lease = "10000000-0000-4000-8000-000000000001"
    receipt = "10000000-0000-4000-8000-000000000002"
    assert capture_diagnostics(BarrierUnknownError(lease)) == {
        "status": "CAPTURE_UNCONFIRMED",
        "reason": "barrier-finish-uncertain",
        "leaseId": lease,
        "realDataReady": False,
    }
    result: JsonObject = {"leaseId": lease, "receiptId": receipt}
    for stage in ("write", "flush"):
        for failure in (BrokenPipeError("private /claimant"), KeyboardInterrupt()):
            output = SimpleNamespace(
                write=Mock(side_effect=failure if stage == "write" else None),
                flush=Mock(side_effect=failure if stage == "flush" else None),
            )
            with patch("capture_delivery.sys.stdout", output):
                try:
                    write_result(result)
                except CaptureCompletedError as completed:
                    observed = capture_diagnostics(completed)
                else:
                    msg = "Capture delivery failure was lost."
                    raise AssertionError(msg)
            assert observed == {
                "status": "CAPTURED_UNVERIFIED",
                "reason": "capture-completion-response-failed",
                "leaseId": lease,
                "cycleReceiptId": receipt,
                "realDataReady": False,
            }


def capture_closed_pipe() -> None:
    source = (
        "import sys; from capture_delivery import write_result, report_failure; "
        "sys.excepthook=report_failure; sys.stdin.buffer.read(1); "
        "write_result({'leaseId':'10000000-0000-4000-8000-000000000001',"
        "'receiptId':'10000000-0000-4000-8000-000000000002'})"
    )
    with subprocess.Popen(
        [sys.executable, "-B", "-c", source],
        cwd=Path(__file__).resolve().parent,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    ) as child:
        assert child.stdin is not None and child.stdout is not None and child.stderr is not None
        child.stdout.close()
        child.stdin.write(b"x")
        child.stdin.close()
        diagnostic = child.stderr.read()
        assert child.wait(timeout=10) != 0
    assert json.loads(diagnostic) == {
        "status": "CAPTURED_UNVERIFIED",
        "reason": "capture-completion-response-failed",
        "leaseId": "10000000-0000-4000-8000-000000000001",
        "cycleReceiptId": "10000000-0000-4000-8000-000000000002",
        "realDataReady": False,
    }


def main() -> None:
    assert managed.failure_category(BackupFailureError("manifest-invalid")) == "manifest-invalid"
    assert (
        managed.failure_category(DeploymentRefusalError("signer-public-key-invalid"))
        == "signer-public-key-invalid"
    )
    assert managed.failure_category(DeploymentRefusalError("private /claimant")) == "backup-refusal"
    assert managed.failure_category(BackupFailureError("private /claimant")) == "backup-refusal"
    assert (
        managed.failure_category(PermissionError("private /claimant")) == "backup-permission-denied"
    )
    assert (
        managed.failure_category(FileNotFoundError("private /claimant"))
        == "backup-file-unavailable"
    )
    assert (
        managed.failure_category(subprocess.TimeoutExpired("private", 1))
        == "backup-subprocess-timeout"
    )
    assert managed.failure_category(KeyError("private /claimant")) == "backup-data-invalid"
    assert (
        managed.failure_category(RuntimeError("private /claimant")) == "unexpected-backup-failure"
    )
    frame = SimpleNamespace(f_code=SimpleNamespace(co_filename=managed.__file__))
    traceback = SimpleNamespace(tb_frame=frame, tb_lineno=1420, tb_next=None)
    stream = io.StringIO()
    with contextlib.redirect_stderr(stream):
        managed.report_failure(
            PermissionError,
            PermissionError("private /claimant"),
            cast("TracebackType", traceback),
        )
    assert json.loads(stream.getvalue()) == {
        "status": "quarantined",
        "reason": "backup-permission-denied-line-1420",
    }
    capture_delivery_checks()
    capture_closed_pipe()


if __name__ == "__main__":
    main()

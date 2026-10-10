"""Negative controls for bounded, nonpayload backup failure categories."""

import contextlib
import io
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace, TracebackType
from typing import cast
from unittest.mock import Mock, patch

import managed
import managed_copy_boot
from backup_barrier import BarrierUnknownError
from backup_types import JsonObject
from capture_delivery import CaptureCompletedError, report_failure, write_result
from deployment_common import DeploymentRefusalError
from managed_common import BackupFailureError
from managed_copy_verification_io import VerificationFailureError, write_new


def copy_cleanup_checks() -> None:
    identifier = "a" * 64
    for body_fails, cleanup_fails in ((True, True), (False, True), (True, False), (False, False)):
        with tempfile.TemporaryDirectory(dir=Path(tempfile.gettempdir()).resolve()) as directory:
            body_error = VerificationFailureError("RECOVERED_IDENTITY_DIVERGED")
            config: JsonObject = {
                "privateScratchRoot": directory,
                "databaseOwnerRole": "synthetic",
                "databaseName": "synthetic_test",
            }
            result = SimpleNamespace(returncode=1 if cleanup_fails else 0)
            with (
                patch.object(managed_copy_boot, "_image", return_value="synthetic"),
                patch.object(managed_copy_boot, "_start", return_value=identifier),
                patch.object(managed_copy_boot, "_load"),
                patch.object(
                    managed_copy_boot,
                    "_check_identity",
                    side_effect=body_error if body_fails else None,
                ),
                patch.object(managed_copy_boot, "_row_count", return_value=7),
                patch.object(managed_copy_boot, "tool", return_value="docker"),
                patch("managed_copy_boot.subprocess.run", return_value=result) as stop,
            ):
                observed = None
                try:
                    assert managed_copy_boot.boot_identity(config, Path(directory)) == 7
                except VerificationFailureError as error:
                    observed = error
                assert (observed is not None) == (body_fails or cleanup_fails)
                if body_fails:
                    assert observed is body_error
                assert stop.call_args.kwargs["timeout"] == 30
            records = list(Path(directory).glob("copy-cleanup-*.json"))
            assert len(records) == int(cleanup_fails)
            if cleanup_fails:
                record = json.loads(records[0].read_text())
                assert record["containerId"] == identifier and record["settlement"] == "UNKNOWN"
                assert record["kind"] == "refused"
                assert records[0].stat().st_mode & 0o777 == 0o600


def copy_cleanup_timeout() -> None:
    identifier = "b" * 64
    original = RuntimeError("PRIVATE-BODY")
    timeout = subprocess.TimeoutExpired("PRIVATE-COMMAND", 30, stderr=b"PRIVATE-PAYLOAD")
    for body_fails, retention_fails in ((True, False), (True, True), (False, False)):
        with tempfile.TemporaryDirectory(dir=Path(tempfile.gettempdir()).resolve()) as directory:
            config: JsonObject = {
                "privateScratchRoot": directory,
                "databaseOwnerRole": "synthetic",
                "databaseName": "synthetic_test",
            }
            with (
                patch.object(managed_copy_boot, "_image", return_value="synthetic"),
                patch.object(managed_copy_boot, "_start", return_value=identifier),
                patch.object(managed_copy_boot, "_load"),
                patch.object(
                    managed_copy_boot,
                    "_check_identity",
                    side_effect=original if body_fails else None,
                ),
                patch.object(managed_copy_boot, "_row_count", return_value=7),
                patch.object(managed_copy_boot, "tool", return_value="docker"),
                patch("managed_copy_boot.subprocess.run", side_effect=timeout) as stop,
                patch.object(
                    managed_copy_boot,
                    "write_new",
                    wraps=write_new,
                    side_effect=OSError("PRIVATE-IO") if retention_fails else None,
                ),
            ):
                observed = None
                try:
                    managed_copy_boot.boot_identity(config, Path(directory))
                except (RuntimeError, subprocess.TimeoutExpired) as error:
                    observed = error
                assert observed is (original if body_fails else timeout)
                assert stop.call_args.kwargs["timeout"] == 30
            assert observed is not None and "PRIVATE" not in " ".join(observed.__notes__)
            if retention_fails:
                assert original.__notes__[-1] == "copy-cleanup-record=unavailable"
                assert not list(Path(directory).glob("copy-cleanup-*.json"))
            else:
                record = next(Path(directory).glob("copy-cleanup-*.json")).read_text()
                assert json.loads(record)["kind"] == "timeout" and "PRIVATE" not in record


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
    copy_cleanup_checks()
    copy_cleanup_timeout()


if __name__ == "__main__":
    main()

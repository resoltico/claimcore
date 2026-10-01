"""Negative controls for bounded, nonpayload backup failure categories."""

import contextlib
import io
import json
import subprocess
from types import SimpleNamespace, TracebackType
from typing import cast

import managed
from managed_common import BackupFailureError


def main() -> None:
    assert managed.failure_category(BackupFailureError("manifest-invalid")) == "manifest-invalid"
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


if __name__ == "__main__":
    main()

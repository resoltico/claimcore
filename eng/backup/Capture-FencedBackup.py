#!/usr/bin/env python3
"""Owner-only encrypted pair capture under a fixed Database authority lease."""

import argparse
import json
import os
import sys
from types import TracebackType

sys.dont_write_bytecode = True
from backup_barrier import BarrierUnknownError, capture_with_barrier
from backup_barrier_process import DatabaseBarrierController
from deployment_common import DeploymentRefusalError
from fenced_backup_identity import expected_identity, require_test_databases
from managed_capture import capture
from managed_common import BackupFailureError, require
from managed_config import configuration

MIN_PYTHON = (3, 12)


def main() -> None:
    """Capture the owner-labelled encrypted pair and write its typed status."""
    os.umask(0o077)
    require(sys.version_info >= MIN_PYTHON, "python-3.12-required")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("--synthetic-only", action="store_true")
    args = parser.parse_args()
    config = configuration(args.config)
    if config["checkpointSignerMode"] == "LOCAL_SYNTHETIC":
        require(args.synthetic_only, "local-checkpoint-signer-is-synthetic-only")
        require_test_databases(config)
    expected = expected_identity(config)
    with DatabaseBarrierController(config["archiveRoot"], config["checkpointRoot"]) as owner:
        result = capture_with_barrier(
            owner,
            lambda held: capture(config, held),
            expected,
            checkpoint_root=config["checkpointRoot"],
        )
    sys.stdout.write(
        json.dumps(
            {
                "status": "CAPTURED_UNVERIFIED",
                "cycleReceiptId": result["receiptId"],
                "realDataReady": False,
            },
            sort_keys=True,
            separators=(",", ":"),
        )
        + "\n"
    )


def safe_error(
    _kind: type[BaseException], error: BaseException, _traceback: TracebackType | None
) -> None:
    """Report only a safe typed reason for an uncaught exception."""
    if isinstance(error, (DeploymentRefusalError, BackupFailureError, BarrierUnknownError)):
        category = error.args[0] if error.args else "capture-unavailable"
    else:
        category = "capture-unavailable"
    sys.stderr.write(
        json.dumps(
            {
                "status": "CAPTURE_UNCONFIRMED"
                if isinstance(error, BarrierUnknownError)
                else "REFUSED",
                "reason": category,
                "realDataReady": False,
            },
            sort_keys=True,
            separators=(",", ":"),
        )
        + "\n"
    )


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()

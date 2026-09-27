#!/usr/bin/env python3
"""Owner-only encrypted pair capture under a fixed Database authority lease."""

import json
import os
import sys

sys.dont_write_bytecode = True
import managed
from backup_barrier import BarrierUnknown, capture_with_barrier
from backup_barrier_process import DatabaseBarrierController
from deployment_common import DeploymentRefusal
from fenced_backup_identity import expected_identity, require_test_databases


def main():
    os.umask(0o077)
    managed.require(sys.version_info >= (3, 12), "python-3.12-required")
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("--synthetic-only", action="store_true")
    args = parser.parse_args()
    config = managed.configuration(args.config)
    if config["checkpointSignerMode"] == "LOCAL_SYNTHETIC":
        managed.require(
            args.synthetic_only, "local-checkpoint-signer-is-synthetic-only"
        )
        require_test_databases(config)
    expected = expected_identity(config)
    with DatabaseBarrierController(
        config["archiveRoot"], config["checkpointRoot"]
    ) as owner:
        result = capture_with_barrier(
            owner,
            lambda held: managed.capture(config, held),
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


def safe_error(_kind, error, _traceback):
    if isinstance(error, (DeploymentRefusal, managed.BackupFailure, BarrierUnknown)):
        category = error.args[0] if error.args else "capture-unavailable"
    else:
        category = "capture-unavailable"
    sys.stderr.write(
        json.dumps(
            {
                "status": "CAPTURE_UNCONFIRMED"
                if isinstance(error, BarrierUnknown)
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

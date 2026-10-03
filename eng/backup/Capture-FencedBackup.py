#!/usr/bin/env python3
"""Owner-only encrypted pair capture under a fixed Database authority lease."""

import argparse
import os
import sys
from typing import TYPE_CHECKING

sys.dont_write_bytecode = True
from backup_barrier import capture_with_barrier
from backup_barrier_process import DatabaseBarrierController
from capture_delivery import CaptureCompletedError, report_failure, write_result
from fenced_backup_identity import expected_identity, require_test_databases
from managed_capture import capture
from managed_common import require
from managed_config import configuration

if TYPE_CHECKING:
    from backup_types import JsonObject

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
    result: JsonObject | None = None
    try:
        with DatabaseBarrierController(config["archiveRoot"], config["checkpointRoot"]) as owner:
            result = capture_with_barrier(
                owner,
                lambda held: capture(config, held),
                expected,
                checkpoint_root=config["checkpointRoot"],
            )
    except (Exception, KeyboardInterrupt) as error:
        if result is not None:
            raise CaptureCompletedError(result) from error
        raise
    write_result(result)


if __name__ == "__main__":
    sys.excepthook = report_failure
    main()

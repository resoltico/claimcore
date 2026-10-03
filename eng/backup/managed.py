#!/usr/bin/env python3
"""Owner-only encrypted PostgreSQL backup and quarantine qualification.

This program never promotes a restored cluster. Its output is a private,
machine-readable qualification input, not authority to admit case work.
"""

import argparse
import json
import os
import re
import subprocess
import sys
import uuid
from pathlib import Path
from types import TracebackType

sys.dont_write_bytecode = True
import promotion
from backup_types import JsonObject
from deployment_common import DeploymentRefusalError
from managed_common import CLUSTERS, BackupFailureError, private_path, require, tool
from managed_config import configuration
from managed_inspect import inspect
from managed_wal import attest_wal
from managed_wal_archive import archive_wal

MIN_PYTHON = (3, 12)
MIN_APPROVERS = 2
MAX_LINE = 9999
CATEGORY = r"[a-z0-9-]{1,70}"


def _review_promotion(config: JsonObject, args: argparse.Namespace) -> JsonObject:
    require(
        "fenceVerificationKey" in config and "approverKeys" in config,
        "promotion-key-registry-missing",
    )
    require(
        isinstance(config["approverKeys"], dict) and len(config["approverKeys"]) >= MIN_APPROVERS,
        "promotion-approver-registry",
    )
    for key_id, entry in config["approverKeys"].items():
        uuid.UUID(key_id)
        uuid.UUID(entry["actorId"])
        entry["publicKey"] = private_path(entry["publicKey"])
    config["fenceVerificationKey"] = private_path(config["fenceVerificationKey"])
    return promotion.review(
        config,
        args.report,
        args.fence_report,
        args.approval,
        promotion.ReviewTools(private_path, tool("openssl")),
    )


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    sub = parser.add_subparsers(dest="command", required=True)
    verify = sub.add_parser("inspect")
    verify.add_argument("cycle_id")
    verify.add_argument("--restore-verifier", required=True)
    wal = sub.add_parser("archive-wal")
    wal.add_argument("cluster", choices=CLUSTERS)
    wal.add_argument("source")
    wal.add_argument("segment")
    attest = sub.add_parser("attest-wal")
    attest.add_argument("cluster", choices=CLUSTERS)
    attest.add_argument("segment")
    review = sub.add_parser("review-promotion")
    review.add_argument("--report", required=True)
    review.add_argument("--fence-report", required=True)
    review.add_argument("--approval", action="append", required=True)
    return parser


def main() -> None:
    """Run the selected backup, WAL or review command."""
    os.umask(0o077)
    require(sys.version_info >= MIN_PYTHON, "python-3.12-required")
    args = _parser().parse_args()
    config = configuration(args.config, archiver=args.command == "archive-wal")
    if args.command == "inspect":
        inspect(config, args.cycle_id, args.restore_verifier)
    elif args.command == "attest-wal":
        attest_wal(config, args.cluster, args.segment)
    elif args.command == "review-promotion":
        sys.stdout.write(json.dumps(_review_promotion(config, args)) + "\n")
    else:
        archive_wal(config, args.cluster, args.source, args.segment)


FAILURE_CATEGORIES: tuple[
    tuple[type[BaseException] | tuple[type[BaseException], ...], str], ...
] = (
    (subprocess.TimeoutExpired, "backup-subprocess-timeout"),
    (subprocess.CalledProcessError, "backup-subprocess-failed"),
    (PermissionError, "backup-permission-denied"),
    (FileNotFoundError, "backup-file-unavailable"),
    (OSError, "backup-os-error"),
    ((json.JSONDecodeError, KeyError, TypeError, ValueError), "backup-data-invalid"),
)


def failure_category(error: BaseException) -> str:
    """Map an exception to a bounded category that never carries payload."""
    if isinstance(
        error, BackupFailureError | promotion.ReviewFailureError | DeploymentRefusalError
    ):
        candidate = error.args[0] if error.args else None
        if isinstance(candidate, str) and re.fullmatch(CATEGORY, candidate):
            return candidate
        return "backup-refusal"
    for kinds, category in FAILURE_CATEGORIES:
        if isinstance(error, kinds):
            return category
    return "unexpected-backup-failure"


def _code_line(traceback: TracebackType | None) -> int | None:
    directory = Path(__file__).resolve().parent
    line = None
    while traceback is not None:
        if Path(traceback.tb_frame.f_code.co_filename).resolve().parent == directory:
            line = traceback.tb_lineno
        traceback = traceback.tb_next
    return line if isinstance(line, int) and 1 <= line <= MAX_LINE else None


def report_failure(
    _exception_type: type[BaseException], error: BaseException, traceback: TracebackType | None
) -> None:
    """Report a quarantined failure with its bounded category on stderr."""
    category = failure_category(error)
    if category == "backup-permission-denied":
        line = _code_line(traceback)
        if line is not None:
            category += f"-line-{line}"
    sys.stderr.write(json.dumps({"status": "quarantined", "reason": category}) + "\n")


if __name__ == "__main__":
    sys.excepthook = report_failure
    main()

#!/usr/bin/env python3
"""Capture exact post-settlement WAL in disposable restored PostgreSQL clusters."""

import argparse
import json
import os
import sys
from pathlib import Path
from types import TracebackType

sys.dont_write_bytecode = True
from fenced_tail_capture import CaptureRefusalError, capture, private_file, require

MIN_PYTHON = (3, 12)
INPUT_LIMIT = 32768


def main() -> None:
    """Capture the fenced recovery tail and write its typed status."""
    os.umask(0o077)
    require(sys.version_info >= MIN_PYTHON, "python-3.12-required")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    config_path = private_file(args.config)
    require(config_path.stat().st_size <= INPUT_LIMIT, "tail-input-limit")
    config = json.loads(config_path.read_bytes())
    root = Path(__file__).resolve().parents[2]
    pinned = json.loads((root / "db/postgresql-baseline.json").read_bytes())["containerImage"]
    capture(config, pinned)
    sys.stdout.write("fenced-tail-capture=synthetic-only\n")


def safe_error(
    _kind: type[BaseException], error: BaseException, _traceback: TracebackType | None
) -> None:
    """Report only a safe typed reason for an uncaught exception."""
    reason = error.args[0] if isinstance(error, CaptureRefusalError) else "tail-capture-refused"
    sys.stderr.write(
        json.dumps({"status": "refused", "reason": reason, "realDataReady": False}) + "\n"
    )


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()

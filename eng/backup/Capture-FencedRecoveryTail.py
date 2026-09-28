#!/usr/bin/env python3
"""Capture exact post-settlement WAL in disposable restored PostgreSQL clusters."""

import argparse
import json
import os
import sys
from pathlib import Path

sys.dont_write_bytecode = True
from fenced_tail_capture import CaptureRefusal, capture, private_file, require


def main():
    os.umask(0o077)
    require(sys.version_info >= (3, 12), "python-3.12-required")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    config_path = private_file(args.config)
    require(config_path.stat().st_size <= 32768, "tail-input-limit")
    config = json.loads(config_path.read_bytes())
    root = Path(__file__).resolve().parents[2]
    pinned = json.loads((root / "db/postgresql-baseline.json").read_bytes())[
        "containerImage"
    ]
    capture(config, pinned)
    sys.stdout.write("fenced-tail-capture=synthetic-only\n")


def safe_error(_kind, error, _traceback):
    reason = (
        error.args[0] if isinstance(error, CaptureRefusal) else "tail-capture-refused"
    )
    sys.stderr.write(
        json.dumps({"status": "refused", "reason": reason, "realDataReady": False})
        + "\n"
    )


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()

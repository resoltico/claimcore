#!/usr/bin/env python3
"""Fixed private CHECKPOINT signer process for synthetic/local custody drills."""

import argparse
import json
import os
import signal
import sys
from types import FrameType, TracebackType

sys.dont_write_bytecode = True
from checkpoint_signer_policy import configuration
from checkpoint_signer_service import serve, serve_once
from deployment_common import DeploymentRefusalError

REMOTE_CONFIG = "/var/lib/claimcore/checkpoint-signer/config.json"
MIN_PYTHON = (3, 12)


def _terminate(_number: int, _frame: FrameType | None) -> None:
    sys.exit(0)


def main() -> None:
    """Serve one CHECKPOINT signing exchange on standard input and output."""
    os.umask(0o077)
    if sys.version_info < MIN_PYTHON:
        msg = "python-3.12-required"
        raise DeploymentRefusalError(msg)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config")
    parser.add_argument("--stdio-sign", action="store_true")
    args = parser.parse_args()
    if args.stdio_sign:
        if args.config is not None:
            msg = "checkpoint-signer-command"
            raise DeploymentRefusalError(msg)
        serve_once(configuration(REMOTE_CONFIG, "REMOTE_STDIO"))
    else:
        if args.config is None:
            msg = "checkpoint-signer-config-missing"
            raise DeploymentRefusalError(msg)
        config = configuration(args.config, "LOCAL_SOCKET")
        signal.signal(signal.SIGTERM, _terminate)
        serve(config)


def safe_error(
    _kind: type[BaseException], error: BaseException, _traceback: TracebackType | None
) -> None:
    """Report only a safe typed reason for an uncaught exception."""
    category = (
        error.args[0]
        if isinstance(error, DeploymentRefusalError)
        else "checkpoint-signer-unavailable-" + type(error).__name__.lower()
    )
    sys.stderr.write(json.dumps({"status": "REFUSED", "reason": category}) + "\n")


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()

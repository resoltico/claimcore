#!/usr/bin/env python3
"""Fixed private CHECKPOINT signer process for synthetic/local custody drills."""

import argparse
import json
import os
import signal
import sys

sys.dont_write_bytecode = True
from checkpoint_signer_policy import configuration
from checkpoint_signer_service import serve, serve_once
from deployment_common import DeploymentRefusal

REMOTE_CONFIG = "/var/lib/claimcore/checkpoint-signer/config.json"


def main():
    os.umask(0o077)
    if sys.version_info < (3, 12):
        raise DeploymentRefusal("python-3.12-required")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config")
    parser.add_argument("--stdio-sign", action="store_true")
    args = parser.parse_args()
    if args.stdio_sign:
        if args.config is not None:
            raise DeploymentRefusal("checkpoint-signer-command")
        serve_once(configuration(REMOTE_CONFIG, "REMOTE_STDIO"))
    else:
        if args.config is None:
            raise DeploymentRefusal("checkpoint-signer-config-missing")
        config = configuration(args.config, "LOCAL_SOCKET")
        signal.signal(signal.SIGTERM, lambda _number, _frame: sys.exit(0))
        serve(config)


def safe_error(_kind, error, _traceback):
    category = (
        error.args[0]
        if isinstance(error, DeploymentRefusal)
        else "checkpoint-signer-unavailable-" + type(error).__name__.lower()
    )
    sys.stderr.write(json.dumps({"status": "REFUSED", "reason": category}) + "\n")


if __name__ == "__main__":
    sys.excepthook = safe_error
    main()

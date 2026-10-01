#!/usr/bin/env python3
"""Disposable labelled-cluster entry point for the unfenced primitive drill only."""

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

sys.dont_write_bytecode = True
import managed
from managed_basebackup import postgres_control
from managed_capture import capture
from managed_common import require
from managed_config import configuration
from synthetic_container_identity import system_id


def _container(identity: str, image: str) -> str:
    require(
        isinstance(identity, str) and re.fullmatch(r"[0-9a-f]{64}", identity),
        "synthetic-container-id",
    )
    result = subprocess.run(
        [
            "docker",
            "inspect",
            "--format",
            '{{.Config.Image}}|{{index .Config.Labels "claimcore.backup-test"}}',
            identity,
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=10,
        check=False,
    )
    require(
        result.returncode == 0 and len(result.stdout) <= 512,
        "synthetic-container-unavailable",
    )
    parts = result.stdout.decode("ascii", "strict").strip().split("|")
    require(
        len(parts) == 2 and parts[0] == image and re.fullmatch(r"[0-9]{1,12}", parts[1]),
        "synthetic-container-label",
    )
    return parts[1]


def main() -> None:
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("--primary-container", required=True)
    parser.add_argument("--witness-container", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    image = json.loads((root / "db/postgresql-baseline.json").read_bytes())["containerImage"]
    primary_label = _container(args.primary_container, image)
    witness_label = _container(args.witness_container, image)
    require(
        args.primary_container != args.witness_container and primary_label == witness_label,
        "synthetic-container-pair",
    )
    config = configuration(args.config)
    require(config["checkpointSignerMode"] == "LOCAL_SYNTHETIC", "synthetic-signer-mode")
    primary_system, _, _ = postgres_control(config, "primary")
    witness_system, _, _ = postgres_control(config, "witness")
    require(
        primary_system == system_id(args.primary_container)
        and witness_system == system_id(args.witness_container),
        "synthetic-connection-target",
    )
    capture(config)


if __name__ == "__main__":
    sys.excepthook = managed.report_failure
    main()

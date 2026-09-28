#!/usr/bin/env python3
"""Exact-labelled synthetic owner capture with optional pre-FINISH byte tamper."""

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

sys.dont_write_bytecode = True
import managed
from backup_barrier import BarrierUnknown, capture_with_barrier
from backup_barrier_process import DatabaseBarrierController
from deployment_common import DeploymentRefusal
from fenced_backup_identity import expected_identity
from synthetic_container_identity import system_id


def labelled(identity, image):
    managed.require(
        isinstance(identity, str) and re.fullmatch(r"[0-9a-f]{64}", identity),
        "synthetic-container-id",
    )
    result = subprocess.run(
        [
            "docker",
            "inspect",
            "--format",
            '{{.Config.Image}}|{{index .Config.Labels "org.claimcore.test-run"}}',
            identity,
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=10,
        check=False,
    )
    managed.require(
        result.returncode == 0 and len(result.stdout) <= 512,
        "synthetic-container-unavailable",
    )
    parts = result.stdout.decode("ascii", "strict").strip().split("|")
    managed.require(
        len(parts) == 2
        and parts[0] == image
        and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,99}", parts[1]),
        "synthetic-container-label",
    )
    return parts[1]


def database_name(config, cluster):
    result = subprocess.run(
        [
            managed.tool("psql", "psql (PostgreSQL) 18.6"),
            "-XAtq",
            "-w",
            "-c",
            "SELECT current_database()",
        ],
        env=managed.pg_env(config[cluster]["metadataService"]),
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=10,
        check=False,
    )
    managed.require(
        result.returncode == 0 and len(result.stdout) <= 128,
        "synthetic-database-unavailable",
    )
    name = result.stdout.decode("ascii", "strict").strip()
    managed.require(name.endswith("_test"), "synthetic-database-name")


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("--primary-container", required=True)
    parser.add_argument("--witness-container", required=True)
    parser.add_argument("--tamper-primary", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    image = json.loads((root / "db/postgresql-baseline.json").read_bytes())[
        "containerImage"
    ]
    labelled(args.primary_container, image)
    labelled(args.witness_container, image)
    managed.require(
        args.primary_container != args.witness_container,
        "synthetic-container-pair",
    )
    config = managed.configuration(args.config)
    managed.require(
        config["checkpointSignerMode"] == "LOCAL_SYNTHETIC", "synthetic-signer-mode"
    )
    for cluster, identity in (
        ("primary", args.primary_container),
        ("witness", args.witness_container),
    ):
        database_name(config, cluster)
        current_system, _, _ = managed.postgres_control(config, cluster)
        managed.require(
            current_system == system_id(identity), "synthetic-connection-target"
        )
    expected_installation = os.environ.get("CLAIMCORE_TEST_EXPECTED_INSTALLATION_ID")
    managed.require(
        expected_installation is not None, "synthetic-installation-unavailable"
    )
    for cluster in ("primary", "witness"):
        observed = managed.metadata(config, cluster)[0]
        managed.require(
            observed == expected_installation,
            "synthetic-" + cluster + "-installation-mismatch",
        )
    expected = expected_identity(config)

    def action(held):
        files = managed.capture(config, held)
        if args.tamper_primary:
            with Path(files["primaryCiphertextPath"]).open("ab") as ciphertext:
                ciphertext.write(b"changed-after-signed-manifest")
                ciphertext.flush()
                os.fsync(ciphertext.fileno())
        return files

    with DatabaseBarrierController(
        config["archiveRoot"], config["checkpointRoot"]
    ) as owner:
        result = capture_with_barrier(
            owner, action, expected, checkpoint_root=config["checkpointRoot"]
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
        reason = error.args[0] if error.args else "capture-unavailable"
    else:
        reason = "capture-unavailable"
    sys.stderr.write(
        json.dumps(
            {
                "status": "CAPTURE_UNCONFIRMED"
                if isinstance(error, BarrierUnknown)
                else "REFUSED",
                "reason": reason,
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

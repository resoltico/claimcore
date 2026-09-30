#!/usr/bin/env python3
"""Exact-labelled synthetic owner capture with optional pre-FINISH byte tamper."""

import argparse
import json
import os
import re
import subprocess
import sys
from collections.abc import Callable
from pathlib import Path
from types import TracebackType

sys.dont_write_bytecode = True
from backup_barrier import BarrierUnknownError, capture_with_barrier
from backup_barrier_process import DatabaseBarrierController
from backup_types import JsonObject
from deployment_common import DeploymentRefusalError
from fenced_backup_identity import expected_identity
from managed_basebackup import postgres_control
from managed_capture import capture
from managed_common import BackupFailureError, metadata, pg_env, require, tool
from managed_config import configuration
from synthetic_container_identity import system_id


def labelled(identity: str, image: str) -> str:
    require(
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
    require(
        result.returncode == 0 and len(result.stdout) <= 512,
        "synthetic-container-unavailable",
    )
    parts = result.stdout.decode("ascii", "strict").strip().split("|")
    require(
        len(parts) == 2
        and parts[0] == image
        and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,99}", parts[1]),
        "synthetic-container-label",
    )
    return parts[1]


def database_name(config: JsonObject, cluster: str) -> None:
    result = subprocess.run(
        [
            tool("psql", "psql (PostgreSQL) 18.6"),
            "-XAtq",
            "-w",
            "-c",
            "SELECT current_database()",
        ],
        env=pg_env(config[cluster]["metadataService"]),
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        timeout=10,
        check=False,
    )
    require(
        result.returncode == 0 and len(result.stdout) <= 128,
        "synthetic-database-unavailable",
    )
    name = result.stdout.decode("ascii", "strict").strip()
    require(name.endswith("_test"), "synthetic-database-name")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("--primary-container", required=True)
    parser.add_argument("--witness-container", required=True)
    parser.add_argument("--tamper-primary", action="store_true")
    return parser.parse_args()


def verify_synthetic_pair(args: argparse.Namespace, config: JsonObject) -> None:
    root = Path(__file__).resolve().parents[2]
    image = json.loads((root / "db/postgresql-baseline.json").read_bytes())["containerImage"]
    labelled(args.primary_container, image)
    labelled(args.witness_container, image)
    require(args.primary_container != args.witness_container, "synthetic-container-pair")
    require(config["checkpointSignerMode"] == "LOCAL_SYNTHETIC", "synthetic-signer-mode")
    for cluster, identity in (
        ("primary", args.primary_container),
        ("witness", args.witness_container),
    ):
        database_name(config, cluster)
        current_system, _, _ = postgres_control(config, cluster)
        require(current_system == system_id(identity), "synthetic-connection-target")
    expected_installation = os.environ.get("CLAIMCORE_TEST_EXPECTED_INSTALLATION_ID")
    require(expected_installation is not None, "synthetic-installation-unavailable")
    for cluster in ("primary", "witness"):
        observed = metadata(config, cluster)[0]
        require(
            observed == expected_installation, "synthetic-" + cluster + "-installation-mismatch"
        )


def capture_action(
    config: JsonObject, *, tamper_primary: bool
) -> Callable[[JsonObject], JsonObject]:
    def action(held: JsonObject) -> JsonObject:
        files = capture(config, held)
        if tamper_primary:
            with Path(files["primaryCiphertextPath"]).open("ab") as ciphertext:
                ciphertext.write(b"changed-after-signed-manifest")
                ciphertext.flush()
                os.fsync(ciphertext.fileno())
        return files

    return action


def main() -> None:
    os.umask(0o077)
    args = parse_arguments()
    config = configuration(args.config)
    verify_synthetic_pair(args, config)
    expected = expected_identity(config)
    action = capture_action(config, tamper_primary=args.tamper_primary)
    with DatabaseBarrierController(config["archiveRoot"], config["checkpointRoot"]) as owner:
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


def safe_error(
    _kind: type[BaseException], error: BaseException, _traceback: TracebackType | None
) -> None:
    if isinstance(error, (DeploymentRefusalError, BackupFailureError, BarrierUnknownError)):
        reason = error.args[0] if error.args else "capture-unavailable"
    else:
        reason = "capture-unavailable"
    sys.stderr.write(
        json.dumps(
            {
                "status": "CAPTURE_UNCONFIRMED"
                if isinstance(error, BarrierUnknownError)
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

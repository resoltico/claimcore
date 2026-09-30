#!/usr/bin/env python3
"""Synthetic negatives only: no local machine is admitted as an independent deployment."""

import sys
import tempfile
from pathlib import Path
from unittest.mock import patch

sys.dont_write_bytecode = True
from backup_test_support import refuses
from deployment_admission_fixture import build_deployment
from deployment_admission_inventory import location_inventory_negative
from deployment_admission_probe import (
    local_probe_negative,
    private_path_negative,
    ssh_pin_negative,
)
from deployment_admission_product import product_report_negative, publication_recheck_synthetic
from deployment_admission_trust import trust_negatives
from deployment_probe import machine_id


def machine_id_negative() -> None:
    if not sys.platform.startswith("linux"):
        return
    for failure in (OSError("synthetic"), UnicodeError("synthetic")):
        with patch("deployment_probe.Path.read_text", side_effect=failure):
            refuses(machine_id, "machine-id-unavailable")


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="claimcore-deploy-test-") as temporary:
        root = Path(temporary).resolve()
        root.chmod(0o700)
        deployment = build_deployment(root)
        trust_negatives(deployment)
        product_report_negative(root, deployment)
        publication_recheck_synthetic(root)
        probe_root = root / "private-probe"
        probe_root.mkdir(mode=0o700)
        local_probe_negative(probe_root)
        machine_id_negative()
        ssh_pin_negative(root, deployment)
        private_path_negative(root)
        location_inventory_negative(root)
    sys.stdout.write(
        "Deployment gate refused same-host, missing/forged/stale evidence "
        "and absent production authority.\n"
    )


if __name__ == "__main__":
    main()

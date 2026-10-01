"""One independently held role signs only after its own local source observations pass."""

import argparse
import base64
import sys

from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_inspection import (
    inspect_archive,
    inspect_checkpoint,
    inspect_restore,
)
from backup_health_source_io import create_private, read_private, role_public_key
from deployment_common import refuse, sign
from health_cli import POLICY_LIMIT, SIGNATURE_LIMIT, SOURCE_LIMIT, run_refusing

INSPECTIONS = {
    "archive": inspect_archive,
    "checkpoint": inspect_checkpoint,
    "test-restore": inspect_restore,
}


def _sign(options: argparse.Namespace) -> str:
    policy = parse_policy(read_private(options.policy, POLICY_LIMIT))
    source = parse_source(read_private(options.source, SOURCE_LIMIT), policy)
    role = next(item for item in policy["roles"] if item["role"] == options.role)
    expected_key = base64.b64decode(role["publicKeyBase64"], validate=True)
    if role_public_key(options.private_key) != expected_key:
        refuse("health-role-key-mismatch")
    INSPECTIONS[options.role](source, policy)
    signed = sign(source, options.private_key)
    signature = base64.b64decode(signed["signatureBase64"], validate=True)
    create_private(options.signature_output, signature, SIGNATURE_LIMIT)
    role_name: str = options.role
    return "backup-health-role=SIGNED_" + role_name.upper().replace("-", "_")


def main() -> int:
    """Sign one role's observations after its local source checks pass."""
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--role", required=True, choices=tuple(INSPECTIONS))
    parser.add_argument("--policy", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--private-key", required=True)
    parser.add_argument("--signature-output", required=True)
    options = parser.parse_args()
    return run_refusing(lambda: _sign(options), "health-role-invalid")


if __name__ == "__main__":
    sys.exit(main())

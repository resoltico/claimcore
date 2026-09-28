"""One independently held role signs only after its own local source observations pass."""

import argparse
import base64
import json
import sys

from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_inspection import (
    inspect_archive,
    inspect_checkpoint,
    inspect_restore,
)
from backup_health_source_io import _read, create_private, role_public_key
from deployment_common import DeploymentRefusal, sign


def main():
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument(
        "--role", required=True, choices=("archive", "checkpoint", "test-restore")
    )
    parser.add_argument("--policy", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--private-key", required=True)
    parser.add_argument("--signature-output", required=True)
    options = parser.parse_args()
    try:
        policy = parse_policy(_read(options.policy, 65536))
        source = parse_source(_read(options.source, 131072), policy)
        role = next(item for item in policy["roles"] if item["role"] == options.role)
        require_key = base64.b64decode(role["publicKeyBase64"], validate=True)
        if role_public_key(options.private_key) != require_key:
            raise DeploymentRefusal("health-role-key-mismatch")
        if options.role == "archive":
            inspect_archive(source, policy)
        elif options.role == "checkpoint":
            inspect_checkpoint(source, policy)
        else:
            inspect_restore(source, policy)
        signed = sign(source, options.private_key)
        signature = base64.b64decode(signed["signatureBase64"], validate=True)
        create_private(options.signature_output, signature, 64)
        print("backup-health-role=SIGNED_" + options.role.upper().replace("-", "_"))
        return 0
    except (DeploymentRefusal, ValueError, KeyError, TypeError, StopIteration) as error:
        reason = (
            str(error)
            if isinstance(error, DeploymentRefusal)
            else "health-role-invalid"
        )
        print(
            json.dumps({"status": "REFUSED", "reason": reason}, separators=(",", ":"))
        )
        return 3


if __name__ == "__main__":
    sys.exit(main())

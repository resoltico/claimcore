"""Prepare create-only canonical source bytes; independent role hosts must still inspect/sign."""

import argparse
import json
import sys

from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_io import _read, create_private
from deployment_common import DeploymentRefusal, canonical


def main():
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--policy", required=True)
    parser.add_argument("--draft", required=True)
    parser.add_argument("--output", required=True)
    options = parser.parse_args()
    try:
        policy = parse_policy(_read(options.policy, 65536))
        draft = json.loads(_read(options.draft, 131072))
        source = canonical(draft)
        parse_source(source, policy)
        create_private(options.output, source, 131072)
        print("backup-health-source=PREPARED_UNVERIFIED")
        return 0
    except (DeploymentRefusal, ValueError, KeyError, TypeError) as error:
        reason = (
            str(error)
            if isinstance(error, DeploymentRefusal)
            else "health-source-invalid"
        )
        print(
            json.dumps({"status": "REFUSED", "reason": reason}, separators=(",", ":"))
        )
        return 3


if __name__ == "__main__":
    sys.exit(main())

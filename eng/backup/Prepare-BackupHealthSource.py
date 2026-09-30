"""Prepare create-only canonical source bytes; independent role hosts must still inspect/sign."""

import argparse
import json
import sys

from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_io import create_private, read_private
from deployment_common import canonical
from health_cli import POLICY_LIMIT, SOURCE_LIMIT, run_refusing


def _prepare(options: argparse.Namespace) -> str:
    policy = parse_policy(read_private(options.policy, POLICY_LIMIT))
    draft = json.loads(read_private(options.draft, SOURCE_LIMIT))
    source = canonical(draft)
    parse_source(source, policy)
    create_private(options.output, source, SOURCE_LIMIT)
    return "backup-health-source=PREPARED_UNVERIFIED"


def main() -> int:
    """Prepare the canonical source bytes that role hosts inspect and sign."""
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--policy", required=True)
    parser.add_argument("--draft", required=True)
    parser.add_argument("--output", required=True)
    options = parser.parse_args()
    return run_refusing(lambda: _prepare(options), "health-source-invalid")


if __name__ == "__main__":
    sys.exit(main())

"""Shared refusal handling for the backup-health command-line tools."""

import json
import sys
from collections.abc import Callable

from deployment_common import DeploymentRefusalError

REFUSED_EXIT = 3
POLICY_LIMIT = 65536
SOURCE_LIMIT = 131072
FENCE_LIMIT = 8192
CERTIFICATE_LIMIT = 65536
SIGNATURE_LIMIT = 64


def run_refusing(action: Callable[[], str], invalid: str) -> int:
    """Run `action`, print its success line, or print a safe refusal and return exit 3."""
    try:
        message = action()
    except (DeploymentRefusalError, ValueError, KeyError, TypeError, StopIteration) as error:
        reason = str(error) if isinstance(error, DeploymentRefusalError) else invalid
        sys.stdout.write(
            (json.dumps({"status": "REFUSED", "reason": reason}, separators=(",", ":"))) + "\n"
        )
        return REFUSED_EXIT
    sys.stdout.write(message + "\n")
    return 0

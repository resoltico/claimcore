"""Prepare a short-lived exact candidate; the independent CHECKPOINT holder signs later."""

import argparse
import json
import sys
from datetime import UTC, datetime

from backup_health_certificate_source import CertificateRequest, candidate
from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_io import create_private, read_private
from deployment_common import canonical, refuse, utc
from health_cli import CERTIFICATE_LIMIT, FENCE_LIMIT, POLICY_LIMIT, SOURCE_LIMIT, run_refusing


def _prepare(options: argparse.Namespace) -> str:
    policy = parse_policy(read_private(options.policy, POLICY_LIMIT))
    source = parse_source(read_private(options.source, SOURCE_LIMIT), policy)
    fence_bytes = read_private(options.writer_fence, FENCE_LIMIT)
    fence = json.loads(fence_bytes)
    if fence_bytes != canonical(fence):
        refuse("health-certificate-fence-canonical")
    request = CertificateRequest(
        options.signer_key_id,
        options.signer_holder_actor_id,
        fence,
        utc(datetime.now(UTC)),
    )
    create_private(options.output, canonical(candidate(source, policy, request)), CERTIFICATE_LIMIT)
    return "backup-health-certificate=CANDIDATE_UNISSUED"


def main() -> int:
    """Prepare the short-lived candidate certificate for the CHECKPOINT holder."""
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--policy", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--writer-fence", required=True)
    parser.add_argument("--signer-key-id", required=True)
    parser.add_argument("--signer-holder-actor-id", required=True)
    parser.add_argument("--output", required=True)
    options = parser.parse_args()
    return run_refusing(lambda: _prepare(options), "health-certificate-invalid")


if __name__ == "__main__":
    sys.exit(main())

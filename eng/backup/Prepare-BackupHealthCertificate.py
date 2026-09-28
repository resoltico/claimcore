"""Prepare a short-lived exact candidate; the independent CHECKPOINT holder signs later."""

import argparse
import json
import sys
from datetime import datetime, timezone

from backup_health_certificate_source import candidate
from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_io import _read, create_private
from deployment_common import DeploymentRefusal, canonical, utc


def main():
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--policy", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--writer-fence", required=True)
    parser.add_argument("--signer-key-id", required=True)
    parser.add_argument("--signer-holder-actor-id", required=True)
    parser.add_argument("--output", required=True)
    options = parser.parse_args()
    try:
        policy = parse_policy(_read(options.policy, 65536))
        source = parse_source(_read(options.source, 131072), policy)
        fence_bytes = _read(options.writer_fence, 8192)
        fence = json.loads(fence_bytes)
        if fence_bytes != canonical(fence):
            raise DeploymentRefusal("health-certificate-fence-canonical")
        checked_at = utc(datetime.now(timezone.utc))
        document = candidate(
            source,
            policy,
            options.signer_key_id,
            options.signer_holder_actor_id,
            fence,
            checked_at,
        )
        create_private(options.output, canonical(document), 65536)
        print("backup-health-certificate=CANDIDATE_UNISSUED")
        return 0
    except (DeploymentRefusal, ValueError, KeyError, TypeError) as error:
        reason = (
            str(error)
            if isinstance(error, DeploymentRefusal)
            else "health-certificate-invalid"
        )
        print(
            json.dumps({"status": "REFUSED", "reason": reason}, separators=(",", ":"))
        )
        return 3


if __name__ == "__main__":
    sys.exit(main())

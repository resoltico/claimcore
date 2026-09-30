"""Independently held CHECKPOINT key signs only a complete three-role source."""

import argparse
import base64
import json
import sys

from backup_health_certificate_source import CertificateRequest, candidate, verify_roles
from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_io import create_private, read_private
from deployment_common import canonical, refuse, sign
from health_cli import CERTIFICATE_LIMIT, POLICY_LIMIT, SIGNATURE_LIMIT, SOURCE_LIMIT, run_refusing


def _sign(options: argparse.Namespace) -> str:
    policy = parse_policy(read_private(options.policy, POLICY_LIMIT))
    source_raw = read_private(options.source, SOURCE_LIMIT)
    source = parse_source(source_raw, policy)
    verify_roles(
        source_raw,
        policy,
        {
            "archive": options.archive_signature,
            "checkpoint": options.checkpoint_signature,
            "test-restore": options.test_restore_signature,
        },
    )
    candidate_raw = read_private(options.candidate, CERTIFICATE_LIMIT)
    document = json.loads(candidate_raw)
    if candidate_raw != canonical(document):
        refuse("health-certificate-canonical")
    expected = candidate(
        source,
        policy,
        CertificateRequest(
            document["signerKeyId"],
            document["signerHolderActorId"],
            document["writerFence"],
            document["checkedAt"],
        ),
    )
    if document != expected:
        refuse("health-certificate-source-diverged")
    signed = sign(document, options.private_key)
    create_private(
        options.signature_output,
        base64.b64decode(signed["signatureBase64"], validate=True),
        SIGNATURE_LIMIT,
    )
    return "backup-health-certificate=SIGNED_PENDING_OWNER_ISSUANCE"


def main() -> int:
    """Sign the certificate once the complete three-role source verifies."""
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--policy", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--archive-signature", required=True)
    parser.add_argument("--checkpoint-signature", required=True)
    parser.add_argument("--test-restore-signature", required=True)
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--private-key", required=True)
    parser.add_argument("--signature-output", required=True)
    options = parser.parse_args()
    return run_refusing(lambda: _sign(options), "health-certificate-invalid")


if __name__ == "__main__":
    sys.exit(main())

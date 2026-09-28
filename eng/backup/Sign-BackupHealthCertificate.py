"""Independently held CHECKPOINT key signs only a complete three-role source."""

import argparse
import base64
import json
import sys

from backup_health_certificate_source import candidate, verify_roles
from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_io import _read, create_private
from deployment_common import DeploymentRefusal, canonical, sign


def main():
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
    try:
        policy = parse_policy(_read(options.policy, 65536))
        source_raw = _read(options.source, 131072)
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
        candidate_raw = _read(options.candidate, 65536)
        document = json.loads(candidate_raw)
        if candidate_raw != canonical(document):
            raise DeploymentRefusal("health-certificate-canonical")
        expected = candidate(
            source,
            policy,
            document["signerKeyId"],
            document["signerHolderActorId"],
            document["writerFence"],
            document["checkedAt"],
        )
        if document != expected:
            raise DeploymentRefusal("health-certificate-source-diverged")
        signed = sign(document, options.private_key)
        create_private(
            options.signature_output,
            base64.b64decode(signed["signatureBase64"], validate=True),
            64,
        )
        print("backup-health-certificate=SIGNED_PENDING_OWNER_ISSUANCE")
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

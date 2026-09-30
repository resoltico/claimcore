#!/usr/bin/env python3
"""Synthetic witnessed publication-root readback and proof-of-possession negatives."""

import base64
import hashlib
import subprocess
import sys
import tempfile
import unittest
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

sys.dont_write_bytecode = True
from backup_types import JsonObject
from deployment_common import DeploymentRefusalError, canonical, utc
from publication_root_record import parse_root_record

RAW_KEY_BYTES = 32


def run(arguments: list[str]) -> bytes:
    result = subprocess.run(
        arguments, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False
    )
    if result.returncode != 0:
        msg = "openssl failed"
        raise RuntimeError(msg)
    return result.stdout


def _make_key(root: Path) -> tuple[Path, bytes]:
    private, public_der = root / "root.key", root / "root.der"
    run(["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(private)])
    run(
        [
            "openssl",
            "pkey",
            "-in",
            str(private),
            "-pubout",
            "-outform",
            "DER",
            "-out",
            str(public_der),
        ]
    )
    return private, public_der.read_bytes()[-RAW_KEY_BYTES:]


def _approvals(now: datetime) -> list[JsonObject]:
    return [
        {
            "approvalId": str(uuid.uuid4()),
            "actorId": str(uuid.uuid4()),
            "role": "OWNER" if number == 1 else "DATA_STEWARD",
            "grantRevision": number,
            "witnessSequence": number,
            "witnessEpoch": 1,
            "witnessHash": f"{number:064x}",
            "candidateSha256": "",
            "approvedAt": utc(now - timedelta(minutes=2)),
            "validUntil": utc(now + timedelta(minutes=2)),
        }
        for number in (1, 2)
    ]


def _sign_candidate(root: Path, private: Path, candidate_bytes: bytes) -> bytes:
    candidate_file, signature_file = root / "candidate.json", root / "candidate.sig"
    candidate_file.write_bytes(candidate_bytes)
    run(
        [
            "openssl",
            "pkeyutl",
            "-sign",
            "-rawin",
            "-inkey",
            str(private),
            "-in",
            str(candidate_file),
            "-out",
            str(signature_file),
        ]
    )
    return signature_file.read_bytes()


def fixture(root: Path, now: datetime) -> tuple[JsonObject, str, JsonObject]:
    private, key = _make_key(root)
    identity = {"installationId": str(uuid.uuid4()), "lineageId": str(uuid.uuid4()), "epoch": 1}
    custodian, registered_event = str(uuid.uuid4()), str(uuid.uuid4())
    approvals = _approvals(now)
    candidate = {
        "format": "claimcore-publication-root-registration-1",
        **identity,
        "rootId": str(uuid.uuid4()),
        "publicKeySha256": hashlib.sha256(key).hexdigest(),
        "custodianActorId": custodian,
        "registeredEventId": registered_event,
        "challengeNonce": "a" * 64,
        "expectedAuthorityRevision": 2,
        "approvalOneId": approvals[0]["approvalId"],
        "approvalTwoId": approvals[1]["approvalId"],
        "validUntil": utc(now + timedelta(minutes=2)),
    }
    candidate_bytes = canonical(candidate)
    signature = _sign_candidate(root, private, candidate_bytes)
    candidate_sha = hashlib.sha256(candidate_bytes).hexdigest()
    for approval in approvals:
        approval["candidateSha256"] = candidate_sha
    nonce = "b" * 64
    record = {
        "format": "claimcore-publication-root-record-1",
        "nonce": nonce,
        **identity,
        "rootId": candidate["rootId"],
        "purpose": "PUBLICATION_ROOT",
        "publicKeyBase64": base64.b64encode(key).decode("ascii"),
        "publicKeySha256": candidate["publicKeySha256"],
        "custodianActorId": custodian,
        "registrationCandidateBase64": base64.b64encode(candidate_bytes).decode("ascii"),
        "registrationCandidateSha256": candidate_sha,
        "proofOfPossessionSignatureBase64": base64.b64encode(signature).decode("ascii"),
        "registeredEventId": registered_event,
        "registeredWitnessSequence": 3,
        "registeredWitnessEpoch": 1,
        "registeredWitnessHash": "3" * 64,
        "authorityRevision": 2,
        "approvals": approvals,
        "active": True,
        "retiredSequence": None,
        "registeredAt": utc(now - timedelta(minutes=1)),
        "checkedAt": utc(now),
        "validUntil": utc(now + timedelta(minutes=2)),
    }
    return record, nonce, identity


class PublicationRootRecordTests(unittest.TestCase):
    def test_exact_root_and_approval_refusals(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            root.chmod(0o700)
            now = datetime.now(UTC).replace(microsecond=0)
            record, nonce, identity = fixture(root, now)
            parsed, pem = parse_root_record(canonical(record), nonce, identity, now=now)
            self.assertEqual(parsed["rootId"], record["rootId"])
            self.assertIn(b"BEGIN PUBLIC KEY", pem)
            for changed, category in (
                ({**record, "nonce": "0" * 64}, "root-record-nonce"),
                ({**record, "publicKeySha256": "0" * 64}, "root-key-digest"),
                (
                    {**record, "validUntil": utc(now - timedelta(seconds=1))},
                    "root-record-expired",
                ),
                ({**record, "retiredSequence": 4}, "root-record-authority"),
            ):
                with self.assertRaisesRegex(DeploymentRefusalError, category):
                    parse_root_record(canonical(changed), nonce, identity, now=now)
            reused = [dict(item) for item in record["approvals"]]
            reused[1]["actorId"] = reused[0]["actorId"]
            with self.assertRaisesRegex(DeploymentRefusalError, "root-approval-independence"):
                parse_root_record(
                    canonical({**record, "approvals": reused}), nonce, identity, now=now
                )
            altered = bytearray(base64.b64decode(record["proofOfPossessionSignatureBase64"]))
            altered[0] ^= 1
            with self.assertRaisesRegex(DeploymentRefusalError, "root-proof-of-possession"):
                parse_root_record(
                    canonical(
                        {
                            **record,
                            "proofOfPossessionSignatureBase64": base64.b64encode(altered).decode(
                                "ascii"
                            ),
                        }
                    ),
                    nonce,
                    identity,
                    now=now,
                )


if __name__ == "__main__":
    unittest.main()

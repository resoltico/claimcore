#!/usr/bin/env python3
"""Synthetic CHECKPOINT signer role/key/identity/replay refusal tests."""

import base64
import hashlib
import subprocess
import sys
import tempfile
import unittest
import uuid
from datetime import datetime, timezone
from pathlib import Path

sys.dont_write_bytecode = True
from checkpoint_signer_policy import configuration
from checkpoint_signer_service import sign_request
from deployment_common import DeploymentRefusal, canonical, utc


def pair(root, name):
    private, public = root / (name + ".key"), root / (name + ".pub")
    for command in (
        ["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(private)],
        ["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)],
    ):
        assert (
            subprocess.run(
                command,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            ).returncode
            == 0
        )
    private.chmod(0o600)
    public.chmod(0o600)
    return private, public


def fixture(root):
    private, public = pair(root, "checkpoint")
    ledger = root / "ledger"
    ledger.mkdir(mode=0o700)
    signer = {
        "format": "claimcore-checkpoint-signer-config-1",
        "purpose": "CHECKPOINT",
        "transport": "LOCAL_SOCKET",
        "socketPath": str(root / "signer.sock"),
        "ledgerRoot": str(ledger),
        "signingKeyFile": str(private),
        "verificationKeyFile": str(public),
        "checkpointSigningKeyId": str(uuid.uuid4()),
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
    }
    path = root / "signer-config.json"
    path.write_bytes(canonical(signer))
    path.chmod(0o600)
    config = configuration(path)
    candidate = {
        "format": "claimcore-witness-checkpoint-1",
        "cycleId": str(uuid.uuid4()),
        "installationId": signer["installationId"],
        "lineageId": signer["lineageId"],
        "epoch": 1,
        "sequence": 1,
        "hash": "a" * 64,
        "capturedAt": utc(datetime.now(timezone.utc)),
        "checkpointSigningKeyId": signer["checkpointSigningKeyId"],
        "checkpointCustodianCommitment": "b" * 64,
        "leaseId": None,
        "captureNonce": None,
        "writerGeneration": None,
        "backupCaptureSequence": None,
        "backupCaptureHash": None,
        "maintenanceEvidenceSha256": None,
    }
    return signer, config, candidate, path


def request(candidate, key_id, nonce):
    raw = canonical(candidate)
    return {
        "format": "claimcore-checkpoint-sign-request-1",
        "nonce": nonce,
        "checkpointSigningKeyId": key_id,
        "candidateSha256": hashlib.sha256(raw).hexdigest(),
        "candidateBase64": base64.b64encode(raw).decode("ascii"),
    }


class CheckpointSignerTests(unittest.TestCase):
    def test_exact_replay_wrong_purpose_key_and_identity(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            root.chmod(0o700)
            signer, config, candidate, path = fixture(root)
            first = sign_request(
                config, request(candidate, signer["checkpointSigningKeyId"], "c" * 64)
            )
            replay = sign_request(
                config, request(candidate, signer["checkpointSigningKeyId"], "d" * 64)
            )
            self.assertEqual(first["signatureBase64"], replay["signatureBase64"])
            changed = {**candidate, "hash": "e" * 64}
            with self.assertRaisesRegex(DeploymentRefusal, "checkpoint-cycle-reused"):
                sign_request(
                    config, request(changed, signer["checkpointSigningKeyId"], "f" * 64)
                )
            wrong_identity = {**candidate, "lineageId": str(uuid.uuid4())}
            with self.assertRaisesRegex(
                DeploymentRefusal, "checkpoint-candidate-identity"
            ):
                sign_request(
                    config,
                    request(wrong_identity, signer["checkpointSigningKeyId"], "0" * 64),
                )
            with self.assertRaisesRegex(DeploymentRefusal, "checkpoint-request-key"):
                sign_request(config, request(candidate, str(uuid.uuid4()), "1" * 64))
            wrong_role = {**signer, "purpose": "COPY_ATTESTOR"}
            path.write_bytes(canonical(wrong_role))
            with self.assertRaisesRegex(DeploymentRefusal, "checkpoint-signer-purpose"):
                configuration(path)
            second_private, _ = pair(root, "wrong")
            wrong_key_config = {**config, "signingKeyFile": second_private}
            newer = {**candidate, "cycleId": str(uuid.uuid4())}
            with self.assertRaisesRegex(
                DeploymentRefusal, "checkpoint-signer-key-mismatch"
            ):
                sign_request(
                    wrong_key_config,
                    request(newer, signer["checkpointSigningKeyId"], "2" * 64),
                )


if __name__ == "__main__":
    unittest.main()

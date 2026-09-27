#!/usr/bin/env python3
"""Synthetic signed health-codec positives and stale/forked evidence negatives."""

import base64
import subprocess
import sys
import tempfile
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.dont_write_bytecode = True
from backup_health import parse_health
from deployment_common import DeploymentRefusal, canonical, sign, utc


def keypair(root):
    private, public = root / "checkpoint.key", root / "checkpoint.pub"
    for arguments in (
        ["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(private)],
        ["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)],
    ):
        result = subprocess.run(
            arguments, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False
        )
        assert result.returncode == 0
    private.chmod(0o600)
    public.chmod(0o600)
    return private, public


def health(now):
    recent = utc(now - timedelta(seconds=10))
    base = lambda: {
        "copyId": str(uuid.uuid4()),
        "revision": 2,
        "physicalReceiptSha256": "a" * 64,
        "verifiedAt": recent,
    }
    wal = lambda: {
        "copyIds": [str(uuid.uuid4())],
        "registeredHorizon": "0/100",
        "archiveInspectionSha256": "b" * 64,
        "verifiedAt": recent,
    }
    return {
        "format": "claimcore-backup-health-1",
        "source": "ClaimCore.Database",
        "scope": "synthetic-only",
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "writerGeneration": 2,
        "policyId": "reviewed-recovery-v1",
        "authorityRevision": 7,
        "witnessTipSequence": 20,
        "witnessTipHash": "c" * 64,
        "checkedAt": utc(now),
        "validUntil": utc(now + timedelta(minutes=2)),
        "maximumBackupAgeSeconds": 3600,
        "maximumWalLagSeconds": 60,
        "maximumCheckpointAgeSeconds": 3600,
        "maximumRestoreTestAgeSeconds": 3600,
        "restoreHorizonSeconds": 86400,
        "primarySystemId": "1111111111111111111",
        "primaryTimeline": 1,
        "witnessSystemId": "2222222222222222222",
        "witnessTimeline": 1,
        "primaryBase": base(),
        "witnessBase": base(),
        "primaryWal": wal(),
        "witnessWal": wal(),
        "checkpoint": {
            "sequence": 18,
            "hash": "d" * 64,
            "objectSha256": "e" * 64,
            "verifiedAt": recent,
        },
        "testRestore": {
            "reportSha256": "f" * 64,
            "witnessCutoff": 17,
            "witnessCutoffHash": "0" * 64,
            "verifiedAt": recent,
        },
        "knownCopyInventorySha256": "1" * 64,
        "artifactCutoffSequence": 16,
        "writerFence": {
            "kind": "HANDOFF",
            "handoffId": str(uuid.uuid4()),
            "w1Sequence": 12,
            "w1Hash": "2" * 64,
            "activationSequence": 19,
            "activationHash": "3" * 64,
            "oldGeneration": 1,
            "newGeneration": 2,
        },
        "signerKeyId": str(uuid.uuid4()),
        "signerHolderActorId": str(uuid.uuid4()),
    }


class BackupHealthTests(unittest.TestCase):
    def test_signed_current_health_and_adversarial_fields(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            root.chmod(0o700)
            private, public = keypair(root)
            now = datetime.now(timezone.utc).replace(microsecond=0)
            report = health(now)
            expected = {
                name: report[name]
                for name in ("installationId", "lineageId", "epoch", "writerGeneration")
            }

            def evaluate(document, *, scope="synthetic-only"):
                envelope = sign(document, private)
                signature = base64.b64decode(envelope["signatureBase64"])
                return parse_health(
                    canonical(document),
                    signature,
                    public,
                    expected,
                    now=now + timedelta(seconds=1),
                    required_scope=scope,
                )

            result, digest = evaluate(report)
            self.assertEqual(result["writerFence"]["activationSequence"], 19)
            self.assertEqual(len(digest), 64)
            for changed, category in (
                (
                    {
                        **report,
                        "primaryWal": {
                            **report["primaryWal"],
                            "verifiedAt": utc(now - timedelta(hours=2)),
                        },
                    },
                    "health-wal-stale",
                ),
                (
                    {
                        **report,
                        "primaryWal": {
                            **report["primaryWal"],
                            "copyIds": report["primaryWal"]["copyIds"] * 2,
                        },
                    },
                    "health-wal-evidence",
                ),
                (
                    {
                        **report,
                        "witnessWal": {
                            **report["witnessWal"],
                            "copyIds": [],
                        },
                    },
                    "health-wal-evidence",
                ),
                (
                    {
                        **report,
                        "primaryBase": {
                            **report["primaryBase"],
                            "verifiedAt": utc(now - timedelta(hours=2)),
                        },
                    },
                    "health-base-stale",
                ),
                (
                    {
                        **report,
                        "writerFence": {
                            **report["writerFence"],
                            "activationSequence": None,
                        },
                    },
                    "health-fence-activation",
                ),
                ({**report, "artifactCutoffSequence": 21}, "health-artifact-cutoff"),
                (
                    {**report, "validUntil": utc(now - timedelta(seconds=1))},
                    "health-expired",
                ),
                ({**report, "writerGeneration": 1}, "health-installation"),
            ):
                with self.assertRaisesRegex(DeploymentRefusal, category):
                    evaluate(changed)
            with self.assertRaisesRegex(DeploymentRefusal, "health-trust-unavailable"):
                evaluate(report, scope="full")
            forged_full = {**report, "scope": "full"}
            full_signature = base64.b64decode(
                sign(forged_full, private)["signatureBase64"]
            )
            with self.assertRaisesRegex(DeploymentRefusal, "health-trust-unavailable"):
                parse_health(
                    canonical(forged_full),
                    full_signature,
                    public,
                    expected,
                    now=now,
                    required_scope="full",
                )
            with self.assertRaisesRegex(DeploymentRefusal, "health-trust-mismatch"):
                parse_health(
                    canonical(forged_full),
                    full_signature,
                    public,
                    expected,
                    now=now,
                    required_scope="full",
                    trusted_key_sha256="0" * 64,
                )
            envelope = sign(report, private)
            signature = bytearray(base64.b64decode(envelope["signatureBase64"]))
            signature[0] ^= 1
            with self.assertRaisesRegex(
                DeploymentRefusal, "deployment-signature-invalid"
            ):
                parse_health(
                    canonical(report), bytes(signature), public, expected, now=now
                )
            with self.assertRaisesRegex(DeploymentRefusal, "health-canonical"):
                parse_health(
                    canonical(report) + b"\n",
                    bytes(signature),
                    public,
                    expected,
                    now=now,
                )


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Synthetic signed health-codec positives and stale/forked evidence negatives."""

import base64
import sys
import tempfile
import unittest
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

sys.dont_write_bytecode = True
from backup_health import parse_health
from backup_test_support import keypair
from backup_types import JsonObject
from deployment_common import DeploymentRefusalError, canonical, sign, utc

ACTIVATION = 19
SHA256_LENGTH = 64


def _base(recent: str) -> JsonObject:
    return {
        "copyId": str(uuid.uuid4()),
        "revision": 2,
        "physicalReceiptSha256": "a" * 64,
        "verifiedAt": recent,
    }


def _wal(recent: str) -> JsonObject:
    return {
        "copyIds": [str(uuid.uuid4())],
        "registeredHorizon": "0/100",
        "archiveInspectionSha256": "b" * 64,
        "verifiedAt": recent,
    }


def _checkpoint(recent: str) -> JsonObject:
    return {"sequence": 18, "hash": "d" * 64, "objectSha256": "e" * 64, "verifiedAt": recent}


def _restore(recent: str) -> JsonObject:
    return {
        "reportSha256": "f" * 64,
        "witnessCutoff": 17,
        "witnessCutoffHash": "0" * 64,
        "verifiedAt": recent,
    }


def health(now: datetime) -> JsonObject:
    recent = utc(now - timedelta(seconds=10))
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
        "primaryBase": _base(recent),
        "witnessBase": _base(recent),
        "primaryWal": _wal(recent),
        "witnessWal": _wal(recent),
        "checkpoint": _checkpoint(recent),
        "testRestore": _restore(recent),
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
    def setUp(self) -> None:
        self._directory = tempfile.TemporaryDirectory()
        root = Path(self._directory.name).resolve()
        root.chmod(0o700)
        self.private, self.public = keypair(root, "checkpoint")
        self.now = datetime.now(UTC).replace(microsecond=0)
        self.report = health(self.now)
        self.expected = {
            name: self.report[name]
            for name in ("installationId", "lineageId", "epoch", "writerGeneration")
        }

    def tearDown(self) -> None:
        self._directory.cleanup()

    def _evaluate(
        self, document: JsonObject, *, scope: str = "synthetic-only"
    ) -> tuple[JsonObject, str]:
        signature = base64.b64decode(sign(document, self.private)["signatureBase64"])
        return parse_health(
            canonical(document),
            signature,
            self.public,
            self.expected,
            now=self.now + timedelta(seconds=1),
            required_scope=scope,
        )

    def _stale(self, name: str, **change: object) -> JsonObject:
        return {**self.report, name: {**self.report[name], **change}}

    def test_signed_current_health_is_accepted(self) -> None:
        result, digest = self._evaluate(self.report)
        self.assertEqual(result["writerFence"]["activationSequence"], ACTIVATION)
        self.assertEqual(len(digest), SHA256_LENGTH)

    def test_adversarial_fields_are_refused(self) -> None:
        old = utc(self.now - timedelta(hours=2))
        cases = (
            (self._stale("primaryWal", verifiedAt=old), "health-wal-stale"),
            (
                self._stale("primaryWal", copyIds=self.report["primaryWal"]["copyIds"] * 2),
                "health-wal-evidence",
            ),
            (self._stale("witnessWal", copyIds=[]), "health-wal-evidence"),
            (self._stale("primaryBase", verifiedAt=old), "health-base-stale"),
            (self._stale("writerFence", activationSequence=None), "health-fence-activation"),
            ({**self.report, "artifactCutoffSequence": 21}, "health-artifact-cutoff"),
            ({**self.report, "validUntil": utc(self.now - timedelta(seconds=1))}, "health-expired"),
            ({**self.report, "writerGeneration": 1}, "health-installation"),
        )
        for changed, category in cases:
            with self.assertRaisesRegex(DeploymentRefusalError, category):
                self._evaluate(changed)

    def test_full_scope_requires_the_pinned_trust_key(self) -> None:
        with self.assertRaisesRegex(DeploymentRefusalError, "health-trust-unavailable"):
            self._evaluate(self.report, scope="full")
        forged = {**self.report, "scope": "full"}
        signature = base64.b64decode(sign(forged, self.private)["signatureBase64"])
        with self.assertRaisesRegex(DeploymentRefusalError, "health-trust-unavailable"):
            parse_health(
                canonical(forged),
                signature,
                self.public,
                self.expected,
                now=self.now,
                required_scope="full",
            )
        with self.assertRaisesRegex(DeploymentRefusalError, "health-trust-mismatch"):
            parse_health(
                canonical(forged),
                signature,
                self.public,
                self.expected,
                now=self.now,
                required_scope="full",
                trusted_key_sha256="0" * SHA256_LENGTH,
            )

    def test_tampered_signature_and_encoding_are_refused(self) -> None:
        signature = bytearray(base64.b64decode(sign(self.report, self.private)["signatureBase64"]))
        signature[0] ^= 1
        with self.assertRaisesRegex(DeploymentRefusalError, "deployment-signature-invalid"):
            parse_health(
                canonical(self.report), bytes(signature), self.public, self.expected, now=self.now
            )
        with self.assertRaisesRegex(DeploymentRefusalError, "health-canonical"):
            parse_health(
                canonical(self.report) + b"\n",
                bytes(signature),
                self.public,
                self.expected,
                now=self.now,
            )


if __name__ == "__main__":
    unittest.main()

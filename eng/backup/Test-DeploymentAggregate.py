#!/usr/bin/env python3
"""Synthetic root-pinned six-observer aggregate and forgery negatives."""

import hashlib
import sys
import tempfile
import unittest
from dataclasses import replace
from datetime import UTC, datetime, timedelta
from pathlib import Path
from unittest.mock import patch

sys.dont_write_bytecode = True
from backup_types import JsonObject
from deployment_aggregate import AggregateSubmission, make_aggregate
from deployment_aggregate_fixture import AggregateFixture, document, fixture
from deployment_aggregate_io import write_aggregate
from deployment_aggregate_model import AggregateContext
from deployment_aggregate_verify import verify_aggregate
from deployment_common import DeploymentRefusalError, public_key_identity, sign
from deployment_topology import ROLES, verify_topology
from managed_common import sync_directory

SHA256_LENGTH = 64


PUBLICATION_SHA = "9" * 64
VERIFIER_SHA = "8" * 64


def _context(fx: AggregateFixture) -> AggregateContext:
    return AggregateContext(
        topology=fx.topology,
        topology_sha=fx.topology_sha,
        publication_sha=PUBLICATION_SHA,
        report_sha=fx.report_sha,
        fenced=fx.fenced,
        role_keys=fx.role_keys,
        observer_key=fx.observer_public,
    )


def _submission(
    fx: AggregateFixture,
    *,
    envelopes: dict[str, JsonObject] | None = None,
    observer: JsonObject | None = None,
) -> AggregateSubmission:
    return AggregateSubmission(
        nonce=fx.nonce,
        envelopes=fx.envelopes if envelopes is None else envelopes,
        observer_envelope=sign(fx.observation, fx.observer_private)
        if observer is None
        else observer,
        verifier_binary_sha=VERIFIER_SHA,
    )


class DeploymentAggregateTests(unittest.TestCase):
    def setUp(self) -> None:
        self._directory = tempfile.TemporaryDirectory()
        self.root = Path(self._directory.name).resolve()
        self.root.chmod(0o700)
        self.now = datetime.now(UTC).replace(microsecond=0)
        self.fx = fixture(self.root, self.now)
        self.context = _context(self.fx)

    def tearDown(self) -> None:
        self._directory.cleanup()

    def _make(
        self, submission: AggregateSubmission, *, now: datetime | None = None
    ) -> tuple[bytes, bytes]:
        return make_aggregate(
            self.context,
            submission,
            self.fx.aggregate_private,
            self.fx.aggregate_public,
            now=self.now if now is None else now,
        )

    def _verify(self, raw: bytes, signature: bytes, scope: str) -> tuple[JsonObject, str]:
        return verify_aggregate(
            raw,
            signature,
            self.fx.aggregate_public,
            self.context,
            now=self.now,
            required_scope=scope,
        )

    def _rewrapped_primary(self) -> Path:
        source = (self.root / "primary.pub").read_text().splitlines()
        encoded = "".join(source[1:-1])
        wrapped = self.root / "rewrapped-primary.pub"
        wrapped.write_text(
            source[0]
            + "\n"
            + "\n".join(encoded[i : i + 20] for i in range(0, len(encoded), 20))
            + "\n"
            + source[-1]
            + "\n"
        )
        wrapped.chmod(0o600)
        self.assertNotEqual(wrapped.read_bytes(), (self.root / "primary.pub").read_bytes())
        self.assertEqual(
            public_key_identity(wrapped), public_key_identity(self.root / "primary.pub")
        )
        return wrapped

    def test_topology_rejects_one_key_under_different_pem_wrapping(self) -> None:
        wrapped = self._rewrapped_primary()
        topology = {
            **self.fx.topology,
            "rolePins": [{**pin} for pin in self.fx.topology["rolePins"]],
        }
        pin = next(item for item in topology["rolePins"] if item["role"] == "witness")
        pin["probePublicKeySha256"] = hashlib.sha256(wrapped.read_bytes()).hexdigest()
        config = {**self.fx.config, "roles": {**self.fx.config["roles"]}}
        config["roles"]["witness"] = {**config["roles"]["witness"], "probePublicKey": str(wrapped)}
        paths = document(
            self.root, "topology-reused-key", topology, self.root / "publication-root.key"
        )
        with self.assertRaisesRegex(DeploymentRefusalError, "topology-key-reused"):
            verify_topology(self.fx.root_public, *paths, PUBLICATION_SHA, config, now=self.now)

    def test_aggregate_signer_cannot_reuse_a_role_key(self) -> None:
        wrapped = self._rewrapped_primary()
        topology = {
            **self.fx.topology,
            "deploymentVerifierPublicKeySha256": hashlib.sha256(wrapped.read_bytes()).hexdigest(),
        }
        context = replace(self.context, topology=topology)
        with self.assertRaisesRegex(DeploymentRefusalError, "aggregate-key-reused"):
            make_aggregate(
                context, _submission(self.fx), self.root / "primary.key", wrapped, now=self.now
            )

    def test_output_durability_failure_retains_partial_evidence(self) -> None:
        raw, signature = self._make(_submission(self.fx))
        output, detached = self.root / "aggregate.json", self.root / "aggregate.sig"
        config: JsonObject = {
            "aggregateOutputFile": str(output),
            "aggregateSignatureFile": str(detached),
        }
        with (
            patch("deployment_aggregate_io.sync_directory", side_effect=OSError),
            self.assertRaises(OSError),
        ):
            write_aggregate(config, raw, signature)
        self.assertEqual(output.read_bytes(), raw)
        self.assertFalse(detached.exists())
        with self.assertRaisesRegex(DeploymentRefusalError, "aggregate-output-exists"):
            write_aggregate(config, raw, signature)
        fresh: JsonObject = {
            "aggregateOutputFile": str(self.root / "fresh.json"),
            "aggregateSignatureFile": str(self.root / "fresh.sig"),
        }
        with patch("deployment_aggregate_io.sync_directory", wraps=sync_directory) as synced:
            write_aggregate(fresh, raw, signature)
            self.assertEqual(synced.call_count, 2)

    def test_root_pins_and_six_signatures(self) -> None:
        raw_proof, signature = self._make(_submission(self.fx))
        proof, digest = self._verify(raw_proof, signature, "synthetic-only")
        self.assertFalse(proof["realDataReady"])
        self.assertEqual(len(proof["probes"]), len(ROLES))
        self.assertEqual(len(digest), SHA256_LENGTH)
        with self.assertRaisesRegex(DeploymentRefusalError, "aggregate-probe-expired"):
            self._make(_submission(self.fx), now=self.now + timedelta(seconds=61))
        with self.assertRaisesRegex(DeploymentRefusalError, "aggregate-scope"):
            self._verify(raw_proof, signature, "full")
        bad = bytearray(signature)
        bad[0] ^= 1
        with self.assertRaisesRegex(DeploymentRefusalError, "deployment-signature-invalid"):
            self._verify(raw_proof, bytes(bad), "synthetic-only")

    def test_probe_evidence_negatives(self) -> None:
        primary = {**self.fx.envelopes["primary"]["report"], "available": False}
        unavailable = {**self.fx.envelopes, "primary": sign(primary, self.root / "primary.key")}
        with self.assertRaisesRegex(DeploymentRefusalError, "aggregate-probe-unavailable"):
            self._make(_submission(self.fx, envelopes=unavailable))
        archive_report = self.fx.envelopes["archive"]["report"]
        incomplete = {
            **archive_report,
            "finalWalObjects": archive_report["finalWalObjects"][:-1],
            "finalWalObjectCount": 1,
        }
        changed = {**self.fx.envelopes, "archive": sign(incomplete, self.root / "archive.key")}
        with self.assertRaisesRegex(DeploymentRefusalError, "aggregate-final-wal"):
            self._make(_submission(self.fx, envelopes=changed))
        fence_open = sign({**self.fx.observation, "routeClosed": False}, self.fx.observer_private)
        with self.assertRaisesRegex(DeploymentRefusalError, "old-writer-not-fenced"):
            self._make(_submission(self.fx, observer=fence_open))

    def test_topology_pin_negatives(self) -> None:
        changed_config = {**self.fx.config, "roles": {**self.fx.config["roles"]}}
        changed_config["roles"]["archive"] = {
            **self.fx.config["roles"]["archive"],
            "machineHash": "0" * SHA256_LENGTH,
        }
        paths = (self.root / "topology.json", self.root / "topology.sig")
        with self.assertRaisesRegex(DeploymentRefusalError, "topology-owner-pin"):
            verify_topology(
                self.fx.root_public, *paths, PUBLICATION_SHA, changed_config, now=self.now
            )
        duplicate_verifier = {
            **self.fx.topology,
            "deploymentVerifierPublicKeySha256": self.fx.topology["rolePins"][0][
                "probePublicKeySha256"
            ],
        }
        duplicate_paths = document(
            self.root,
            "topology-duplicate-verifier",
            duplicate_verifier,
            self.root / "publication-root.key",
        )
        with self.assertRaisesRegex(DeploymentRefusalError, "topology-verifier-not-independent"):
            verify_topology(
                self.fx.root_public,
                *duplicate_paths,
                PUBLICATION_SHA,
                self.fx.config,
                now=self.now,
            )


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Synthetic root-pinned six-observer aggregate and forgery negatives."""

import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.dont_write_bytecode = True
from deployment_aggregate import make_aggregate
from deployment_aggregate_fixture import document, fixture
from deployment_aggregate_verify import verify_aggregate
from deployment_common import DeploymentRefusal, sign
from deployment_topology import verify_topology


class DeploymentAggregateTests(unittest.TestCase):
    def test_root_pins_and_six_signatures(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            root.chmod(0o700)
            now = datetime.now(timezone.utc).replace(microsecond=0)
            (
                root_public,
                private,
                public,
                observer_private,
                observer_public,
                config,
                topology,
                topology_sha,
                envelopes,
                role_keys,
                observation,
                fenced,
                nonce,
                report_sha,
            ) = fixture(root, now)
            old_envelope = sign(observation, observer_private)
            arguments = (
                "9" * 64,
                topology_sha,
                report_sha,
                fenced,
                nonce,
                envelopes,
                old_envelope,
                topology,
                role_keys,
                observer_public,
                private,
                public,
                "8" * 64,
            )
            raw_proof, signature = make_aggregate(*arguments, now=now)
            proof, digest = verify_aggregate(
                raw_proof,
                signature,
                public,
                topology,
                topology_sha,
                "9" * 64,
                report_sha,
                fenced,
                role_keys,
                observer_public,
                now=now,
                required_scope="synthetic-only",
            )
            self.assertFalse(proof["realDataReady"])
            self.assertEqual(len(proof["probes"]), 5)
            self.assertEqual(len(digest), 64)
            with self.assertRaisesRegex(DeploymentRefusal, "aggregate-probe-expired"):
                make_aggregate(*arguments, now=now + timedelta(seconds=61))
            unavailable = {
                **envelopes,
                "primary": sign(
                    {**envelopes["primary"]["report"], "available": False},
                    root / "primary.key",
                ),
            }
            changed_arguments = list(arguments)
            changed_arguments[5] = unavailable
            with self.assertRaisesRegex(
                DeploymentRefusal, "aggregate-probe-unavailable"
            ):
                make_aggregate(*changed_arguments, now=now)
            archive_report = envelopes["archive"]["report"]
            incomplete = {
                **archive_report,
                "finalWalObjects": archive_report["finalWalObjects"][:-1],
                "finalWalObjectCount": 1,
            }
            changed_arguments[5] = {
                **envelopes,
                "archive": sign(incomplete, root / "archive.key"),
            }
            with self.assertRaisesRegex(DeploymentRefusal, "aggregate-final-wal"):
                make_aggregate(*changed_arguments, now=now)
            with self.assertRaisesRegex(DeploymentRefusal, "aggregate-scope"):
                verify_aggregate(
                    raw_proof,
                    signature,
                    public,
                    topology,
                    topology_sha,
                    "9" * 64,
                    report_sha,
                    fenced,
                    role_keys,
                    observer_public,
                    now=now,
                    required_scope="full",
                )
            bad = bytearray(signature)
            bad[0] ^= 1
            with self.assertRaisesRegex(
                DeploymentRefusal, "deployment-signature-invalid"
            ):
                verify_aggregate(
                    raw_proof,
                    bytes(bad),
                    public,
                    topology,
                    topology_sha,
                    "9" * 64,
                    report_sha,
                    fenced,
                    role_keys,
                    observer_public,
                    now=now,
                    required_scope="synthetic-only",
                )
            changed_observation = {**observation, "routeClosed": False}
            with self.assertRaisesRegex(DeploymentRefusal, "old-writer-not-fenced"):
                make_aggregate(
                    *arguments[:6],
                    sign(changed_observation, observer_private),
                    *arguments[7:],
                    now=now,
                )
            changed_config = {**config, "roles": {**config["roles"]}}
            changed_config["roles"]["archive"] = {
                **config["roles"]["archive"],
                "machineHash": "0" * 64,
            }
            paths = (root / "topology.json", root / "topology.sig")
            with self.assertRaisesRegex(DeploymentRefusal, "topology-owner-pin"):
                verify_topology(root_public, *paths, "9" * 64, changed_config, now=now)
            duplicate_verifier = {
                **topology,
                "deploymentVerifierPublicKeySha256": topology["rolePins"][0][
                    "probePublicKeySha256"
                ],
            }
            duplicate_paths = document(
                root,
                "topology-duplicate-verifier",
                duplicate_verifier,
                root / "publication-root.key",
            )
            with self.assertRaisesRegex(
                DeploymentRefusal, "topology-verifier-not-independent"
            ):
                verify_topology(
                    root_public, *duplicate_paths, "9" * 64, config, now=now
                )


if __name__ == "__main__":
    unittest.main()

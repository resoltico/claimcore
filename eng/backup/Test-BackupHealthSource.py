"""Synthetic private source/signing positives and missing-object refusals."""

import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

sys.dont_write_bytecode = True
from backup_health_policy import parse_policy
from backup_health_source import parse_source
from backup_health_source_fixture import fixture, key, private
from backup_health_source_inspection import (
    inspect_archive,
    inspect_checkpoint,
    inspect_restore,
)
from deployment_common import DeploymentRefusal, canonical


class BackupHealthSourceTests(unittest.TestCase):
    def test_role_signing_requires_current_physical_files(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw).resolve()
            root.chmod(0o700)
            policy, source, keys, archive = fixture(root)
            self.assertEqual(
                parse_policy(canonical(policy))["policyId"], policy["policyId"]
            )
            self.assertEqual(
                parse_source(canonical(source), policy)["cycleId"], source["cycleId"]
            )
            inspect_archive(source, policy)
            inspect_checkpoint(source, policy)
            inspect_restore(source, policy)
            private(root, "policy.json", canonical(policy))
            private(root, "draft.json", canonical(source))
            prepared = root / "health.source.json"
            result = subprocess.run(
                [
                    sys.executable,
                    "-B",
                    "eng/backup/Prepare-BackupHealthSource.py",
                    "--policy",
                    str(root / "policy.json"),
                    "--draft",
                    str(root / "draft.json"),
                    "--output",
                    str(prepared),
                ],
                cwd=Path(__file__).parents[2],
                capture_output=True,
                check=False,
            )
            self.assertEqual(result.returncode, 0)
            self.assertEqual(prepared.read_bytes(), canonical(source))
            for role, key_path in keys.items():
                output = root / ("health." + role + ".sig")
                signed = subprocess.run(
                    [
                        sys.executable,
                        "-B",
                        "eng/backup/Sign-BackupHealthRole.py",
                        "--role",
                        role,
                        "--policy",
                        str(root / "policy.json"),
                        "--source",
                        str(prepared),
                        "--private-key",
                        str(key_path),
                        "--signature-output",
                        str(output),
                    ],
                    cwd=Path(__file__).parents[2],
                    capture_output=True,
                    check=False,
                )
                self.assertEqual(signed.returncode, 0)
                self.assertEqual(len(output.read_bytes()), 64)
            fence = {
                name: None
                for name in (
                    "handoffId",
                    "w1Sequence",
                    "w1Hash",
                    "activationSequence",
                    "activationHash",
                    "oldGeneration",
                    "newGeneration",
                )
            }
            fence["kind"] = "GENESIS"
            private(root, "fence.json", canonical(fence))
            issuer = key(root, "issuer")
            issuer_id, holder_id = str(uuid.uuid4()), str(uuid.uuid4())
            candidate_path = root / "health.certificate.json"
            prepared_candidate = subprocess.run(
                [
                    sys.executable,
                    "-B",
                    "eng/backup/Prepare-BackupHealthCertificate.py",
                    "--policy",
                    str(root / "policy.json"),
                    "--source",
                    str(prepared),
                    "--writer-fence",
                    str(root / "fence.json"),
                    "--signer-key-id",
                    issuer_id,
                    "--signer-holder-actor-id",
                    holder_id,
                    "--output",
                    str(candidate_path),
                ],
                cwd=Path(__file__).parents[2],
                capture_output=True,
                check=False,
            )
            self.assertEqual(prepared_candidate.returncode, 0)
            signed_candidate = subprocess.run(
                [
                    sys.executable,
                    "-B",
                    "eng/backup/Sign-BackupHealthCertificate.py",
                    "--policy",
                    str(root / "policy.json"),
                    "--source",
                    str(prepared),
                    "--archive-signature",
                    str(root / "health.archive.sig"),
                    "--checkpoint-signature",
                    str(root / "health.checkpoint.sig"),
                    "--test-restore-signature",
                    str(root / "health.test-restore.sig"),
                    "--candidate",
                    str(candidate_path),
                    "--private-key",
                    str(issuer),
                    "--signature-output",
                    str(root / "health.certificate.sig"),
                ],
                cwd=Path(__file__).parents[2],
                capture_output=True,
                check=False,
            )
            self.assertEqual(signed_candidate.returncode, 0)
            self.assertEqual(len((root / "health.certificate.sig").read_bytes()), 64)
            first = source["objects"][0]
            (archive / first["relativePath"]).write_bytes(b"changed")
            with self.assertRaisesRegex(
                DeploymentRefusal, "health-source-archive-object"
            ):
                inspect_archive(source, policy)


if __name__ == "__main__":
    unittest.main()
